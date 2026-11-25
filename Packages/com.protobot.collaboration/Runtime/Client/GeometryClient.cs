using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    public sealed partial class CollaborationClient {
        private sealed class GeometryCache {
            public readonly Dictionary<string, GeometryAsset> assets = new Dictionary<string, GeometryAsset>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        }
        public int AppearanceVersion { get; private set; }
        private GeometryCache geometryCache = new GeometryCache();
        private readonly SemaphoreSlim transferGate = new SemaphoreSlim(1, 1);
        private void Cache(GeometryAsset asset, int bytes, GeometryCache cache = null) {
            lock (gate) {
                cache ??= geometryCache; var geometry = cache.assets; var geometrySizes = cache.sizes;
                if (geometry.TryGetValue(asset.hash, out var existing)) {
                    DocumentModel.Require(existing.name == asset.name, "asset-hash-mismatch"); return;
                }
                DocumentModel.Require(geometry.Values.Sum(GeometryTransfer.MaterializedBytes) + GeometryTransfer.MaterializedBytes(asset) <= GeometryTransfer.MaxMaterializedBytes, "asset-materialization-quota");
                DocumentModel.Require(geometry.Count < DocumentModel.MaxAssets && geometrySizes.Values.Sum(v => (long)v) + bytes <= GeometryTransfer.MaxRoomBytes, "asset-client-quota");
                geometry.Add(asset.hash, asset); geometrySizes.Add(asset.hash, bytes);
            }
        }
        private GeometryCache CandidateGeometry(Document value) {
            var cache = new GeometryCache();
            lock (gate) foreach (var reference in DocumentModel.Assets(value))
                if (geometryCache.assets.TryGetValue(reference.hash, out var asset)) {
                    DocumentModel.Require(asset.name == reference.name, "asset-hash-mismatch");
                    Cache(asset, geometryCache.sizes[reference.hash], cache);
                }
            return cache;
        }
        private void HydrateCached(Document value, GeometryCache cache = null) {
            cache ??= geometryCache; var geometry = cache.assets;
            value.assets = DocumentModel.Assets(value).Select(reference => {
                DocumentModel.Require(geometry.TryGetValue(reference.hash, out var asset) && asset.name == reference.name, "missing-asset");
                return asset;
            }).ToArray();
            GeometryTransfer.RequireSceneBudget(value, hash => GeometryTransfer.MaterializedBytes(geometry[hash]));
        }
        // Called on Unity's main thread for a queued revision. The pump downloaded and validated it first.
        public Document Materialize(Document value) {
            lock (gate) { HydrateCached(value); return value; }
        }
        private async Task HydrateAsync(Document value, CancellationToken cancellation, IProgress<ConnectionProgress> progress = null, GeometryCache cache = null) {
            cache ??= geometryCache;
            var references = DocumentModel.Assets(value);
            for (int i = 0; i < references.Length; i++) {
                cancellation.ThrowIfCancellationRequested(); int completed = i;
                progress?.Report(new ConnectionProgress(ConnectionStage.DownloadingGeometry, i, references.Length));
                await ObtainAssetAsync(references[i], cancellation, progress == null ? null :
                    (Action<long, long?, int>)((bytes, length, attempt) => progress.Report(new ConnectionProgress(ConnectionStage.DownloadingGeometry, completed, references.Length, bytes, length, attempt))), progress, cache).ConfigureAwait(false);
                progress?.Report(new ConnectionProgress(ConnectionStage.DownloadingGeometry, i + 1, references.Length));
            }
            lock (gate) HydrateCached(value, cache);
        }
        private async Task ObtainAssetAsync(GeometryAsset reference, CancellationToken cancellation, Action<long, long?, int> progress = null, IProgress<ConnectionProgress> retryProgress = null, GeometryCache cache = null) {
            GeometryTransfer.ValidateReference(reference);
            cache ??= geometryCache;
            await transferGate.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                lock (gate) if (cache.assets.ContainsKey(reference.hash)) {
                    DocumentModel.Require(cache.assets[reference.hash].name == reference.name, "asset-hash-mismatch"); return;
                }
                byte[] bytes = await TransferAsync(HttpMethod.Get, reference.hash, null, cancellation, progress, retryProgress).ConfigureAwait(false);
                var asset = GeometryTransfer.Decode(bytes);
                DocumentModel.Require(asset.hash == reference.hash && asset.name == reference.name, "asset-hash-mismatch");
                Cache(asset, bytes.Length, cache);
            }
            finally { transferGate.Release(); }
        }
        public async Task UploadAssetAsync(GeometryAsset asset, CancellationToken cancellation = default) {
            DocumentModel.Require(IsConnected, "disconnected");
            DocumentModel.Require(Participant?.role != "viewer", "viewer-read-only");
            GeometryTransfer.RequireAppearanceSupport(asset.appearance != null, AppearanceVersion);
            long epoch = ConnectionEpoch;
            await transferGate.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                int maximum = GeometryAssetByteLimit;
                // Large JSON encoding is pure geometry work; never block Unity's UI thread.
                byte[] bytes = await Task.Run(() => GeometryTransfer.Encode(asset, maximum), cancellation).ConfigureAwait(false);
                bool present;
                try { await TransferAsync(HttpMethod.Head, asset.hash, null, cancellation).ConfigureAwait(false); present = true; }
                catch (CollaborationException error) when (error.Code == "missing-asset") { present = false; }
                if (!present) await TransferAsync(HttpMethod.Put, asset.hash, bytes, cancellation).ConfigureAwait(false);
                var decoded = GeometryTransfer.Decode(bytes);
                lock (gate) {
                    DocumentModel.Require(ConnectionEpoch == epoch && IsConnected, "client-connection-changed");
                    Cache(decoded, bytes.Length);
                }
            }
            finally { transferGate.Release(); }
        }
        private async Task<byte[]> PrepareEditPayloadAsync(Transaction transaction, int maximum, CancellationToken cancellation) {
            var assets = DocumentModel.AssetWrites(transaction);
            // Freeze reference-only metadata and check its wire size before uploading any geometry.
            var refs = assets.Select(GeometryTransfer.Reference).ToArray();
            var metadata = new Transaction { protocol = transaction.protocol, generation = transaction.generation, operationId = transaction.operationId,
                sequence = transaction.sequence, kind = transaction.kind, lease = transaction.lease, expectedRevision = transaction.expectedRevision,
                expected = transaction.expected, writes = transaction.writes, deletes = transaction.deletes, expectedChains = transaction.expectedChains,
                chainWrites = transaction.chainWrites, chainDeletes = transaction.chainDeletes, assetWrites = refs };
            byte[] payload;
            try { payload = LocalJson.Encode(metadata, maximum, cancellation); }
            catch (CollaborationException error) when (error.Code == "local-serialization-limit") { throw new CollaborationException("edit-byte-limit"); }
            foreach (var asset in assets) if (GeometryTransfer.HasGeometry(asset)) await UploadAssetAsync(asset, cancellation).ConfigureAwait(false);
            return payload;
        }
        private async Task<byte[]> TransferAsync(HttpMethod method, string hash, byte[] content, CancellationToken cancellation, Action<long, long?, int> progress = null, IProgress<ConnectionProgress> retryProgress = null) {
            DocumentModel.Require(DocumentModel.ValidAssetHash(hash), "invalid-asset-hash");
            long epoch = ConnectionEpoch;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime?.Token ?? CancellationToken.None);
            // Immutable PUT/GET can restart from byte zero safely. No partial server object survives a request.
            return await ConnectionRetry.TransferAsync(async (attempt, requestCancellation) => {
                    progress?.Invoke(0, null, attempt);
                    using var request = new HttpRequestMessage(method, new Uri(endpoint, "rooms/" + room + "/assets/" + hash));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (content != null) request.Headers.ExpectContinue = true;
                    if (content != null) { request.Content = new ByteArrayContent(content); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation).ConfigureAwait(false);
                    NetworkPolicy.CheckProxyResponse(response);
                    DocumentModel.Require(!response.Content.Headers.ContentLength.HasValue || response.Content.Headers.ContentLength <= GeometryAssetByteLimit, "asset-byte-limit");
                    if (response.IsSuccessStatusCode) progress?.Invoke(0, response.Content.Headers.ContentLength, attempt);
                    using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var output = new MemoryStream(); var buffer = new byte[GeometryTransfer.BufferBytes]; int read;
                    int maximum = response.IsSuccessStatusCode && method == HttpMethod.Get ? GeometryAssetByteLimit : 16384;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, requestCancellation).ConfigureAwait(false)) > 0) {
                        DocumentModel.Require(output.Length + read <= maximum, "asset-byte-limit"); output.Write(buffer, 0, read);
                        if (response.IsSuccessStatusCode) progress?.Invoke(output.Length, response.Content.Headers.ContentLength, attempt);
                    }
                    var bytes = output.ToArray();
                    if (!response.IsSuccessStatusCode) {
                        if (method == HttpMethod.Head) {
                            if ((int)response.StatusCode == 409 && !response.Headers.Contains("X-Protobot-Rejection")) throw new CollaborationException("missing-asset");
                            if (response.Headers.Contains("X-Protobot-Rejection")) {
                                var rejected = ConnectionRetry.Rejection(response, Array.Empty<byte>());
                                if ((rejected.Code == "membership-revoked" || rejected.Code == "server-stopping" || rejected.Code == "room-replaced") && ConnectionEpoch == epoch) Freeze(rejected.Code);
                                throw rejected;
                            }
                            // A HEAD error carries no body; authenticate a bounded ordinary request to recover its terminal reason.
                            await Get<WireError>("receipt?operationId=assetAuthorization", requestCancellation).ConfigureAwait(false);
                            throw new CollaborationException("asset-http-" + (int)response.StatusCode);
                        }
                        var rejection = ConnectionRetry.Rejection(response, bytes); string reason = rejection.Code;
                        if ((reason == "membership-revoked" || reason == "server-stopping" || reason == "room-replaced") && ConnectionEpoch == epoch) Freeze(reason);
                        throw rejection;
                    }
                    return bytes;
            }, timeout.Token, retryProgress).ConfigureAwait(false);
        }
    }
}
