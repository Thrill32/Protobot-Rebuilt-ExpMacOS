using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static GeometryAsset LargeAsset(string name = "Large transfer mesh") {
        var random = new Random(773);
        var asset = new GeometryAsset { name = name, vertices = Enumerable.Range(0, 270000).Select(_ => (double)(float)(random.NextDouble() * 100)).ToArray(),
            triangles = Enumerable.Range(0, 90000).ToArray() };
        asset.hash = DocumentModel.GeometryHash(asset); return asset;
    }
    private static async Task AssetChecks(string directory) {
        var timer = Stopwatch.StartNew(); long allocated = GC.GetTotalAllocatedBytes();
        var seed = Fixture(); var asset = LargeAsset(); byte[] bytes = GeometryTransfer.Encode(asset);
        Check(LocalJson.Encode(asset, bytes.Length).SequenceEqual(bytes), "exact local serialization byte limit accepts unchanged geometry");
        Reject(() => LocalJson.Encode(asset, bytes.Length - 1), "local-serialization-limit");
        Check(bytes.Length > WireJson.MaxMessageBytes && bytes.Length <= GeometryTransfer.MaxAssetBytes, "representative geometry exceeds old inline and wire limits within the transfer quota");
        Reject(() => WireJson.Decode<GeometryAsset>(bytes), "message-too-large");
        var room = new Room(seed); var owner = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Asset owner", ownerCredential = seed.ownerCredential });
        var viewer = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Viewer", viewer = true });
        Reject(() => room.PutAsset(viewer.token, asset.hash, bytes), "viewer-read-only");
        Reject(() => room.PutAsset("bad", asset.hash, bytes), "invalid-session");
        room.PutAsset(owner.token, asset.hash, bytes); room.PutAsset(owner.token, asset.hash, bytes);
        Check(room.Snapshot.revision == 0 && room.GetAsset(viewer.token, asset.hash).SequenceEqual(bytes), "duplicate uploads are immutable and outside document history; viewers download");
        var reference = GeometryTransfer.Reference(asset);
        Check(room.Commit(owner.token, Tx(room.Snapshot, "AddParts", new[] { Custom("large", asset) }, assets: new[] { reference })).accepted, "stored geometry can be referenced without inline data");
        Check(WireJson.Encode(room.Snapshot).Length < 4096 && DocumentModel.Hash(room.Snapshot) == DocumentModel.Hash(new Document {
            schema = 2, generation = room.Snapshot.generation, catalogHash = seed.catalog.hash, revision = room.Snapshot.revision,
            parts = room.Snapshot.parts, assets = new[] { asset } }), "hydrated and reference snapshots have the same canonical document hash");
        room.RemoveParticipant(owner.token, new RemoveParticipant { participantId = viewer.participant.id });
        Reject(() => room.GetAsset(viewer.token, asset.hash), "membership-revoked");
        var corrupt = LocalJson.Clone(asset); corrupt.vertices[0] += 1;
        Reject(() => room.PutAsset(owner.token, asset.hash, LocalJson.Encode(corrupt)), "asset-hash-mismatch");
        Reject(() => room.GetAsset(owner.token, asset.hash.ToUpperInvariant()), "invalid-asset-hash");
        var absent = Asset("Absent");
        Check(room.Commit(owner.token, Tx(room.Snapshot, "AddParts", new[] { Custom("absent", absent) }, sequence: 2,
            assets: new[] { GeometryTransfer.Reference(absent) })).error == "missing-asset", "missing uploaded geometry refuses the edit atomically");
        var tooManyCopies = Enumerable.Range(0, 55).Select(i => Custom("over-instance-" + i, asset)).ToArray();
        Check(room.Commit(owner.token, Tx(room.Snapshot, "AddParts", tooManyCopies, sequence: 3)).error == "asset-scene-quota" && room.Snapshot.parts.Length == 1,
            "deduplicated copies still count against the live materialization/local-save expansion budget");
        // Retained and unused quotas are separate. Reference each accepted object to free unused capacity.
        var quotaRoom = new Room(seed); var q = quotaRoom.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Quota writer" });
        for (int i = 0; i < GeometryTransfer.MaxUnusedAssets; i++) { var small = Asset("Unused " + i); quotaRoom.PutAsset(q.token, small.hash, GeometryTransfer.Encode(small)); }
        var overflow = Asset("Unused overflow"); Reject(() => quotaRoom.PutAsset(q.token, overflow.hash, GeometryTransfer.Encode(overflow)), "asset-unused-quota");
        // Encoded-byte quotas, independent of the asset-count cap.
        var byteQuota = new Room(seed); var byteOwner = byteQuota.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Byte quota" });
        long total = 0; int retainedCount = 0;
        int unusedCount = (GeometryTransfer.MaxUnusedBytes - 1) / bytes.Length;
        for (int i = 0; i < unusedCount; i++) {
            var unused = new GeometryAsset { name = "Large unused " + i, vertices = asset.vertices, triangles = asset.triangles }; unused.hash = DocumentModel.GeometryHash(unused);
            var encoded = GeometryTransfer.Encode(unused); byteQuota.PutAsset(byteOwner.token, unused.hash, encoded); total += encoded.Length;
        }
        var noSpace = new GeometryAsset { name = "Large overflow", vertices = asset.vertices, triangles = asset.triangles }; noSpace.hash = DocumentModel.GeometryHash(noSpace);
        Reject(() => byteQuota.PutAsset(byteOwner.token, noSpace.hash, GeometryTransfer.Encode(noSpace)),
            GeometryTransfer.MaxUnusedBytes >= GeometryTransfer.MaxRoomBytes ? "asset-room-quota" : "asset-unused-quota");
        foreach (var unused in Enumerable.Range(0, unusedCount).Select(i => { var a = new GeometryAsset { name = "Large unused " + i, vertices = asset.vertices, triangles = asset.triangles }; a.hash = DocumentModel.GeometryHash(a); return a; })) {
            Check(byteQuota.Commit(byteOwner.token, Tx(byteQuota.Snapshot, "AddParts", new[] { Custom("quota-" + retainedCount++, unused) }, sequence: retainedCount,
                assets: new[] { GeometryTransfer.Reference(unused) })).accepted, "referencing a durable upload releases unused-upload capacity");
        }
        while (total + bytes.Length < GeometryTransfer.MaxRoomBytes) {
            var retained = new GeometryAsset { name = "Retained " + retainedCount, vertices = asset.vertices, triangles = asset.triangles }; retained.hash = DocumentModel.GeometryHash(retained);
            var encoded = GeometryTransfer.Encode(retained); byteQuota.PutAsset(byteOwner.token, retained.hash, encoded); total += encoded.Length;
            Check(byteQuota.Commit(byteOwner.token, Tx(byteQuota.Snapshot, "AddParts", new[] { Custom("quota-" + retainedCount++, retained) }, sequence: retainedCount,
                assets: new[] { GeometryTransfer.Reference(retained) })).accepted, "accepted geometry stays retained within the room budget");
        }
        Reject(() => byteQuota.PutAsset(byteOwner.token, noSpace.hash, GeometryTransfer.Encode(noSpace)), "asset-room-quota");
        string failed = Path.Combine(directory, "asset write failure");
        using (var store = new RoomStore(failed)) {
            var durable = new Room(seed, storage: store); var member = durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Failure writer" });
            store.BeforeCommit = stage => { if (stage == "asset") throw new IOException("Injected asset write failure"); };
            Reject(() => durable.PutAsset(member.token, asset.hash, bytes), "room-storage-failed-restart-required");
            Check(!durable.IsHealthy && durable.Snapshot.revision == 0, "failed asset persistence never publishes content or success");
        }
        using (var store = new RoomStore(failed)) {
            var durable = new Room(null, storage: store); var member = durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Restart reader" });
            Reject(() => durable.GetAsset(member.token, asset.hash), "missing-asset");
        }
        string failedEdit = Path.Combine(directory, "asset reference write failure"); JoinResponse referenceOwner;
        using (var store = new RoomStore(failedEdit)) {
            var durable = new Room(seed, storage: store); referenceOwner = durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Reference writer" });
            durable.PutAsset(referenceOwner.token, asset.hash, bytes);
            store.BeforeCommit = stage => { if (stage == "edit") throw new IOException("Injected referencing edit failure"); };
            Reject(() => durable.Commit(referenceOwner.token, Tx(durable.Snapshot, "AddParts", new[] { Custom("failed-reference", asset) }, assets: new[] { reference })), "room-storage-failed-restart-required");
        }
        using (var store = new RoomStore(failedEdit)) {
            var durable = new Room(null, storage: store); var resumed = durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = referenceOwner.resumeToken });
            Check(durable.Snapshot.parts.Length == 0 && durable.GetAsset(resumed.token, asset.hash).SequenceEqual(bytes), "referencing-edit rollback preserves the already durable unused asset");
        }
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling })
            await AssetNetworkChecks(Path.Combine(directory, "assets " + transport), seed, asset, bytes, transport);
        var measurement = new { encodedBytes = bytes.Length, vertices = asset.vertices.Length / 3, triangles = asset.triangles.Length / 3,
            elapsedSeconds = timer.Elapsed.TotalSeconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        File.WriteAllText(Path.Combine(directory, "asset-measurements.json"), System.Text.Json.JsonSerializer.Serialize(measurement));
        Console.WriteLine("Asset measurements: " + System.Text.Json.JsonSerializer.Serialize(measurement));
    }
    private sealed class PausedAssetContent : HttpContent {
        private readonly byte[] bytes;
        public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public PausedAssetContent(byte[] value) { bytes = value; }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context, CancellationToken cancellation) {
            await stream.WriteAsync(bytes.AsMemory(0, 32), cancellation); await stream.FlushAsync(cancellation); Started.TrySetResult(true);
            await Resume.Task.WaitAsync(cancellation); await stream.WriteAsync(bytes.AsMemory(32), cancellation);
        }
    }
    private static async Task AssetNetworkChecks(string path, Bootstrap seed, GeometryAsset asset, byte[] bytes, CollaborationTransport transport) {
        Directory.CreateDirectory(path); string catalog = Path.Combine(path, "catalog.json"), data = Path.Combine(path, "room"); RecoveryFiles.Write(catalog, seed.catalog);
        Check((await RunCli("init", "--data-dir", data, "--room", "team", "--catalog", catalog)).code == 0, "asset room initialized through CLI");
        var ownerSecret = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(data, "owner.json")))["credential"].GetValue<string>();
        string hash; JoinResponse readerIdentity;
        await using (var server = new CollaborationServer(null, dataDirectory: data)) {
            await server.StartAsync();
            using var writer = new CollaborationClient(server.Url, "team", seed.catalog, transport); await writer.JoinAsync("Writer", ownerCredential: ownerSecret);
            // Raw authenticated endpoints verify authorization independently of client UI checks.
            readerIdentity = server.Room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Raw viewer", viewer = true });
            using var http = new HttpClient();
            async Task<string> Put(string token, string target, byte[] payload) {
                using var request = new HttpRequestMessage(HttpMethod.Put, server.Url + "/rooms/team/assets/" + target);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); request.Headers.ExpectContinue = true; request.Content = new ByteArrayContent(payload);
                using var response = await http.SendAsync(request); return await response.Content.ReadAsStringAsync();
            }
            Check((await Put(readerIdentity.token, asset.hash, bytes)).Contains("viewer-read-only"), "HTTP viewer uploads are refused");
            Check((await Put("invalid", asset.hash, bytes)).Contains("invalid-session"), "HTTP asset transfer requires membership");
            var rawEditor = server.Room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Size probe" });
            Check((await Put(rawEditor.token, asset.hash, new byte[GeometryTransfer.MaxAssetBytes + 1])).Contains("asset-byte-limit"), "HTTP asset body limit is enforced independently of ordinary message limits");
            server.Room.Leave(rawEditor.token);
            await using var proxy = new LoopbackProxy(server.Url) { DropNextAssetUploadResponse = true }; await proxy.StartAsync();
            using var throughProxy = new CollaborationClient(proxy.Url, "team", seed.catalog, CollaborationTransport.LongPolling); await throughProxy.JoinAsync("Retry writer");
            await throughProxy.UploadAssetAsync(asset);
            Check(server.Room.GetAsset(readerIdentity.token, asset.hash).SequenceEqual(bytes), "lost upload acknowledgement retries and deduplicates canonical geometry");
            var canceledAsset = Asset("Canceled upload");
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel();
                try { await writer.UploadAssetAsync(canceledAsset, cancellation.Token); throw new Exception("Expected canceled upload"); } catch (OperationCanceledException) { Check(true, "pre-canceled uploads publish no asset"); }
            }
            Reject(() => server.Room.GetAsset(readerIdentity.token, canceledAsset.hash), "missing-asset");
            var interrupted = Asset("Interrupted body"); byte[] interruptedBytes = GeometryTransfer.Encode(interrupted);
            using (var stop = new CancellationTokenSource()) {
                using var body = new PausedAssetContent(interruptedBytes);
                using var request = new HttpRequestMessage(HttpMethod.Put, server.Url + "/rooms/team/assets/" + interrupted.hash) { Content = body };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Room.Join(new JoinRequest { name = "Interrupted writer", catalogHash = seed.catalog.hash }).token);
                var upload = http.SendAsync(request, stop.Token); await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel();
                try { await upload; throw new Exception("Expected interrupted upload"); } catch (OperationCanceledException) { Check(true, "mid-body upload interruption cancels the transfer"); }
            }
            Reject(() => server.Room.GetAsset(readerIdentity.token, interrupted.hash), "missing-asset");
            await writer.UploadAssetAsync(interrupted);
            Check(server.Room.GetAsset(readerIdentity.token, interrupted.hash).SequenceEqual(interruptedBytes), "an interrupted upload can restart without partial content");
            var revoking = server.Room.Join(new JoinRequest { name = "Revoked during upload", catalogHash = seed.catalog.hash });
            var revokedAsset = Asset("Revoked in flight");
            using (var body = new PausedAssetContent(GeometryTransfer.Encode(revokedAsset))) {
                using var request = new HttpRequestMessage(HttpMethod.Put, server.Url + "/rooms/team/assets/" + revokedAsset.hash) { Content = body };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", revoking.token);
                var upload = http.SendAsync(request); await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                server.Room.RemoveParticipant(ownerSecret, new RemoveParticipant { participantId = revoking.participant.id }); body.Resume.TrySetResult(true);
                using var refused = await upload;
                Check((await refused.Content.ReadAsStringAsync()).Contains("membership-revoked"), "membership revocation fences an upload already in flight");
            }
            Reject(() => server.Room.GetAsset(readerIdentity.token, revokedAsset.hash), "missing-asset");
            Check((await writer.SubmitAsync(writer.CreateTransaction("AddParts", new[] { Custom("large", asset) }, assetWrites: new[] { asset }))).accepted, "real client uploads and commits geometry larger than a wire message");
            proxy.ContinueAssetDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); proxy.DropNextAssetDownload = true;
            using var joining = new CollaborationClient(proxy.Url, "team", seed.catalog, CollaborationTransport.LongPolling);
            var join = joining.JoinAsync("Downloading participant"); await proxy.AssetDownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!joining.IsConnected, "editing stays disabled while snapshot geometry is downloading");
            Check((await writer.SubmitAsync(writer.CreateTransaction("AddParts", new[] { Cube("during-download", 12) }))).accepted, "accepted edits continue during another client's asset download");
            proxy.ContinueAssetDownload.TrySetResult(true); await join; await joining.WaitForRevisionAsync(writer.Snapshot.revision);
            Check(DocumentModel.Hash(joining.Snapshot) == DocumentModel.Hash(writer.Snapshot) && joining.Snapshot.assets.Single().vertices.Length == asset.vertices.Length,
                "interrupted download retries and snapshot/event handoff converges across concurrent edits");
            proxy.CorruptAssetDownload = true;
            using (var bad = new CollaborationClient(proxy.Url, "team", seed.catalog, CollaborationTransport.LongPolling)) {
                await RejectAsync(() => bad.JoinAsync("Corrupt download"), "asset-hash-mismatch"); Check(!bad.IsConnected, "corrupt geometry never enables editing");
            }
            proxy.CorruptAssetDownload = false;
            var missing = Asset("Missing remote");
            var absent = new Bootstrap { room = "missing", catalog = seed.catalog, document = new Document { catalogHash = seed.catalog.hash, assets = new[] { GeometryTransfer.Reference(missing) } } };
            Reject(() => new Room(absent), "missing-asset");
            var refTx = writer.CreateTransaction("AddParts", new[] { Custom("copy", asset, 5) }, assetWrites: new[] { GeometryTransfer.Reference(asset) });
            Check((await writer.SubmitAsync(refTx)).accepted && WireJson.Encode(refTx).Length < 4096, "duplicate geometry declarations reuse the stored asset with bounded reference-only commits");
            Check((await writer.SubmitAsync(writer.CreateTransaction("Undo", Array.Empty<Part>()))).accepted &&
                (await writer.SubmitAsync(writer.CreateTransaction("Redo", Array.Empty<Part>()))).accepted, "large geometry personal undo/redo retains the asset");
            hash = DocumentModel.Hash(writer.Snapshot);
            await writer.RemoveParticipantAsync(readerIdentity.participant.id);
            Check((await Put(readerIdentity.token, asset.hash, bytes)).Contains("membership-revoked"), "revoked membership cannot upload even a duplicate");
            using var get = new HttpRequestMessage(HttpMethod.Get, server.Url + "/rooms/team/assets/" + asset.hash);
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readerIdentity.token);
            using var denied = await http.SendAsync(get);
            Check((await denied.Content.ReadAsStringAsync()).Contains("membership-revoked"), "revoked membership cannot download");
        }
        string archive = Path.Combine(path, "assets.zip"), restored = Path.Combine(path, "restored");
        Check((await RunCli("backup", "--data-dir", data, "--output", archive)).code == 0 &&
            (await RunCli("restore", "--input", archive, "--data-dir", restored)).code == 0, "offline v2 backup/restore carries all retained geometry in SQLite");
        await using (var restarted = new CollaborationServer(null, dataDirectory: restored)) {
            await restarted.StartAsync(); using var reader = new CollaborationClient(restarted.Url, "team", seed.catalog, transport); await reader.JoinAsync("Restored reader", viewer: true);
            Check(DocumentModel.Hash(reader.Snapshot) == hash && reader.Snapshot.assets.Single().vertices.SequenceEqual(asset.vertices), "restart and archive restoration hydrate exact geometry and canonical accepted state");
            Reject(() => restarted.Room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = readerIdentity.resumeToken }), "membership-revoked");
        }
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(restored, "room.sqlite") + ";Pooling=False")) {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM assets"; command.ExecuteNonQuery();
        }
        using (var store = new RoomStore(restored)) Reject(() => new Room(null, storage: store), "room-storage-corrupt");
    }
}
