using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task LegacyGeometryChecks(string directory, string helper) {
        string root = Path.Combine(directory, "original 0.7 geometry"); Directory.CreateDirectory(root);
        string data = Path.Combine(root, "room"), archive = Path.Combine(root, "v1.zip"), restored = Path.Combine(root, "restored");
        var seed = Fixture(); seed.document.schema = 1; seed.password = "legacy-password";
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "serve", "--bootstrap-stdin", "--data-dir", data, "--listen", "http://127.0.0.1:0" }) start.ArgumentList.Add(value);
        using var process = Process.Start(start); var errors = process.StandardError.ReadToEndAsync();
        JoinResponse owner, revoked; Document original = seed.document; Transaction accepted; string legacyHash; long sequence = 0;
        try {
            await process.StandardInput.WriteLineAsync(WireJson.Text(seed)); await process.StandardInput.FlushAsync();
            string line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var ready = JsonNode.Parse(line); Check(ready["version"].GetValue<string>() == "0.7.0-alpha", "compatibility fixture uses the actual original 0.7 executable");
            string url = ready["url"].GetValue<string>(); using var http = new HttpClient();
            async Task<T> Post<T>(string action, object body, string token = null) {
                using var request = new HttpRequestMessage(HttpMethod.Post, url + "/rooms/team/" + action);
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                // Serialize the concrete DTO (WireJson's generic serializer must see its actual type).
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body, body.GetType(), new System.Text.Json.JsonSerializerOptions { IncludeFields = true }), Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request); var bytes = await response.Content.ReadAsByteArrayAsync();
                Check(response.IsSuccessStatusCode, "original helper accepts fixture " + action); return WireJson.Decode<T>(bytes);
            }
            owner = await Post<JoinResponse>("join", new JoinRequest { version = "0.7.0-alpha", catalogHash = seed.catalog.hash, name = "Legacy owner", ownerCredential = seed.ownerCredential });
            revoked = await Post<JoinResponse>("join", new JoinRequest { version = "0.7.0-alpha", catalogHash = seed.catalog.hash, name = "Legacy viewer", password = seed.password, viewer = true });
            async Task<Receipt> Commit(Transaction tx) {
                var receipt = await Post<Receipt>("commit", tx, owner.token); Check(receipt.accepted, "original helper accepts legacy geometry/history edit");
                using var request = new HttpRequestMessage(HttpMethod.Get, url + "/rooms/team/events?after=" + original.revision + "&transient=-1");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.token);
                using var response = await http.SendAsync(request); var edit = WireJson.Decode<EventBatch>(await response.Content.ReadAsByteArrayAsync()).edits.Single();
                original = DocumentModel.Reduce(original, edit.transaction, seed.catalog); Check(DocumentModel.Hash(original) == edit.hash, "new code verifies original 0.7 canonical hashes before migration");
                return receipt;
            }
            var asset = Asset("Original inline geometry");
            accepted = Tx(original, "AddParts", new[] { Cube("a"), Cube("b", 3), Cube("c", 6), Custom("legacy-custom", asset) }, sequence: ++sequence,
                chains: new[] { new ChainRecord { id = "legacy-route", members = new[] { "c", "a", "b" }, routeVersion = 1, wraps = new[] { 0, 0, 1 } } }, assets: new[] { asset });
            await Commit(accepted);
            var layout = WireJson.Clone(original.parts.Single(p => p.id == "legacy-custom"));
            layout.mounts = layout.mounts.Concat(new[] { new CustomMount { position = new[] { 1.0, 0.0, 0.0 }, diameter = .25, depth = .125 } }).Reverse().ToArray();
            var lease = await Post<LeaseResponse>("lease", new LeaseRequest { ids = new[] { layout.id } }, owner.token);
            await Commit(Tx(original, "SetCustomMounts", new[] { layout }, lease: lease.token, sequence: ++sequence));
            await Commit(Tx(original, "Undo", Array.Empty<Part>(), sequence: ++sequence));
            await Post<RemovalResult>("participants/remove", new RemoveParticipant { participantId = revoked.participant.id }, owner.token);
            legacyHash = DocumentModel.Hash(original);
        }
        finally {
            process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(process.ExitCode == 0, "original helper stopped cleanly: " + await errors);
        }
        RecoveryFiles.Write(Path.Combine(data, "owner.json"), new LegacyOwner { room = seed.room, credential = seed.ownerCredential });
        async Task<int> OldBackup() {
            using var child = Process.Start(new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "backup", "--data-dir", data, "--output", archive } });
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync(); await output; await error; return child.ExitCode;
        }
        Check(await OldBackup() == 0, "actual original helper creates a v1 backup with identities/revocation/history/assets");
        // Inject a failure immediately before migration commit; original room remains v1 and intact.
        using (var store = new RoomStore(data)) {
            store.BeforeCommit = stage => { if (stage == "migration") throw new IOException("Injected migration rollback"); };
            try { new Room(null, storage: store); throw new Exception("Expected failed migration"); } catch (IOException) { Check(true, "failed migration propagates without publishing a room"); }
        }
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(data, "room.sqlite") + ";Pooling=False")) {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
            Check(Convert.ToInt32(command.ExecuteScalar()) == 1, "failed migration leaves the old storage version intact");
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='assets'"; Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "failed migration leaves no partial asset table");
        }
        Check((await RunCli("restore", "--input", archive, "--data-dir", restored)).code == 0, "v1 archive validates its original hash then migrates in private staging");
        foreach (string path in new[] { data, restored }) {
            using var store = new RoomStore(path); var migrated = new Room(null, storage: store);
            Check(migrated.Snapshot.schema == 2 && migrated.Snapshot.generation == original.generation && migrated.Snapshot.revision == original.revision &&
                migrated.Snapshot.parts.Select(p => p.id).SequenceEqual(original.parts.Select(p => p.id)), "v1 room and backup migrate accepted identities/content without revision changes");
            var resumed = migrated.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken });
            Check(resumed.participant.id == owner.participant.id && resumed.participant.role == "owner", "migration preserves stable owner credentials");
            Reject(() => migrated.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = revoked.resumeToken }), "membership-revoked");
            Check(migrated.Commit(resumed.token, accepted).revision == 1, "migration preserves accepted receipt deduplication");
            Check(migrated.Commit(resumed.token, Tx(migrated.Snapshot, "Redo", Array.Empty<Part>(), sequence: sequence + 1)).accepted &&
                migrated.Snapshot.parts.Single(p => p.id == "legacy-custom").mounts[0].diameter == .25 && migrated.Snapshot.chains.Single().wraps.Contains(1),
                "migration preserves personal redo cursor, ordered mounts and explicit route wraps");
            Check(GeometryTransfer.Decode(migrated.GetAsset(resumed.token, original.assets.Single().hash)).vertices.SequenceEqual(original.assets.Single().vertices), "migration persists exact canonical legacy geometry");
            Reject(() => migrated.Join(new JoinRequest { version = "0.7.0-alpha", catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken }), "server-version-mismatch");
        }
        // A v1 room can deduplicate enough copies to exceed the new live-instance budget.
        // Refuse the upgrade intact, allowing the operator to reduce it with the original build.
        var overSeed = Fixture(); overSeed.document.schema = 1;
        var overAsset = LargeAsset("Legacy repeated geometry"); overAsset.vertices = overAsset.vertices.Take(21000).ToArray();
        overAsset.triangles = Enumerable.Range(0, 6999).ToArray(); overAsset.hash = DocumentModel.GeometryHash(overAsset);
        overSeed.document.assets = new[] { overAsset };
        overSeed.document.parts = Enumerable.Range(0, 700).Select(i => { var part = Custom("legacy-copy-" + i, overAsset); part.version = 1; return part; }).ToArray();
        string overPath = Path.Combine(root, "over-budget-original");
        var overStart = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "serve", "--bootstrap-stdin", "--data-dir", overPath, "--listen", "http://127.0.0.1:0" }) overStart.ArgumentList.Add(value);
        using (var overProcess = Process.Start(overStart)) {
            var overErrors = overProcess.StandardError.ReadToEndAsync();
            await overProcess.StandardInput.WriteLineAsync(WireJson.Text(overSeed)); await overProcess.StandardInput.FlushAsync();
            Check(await overProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) != null, "original helper accepts the large deduplicated legacy instance fixture");
            overProcess.StandardInput.Close(); await overProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); await overErrors;
            Check(overProcess.ExitCode == 0, "original over-budget room closes normally");
        }
        using (var store = new RoomStore(overPath)) Reject(() => new Room(null, storage: store), "asset-scene-quota");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(overPath, "room.sqlite") + ";Pooling=False")) {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
            Check(Convert.ToInt32(command.ExecuteScalar()) == 1, "an over-quota legacy room remains unmigrated without dropping accepted instances");
        }
        string v2 = Path.Combine(root, "v2.zip"); Check((await RunCli("backup", "--data-dir", restored, "--output", v2)).code == 0, "migrated room can be archived as v2");
        File.WriteAllText(Path.Combine(root, "compatibility.json"), System.Text.Json.JsonSerializer.Serialize(new { oldHash = legacyHash, revision = original.revision, migrated = true }));
    }
    [Serializable] private sealed class LegacyOwner { public int schema = 1; public string room, credential; }
}
