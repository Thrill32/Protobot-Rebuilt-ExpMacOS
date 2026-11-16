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
        private readonly Dictionary<string, GeometryAsset> geometry = new Dictionary<string, GeometryAsset>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> geometrySizes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly SemaphoreSlim transferGate = new SemaphoreSlim(1, 1);
        private void Cache(GeometryAsset asset, int bytes) {
            lock (gate) {
                if (geometry.TryGetValue(asset.hash, out var existing)) {
                    DocumentModel.Require(existing.name == asset.name, "asset-hash-mismatch"); return;
                }
                DocumentModel.Require(geometry.Values.Sum(GeometryTransfer.MaterializedBytes) + GeometryTransfer.MaterializedBytes(asset) <= GeometryTransfer.MaxMaterializedBytes, "asset-materialization-quota");
                DocumentModel.Require(geometry.Count < DocumentModel.MaxAssets && geometrySizes.Values.Sum(v => (long)v) + bytes <= GeometryTransfer.MaxRoomBytes, "asset-client-quota");
                geometry.Add(asset.hash, asset); geometrySizes.Add(asset.hash, bytes);
            }
        }
        private void HydrateCached(Document value) {
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
        private async Task HydrateAsync(Document value, CancellationToken cancellation) {
            foreach (var reference in DocumentModel.Assets(value)) await ObtainAssetAsync(reference, cancellation).ConfigureAwait(false);
            lock (gate) HydrateCached(value);
        }
        private async Task ObtainAssetAsync(GeometryAsset reference, CancellationToken cancellation) {
            GeometryTransfer.ValidateReference(reference);
            await transferGate.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                lock (gate) if (geometry.ContainsKey(reference.hash)) {
                    DocumentModel.Require(geometry[reference.hash].name == reference.name, "asset-hash-mismatch"); return;
                }
                byte[] bytes = await TransferAsync(HttpMethod.Get, reference.hash, null, cancellation).ConfigureAwait(false);
                var asset = GeometryTransfer.Decode(bytes);
                DocumentModel.Require(asset.hash == reference.hash && asset.name == reference.name, "asset-hash-mismatch");
                Cache(asset, bytes.Length);
            }
            finally { transferGate.Release(); }
        }
        public async Task UploadAssetAsync(GeometryAsset asset, CancellationToken cancellation = default) {
            DocumentModel.Require(IsConnected, "disconnected");
            DocumentModel.Require(Participant?.role != "viewer", "viewer-read-only");
            await transferGate.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                byte[] bytes = GeometryTransfer.Encode(asset);
                bool present;
                try { await TransferAsync(HttpMethod.Head, asset.hash, null, cancellation).ConfigureAwait(false); present = true; }
                catch (CollaborationException error) when (error.Code == "missing-asset") { present = false; }
                if (!present) await TransferAsync(HttpMethod.Put, asset.hash, bytes, cancellation).ConfigureAwait(false);
                Cache(GeometryTransfer.Decode(bytes), bytes.Length);
            }
            finally { transferGate.Release(); }
        }
        private async Task<Transaction> PrepareAssetsAsync(Transaction transaction, CancellationToken cancellation) {
            var assets = DocumentModel.AssetWrites(transaction);
            foreach (var asset in assets) if (GeometryTransfer.HasGeometry(asset)) await UploadAssetAsync(asset, cancellation).ConfigureAwait(false);
            // Clone metadata only; large geometry never enters pending operations or ordinary messages.
            var refs = assets.Select(GeometryTransfer.Reference).ToArray();
            // Do not mutate the caller's transaction while converting it to the wire shape.
            var metadata = new Transaction { protocol = transaction.protocol, generation = transaction.generation, operationId = transaction.operationId,
                sequence = transaction.sequence, kind = transaction.kind, lease = transaction.lease, expectedRevision = transaction.expectedRevision,
                expected = transaction.expected, writes = transaction.writes, deletes = transaction.deletes, expectedChains = transaction.expectedChains,
                chainWrites = transaction.chainWrites, chainDeletes = transaction.chainDeletes, assetWrites = refs };
            return WireJson.Clone(metadata);
        }
        private async Task<byte[]> TransferAsync(HttpMethod method, string hash, byte[] content, CancellationToken cancellation) {
            DocumentModel.Require(DocumentModel.ValidAssetHash(hash), "invalid-asset-hash");
            long epoch = ConnectionEpoch;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime?.Token ?? CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            // Immutable PUT/GET can restart from byte zero safely. No partial server object survives a request.
            for (int attempt = 0; ; attempt++) {
                try {
                    using var request = new HttpRequestMessage(method, new Uri(endpoint, "rooms/" + room + "/assets/" + hash));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (content != null) request.Headers.ExpectContinue = true;
                    if (content != null) { request.Content = new ByteArrayContent(content); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    DocumentModel.Require(!response.Content.Headers.ContentLength.HasValue || response.Content.Headers.ContentLength <= GeometryTransfer.MaxAssetBytes, "asset-byte-limit");
                    using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var output = new MemoryStream(); var buffer = new byte[GeometryTransfer.BufferBytes]; int read;
                    int maximum = response.IsSuccessStatusCode && method == HttpMethod.Get ? GeometryTransfer.MaxAssetBytes : 16384;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                        DocumentModel.Require(output.Length + read <= maximum, "asset-byte-limit"); output.Write(buffer, 0, read);
                    }
                    var bytes = output.ToArray();
                    if (!response.IsSuccessStatusCode) {
                        if (method == HttpMethod.Head) {
                            if ((int)response.StatusCode == 409) throw new CollaborationException("missing-asset");
                            // A HEAD error carries no body; authenticate a bounded ordinary request to recover its terminal reason.
                            await Get<WireError>("receipt?operationId=assetAuthorization", timeout.Token).ConfigureAwait(false);
                            throw new CollaborationException("asset-http-" + (int)response.StatusCode);
                        }
                        string reason; try { reason = WireJson.Decode<WireError>(bytes)?.error; } catch { reason = null; }
                        if (reason == "membership-revoked" && ConnectionEpoch == epoch) Freeze(reason);
                        if ((reason == "asset-transfer-busy" || reason == "server-transfer-busy") && attempt < 30) { await Task.Delay(250, timeout.Token).ConfigureAwait(false); continue; }
                        throw new CollaborationException(reason ?? "asset-http-" + (int)response.StatusCode);
                    }
                    return bytes;
                }
                catch (Exception error) when (attempt < 2 && !timeout.IsCancellationRequested && (error is HttpRequestException || error is IOException)) {
                    await Task.Delay(100 * (attempt + 1), timeout.Token).ConfigureAwait(false);
                }
            }
        }
    }
}
