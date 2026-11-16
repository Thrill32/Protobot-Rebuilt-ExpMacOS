using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    [Serializable] private sealed class DiagnosticOwner { public int schema = 1; public string room, credential; }
    private static async Task DiagnosticChecks(string directory) {
        Reject(() => DiagnosticExport.Encode(new DiagnosticReport { schema = 2 }), "invalid-diagnostics");
        Reject(() => DiagnosticExport.Encode(new DiagnosticReport { version = "unrecognized-version" }), "invalid-diagnostics");
        Reject(() => DiagnosticExport.Decode(new byte[DiagnosticExport.MaxBytes + 1]), "invalid-diagnostics");
        var oversized = new DiagnosticReport { events = Enumerable.Range(0, 1000).Select(_ => new DiagnosticEvent { stage = new string('s', 8192), code = "credential=secret" }).ToArray() };
        Check(DiagnosticExport.Decode(DiagnosticExport.Encode(oversized)).events.Length == 32 && DiagnosticExport.Encode(oversized).Length < 8192, "hostile DTO event strings and count are bounded before serialization");
        string root = Path.Combine(directory, "diagnostic registry"); RoomRegistry.Initialize(root);
        string config = Path.Combine(root, "server.json"), adminPath = Path.Combine(root, "admin.json");
        var admin = RoomRegistry.Read<ServerAdministration>(adminPath); var a = HostedSeed("diagnostic-alpha"); var b = HostedSeed("diagnostic-beta");
        string ownerPath = Path.Combine(directory, "diagnostic-owner.json"); RecoveryFiles.Write(ownerPath, new DiagnosticOwner { room = a.room, credential = a.ownerCredential });
        await using (var server = new CollaborationServer(null, multiRoomConfig: config)) {
            server.Registry.Create(a); server.Registry.Create(b); await server.StartAsync();
            using var owner = new CollaborationClient(server.Url, a.room, a.catalog, CollaborationTransport.LongPolling, recoveryFile: Path.Combine(directory, "diagnostic-private-recovery.json"));
            await owner.JoinAsync("Private display name", a.password, ownerCredential: a.ownerCredential);
            using var viewer = new CollaborationClient(server.Url, a.room, a.catalog, CollaborationTransport.LongPolling);
            await viewer.JoinAsync("Private viewer name", a.password, viewer: true);
            var raw = server.Registry.Resolve(a.room).Join(new JoinRequest { catalogHash = a.catalog.hash, password = a.password, name = "Raw editor" });
            foreach (string credential in new[] { null, a.ownerCredential, raw.token, raw.resumeToken })
                Check((await HostingRequest(server.Url, "/admin/diagnostics", credential)).code == (credential == null ? 401 : 403), "only server admin can inspect process/all-room metrics");
            foreach (string credential in new[] { null, admin.credential, b.ownerCredential, raw.token, raw.resumeToken })
                Check((await HostingRequest(server.Url, "/rooms/" + a.room + "/diagnostics", credential)).code == (credential == null ? 401 : 403), "room metrics reject anonymous, admin, other-room and participant credentials");
            try { await viewer.ResourcesAsync(); throw new Exception("Expected owner scope"); } catch (CollaborationException error) { Check(error.Code == "owner-required" && viewer.IsConnected, "viewer inspection refusal preserves its connection"); }
            var caps = await HostingRequest(server.Url, "/capabilities");
            Check(WireJson.Decode<Capabilities>(caps.bytes).diagnosticsVersion == 1 && !Encoding.UTF8.GetString(caps.bytes).Contains("retainedGeometryBytes"), "public discovery advertises only diagnostic version, without metrics");
            var asset = Asset("Private geometry payload name"); byte[] encoded = GeometryTransfer.Encode(asset);
            var room = server.Registry.Resolve(a.room); room.PutAsset(raw.token, asset.hash, encoded);
            var created = await owner.SubmitAsync(owner.CreateTransaction("AddParts", new[] { Custom("diagnostic-custom-a", asset), Custom("diagnostic-custom-b", asset, 4) }, assetWrites: new[] { asset }));
            Check(created.accepted, "diagnostic fixture accepts shared geometry instances");
            var report = await owner.ResourcesAsync(); var usage = report.selectedRoom.usage;
            Check(report.source == "room-owner" && report.rooms.Length == 0 && report.process == null && report.selectedRoom.room == a.room, "owner sees only selected room, never another room or process");
            Check(usage.assets.used == 1 && usage.retainedGeometryBytes.used == encoded.Length && usage.retainedGeometryBytes.limit == GeometryTransfer.MaxRoomBytes &&
                usage.decodedArrayBytes.used == GeometryTransfer.MaterializedBytes(asset) && usage.liveInstanceArrayBytes.used == 2 * GeometryTransfer.MaterializedBytes(asset), "logical retained, decoded and live-instance usage has distinct correct quotas");
            Check(usage.unusedUploads.used == 0 && usage.receipts.used == 1 && usage.historyEntries == 1 && usage.historyBytes.used > 0 && usage.receipts.limit == 20000, "referencing edits update unused/history/receipt counters");
            for (int i = 0; i < GeometryTransfer.MaxUnusedAssets; i++) { var unused = Asset("Unused diagnostic " + i); room.PutAsset(raw.token, unused.hash, GeometryTransfer.Encode(unused)); }
            var excess = Asset("Excess diagnostic"); Reject(() => room.PutAsset(raw.token, excess.hash, GeometryTransfer.Encode(excess)), "asset-unused-quota");
            report = await owner.ResourcesAsync();
            Check(report.selectedRoom.usage.unusedUploads.used == GeometryTransfer.MaxUnusedAssets && report.selectedRoom.usage.unusedUploadBytes.used > 0, "quota refusal leaves reported usage at its exact bound");
            byte[] cacheBefore = File.ReadAllBytes(Path.Combine(directory, "diagnostic-private-recovery.json")); string before = DocumentModel.Hash(owner.Snapshot);
            var local = owner.CaptureDiagnostics();
            Check(local.selectedRoom.documentHash == before && local.selectedRoom.revision == 1 && local.stage == "Ready" && local.events.Any(e => e.stage == "Authenticating") && local.pendingOperations == 0, "client capture includes accepted hash/revision, stage/timing and pending count without copying content");
            string output = Path.Combine(directory, "diagnostic-export.json");
            await DiagnosticExport.WriteAsync(output, local);
            using (var canceled = new CancellationTokenSource()) {
                canceled.Cancel(); string never = Path.Combine(directory, "diagnostic-canceled.json");
                try { await DiagnosticExport.WriteAsync(never, local, canceled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { Check(!File.Exists(never), "pre-cancel never publishes diagnostic output"); }
            }
            try { await DiagnosticExport.WriteAsync(output, report); throw new Exception("Expected exclusive export"); } catch (IOException) { Check(File.ReadAllBytes(output).SequenceEqual(DiagnosticExport.Encode(local)), "failed export cannot overwrite an existing file"); }
            try { await DiagnosticExport.WriteAsync(Path.Combine(directory, "missing diagnostic folder", "report.json"), local); throw new Exception("Expected unwritable destination"); } catch (DirectoryNotFoundException) { Check(true, "export failure reports no implicit directory creation"); }
            Check(owner.IsConnected && DocumentModel.Hash(owner.Snapshot) == before && cacheBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "diagnostic-private-recovery.json"))) &&
                !Directory.EnumerateFiles(directory, "*.tmp").Any(), "export success/cancellation/failure retain scene, cache, membership and pending state without temporary files");
            foreach (string role in new[] { "owner", "admin" }) {
                string cliOutput = Path.Combine(directory, "cli-diagnostics-" + role + ".json");
                var cli = await RunCli("diagnostics", "--url", server.Url, role == "owner" ? "--owner-file" : "--admin-file", role == "owner" ? ownerPath : adminPath, "--output", cliOutput);
                var data = DiagnosticExport.Decode(File.ReadAllBytes(cliOutput));
                Check(cli.code == 0 && (role == "owner" ? data.process == null && data.selectedRoom.room == a.room : data.rooms.Length == 2 && data.process.rooms.used == 2 && data.process.measuredWorkingSetBytes > 0), "actual CLI exports correctly scoped versioned " + role + " diagnostics");
                foreach (string secret in new[] { a.password, a.ownerCredential, b.ownerCredential, admin.credential, raw.token, raw.resumeToken, "Private display name", asset.name, directory, "vertices", "resumeToken" })
                    Check(!cli.output.Contains(secret) && !File.ReadAllText(cliOutput).Contains(secret), "allowlisted CLI diagnostics omit private content/credential/path fields");
            }
            var list = await RunCli("room", "list", "--url", server.Url, "--admin-file", adminPath);
            var listing = WireJson.Decode<HostedRoomList>(Encoding.UTF8.GetBytes(list.output));
            Check(list.code == 0 && listing.rooms.Single(r => r.room == a.room).usage.unusedUploads.used == 16 && listing.process.transfers.limit == 2 && listing.process.connections.used > 0, "actual room list includes protected room/process usage and observed connections");
            await using var proxy = new LoopbackProxy(server.Url); await proxy.StartAsync();
            using var proxied = new CollaborationClient(proxy.Url, a.room, a.catalog, CollaborationTransport.LongPolling);
            await proxied.JoinAsync("Proxied owner", a.password, ownerCredential: a.ownerCredential);
            string reflected = "password=" + a.password + " owner=" + a.ownerCredential + " token=" + raw.token + " /private/source.obj\n" + new string('x', 8192);
            proxy.RejectedDiagnosticsError = reflected;
            try { await proxied.ResourcesAsync(); throw new Exception("Expected reflected error"); } catch (CollaborationException error) { Check(error.Code == "server-rejected-request" && proxied.IsConnected, "malicious reflected diagnostic errors are reduced to a recognized code without freezing connection"); }
            var maliciousCli = await RunCli("diagnostics", "--url", proxy.Url, "--owner-file", ownerPath);
            Check(maliciousCli.code == 1 && !maliciousCli.error.Contains(a.password) && !maliciousCli.error.Contains(a.ownerCredential) && !maliciousCli.error.Contains("/private/"), "CLI diagnostic failure cannot reflect credentials/paths");
            proxy.RejectedJoinError = reflected;
            using var rejected = new CollaborationClient(proxy.Url, a.room, a.catalog, CollaborationTransport.LongPolling);
            try { await rejected.JoinAsync("Rejected", a.password); } catch (CollaborationException) { }
            Check(!Encoding.UTF8.GetString(DiagnosticExport.Encode(rejected.CaptureDiagnostics())).Contains(a.ownerCredential), "failed connection export redacts malicious join errors");
            for (int i = 0; i < 1000; i++) rejected.Suspend(reflected);
            var bounded = rejected.CaptureDiagnostics();
            Check(bounded.events.Length == DiagnosticExport.MaxEvents && DiagnosticExport.Encode(bounded).Length < 8192 && bounded.events.All(e => e.code == "server-rejected-request"), "recent events bound both count and string size despite malicious repeated input");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await owner.ResourcesAsync(stop.Token); } catch (OperationCanceledException) { }
            Check(owner.IsConnected && DocumentModel.Hash(owner.Snapshot) == before, "canceled resource request leaves connection/scene intact");
            var storage = (RoomStore)typeof(Room).GetField("store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(room);
            storage.BeforeCommit = _ => throw new IOException("Private failure /private/source credential=do-not-export");
            Reject(() => room.Commit(raw.token, Tx(room.Snapshot, "AddParts", new[] { Cube("failed-diagnostic-edit") })), "room-storage-failed-restart-required");
            var frozen = await DiagnosticExport.FetchAsync(server.Url, a.room, a.ownerCredential);
            Check(frozen.selectedRoom.status == "room-storage-failed-restart-required" && frozen.selectedRoom.usage != null && frozen.selectedRoom.documentHash == before &&
                !Encoding.UTF8.GetString(DiagnosticExport.Encode(frozen)).Contains("do-not-export"), "frozen room inspection retains last accepted counters/hash with fixed status and no raw storage error");
        }
        await UnavailableDiagnosticChecks(directory);
    }
    private static async Task CurrentDiagnosticCompatibility(string directory, string helper) {
        string root = Path.Combine(directory, "original-nine-diagnostics"); Directory.CreateDirectory(root);
        string data = Path.Combine(root, "room"), archive = Path.Combine(root, "original.zip"), snapshot = Path.Combine(root, "snapshot.json"), cache = Path.Combine(root, "membership.json");
        var seed = HostedSeed("original-nine"); var asset = Asset("Original nine geometry");
        seed.document.assets = new[] { asset }; seed.document.parts = new[] { Custom("original-nine-custom", asset) }; seed.document.parts[0].version = 1;
        RecoveryFiles.Write(snapshot, new RoomExport { catalog = seed.catalog, document = seed.document });
        async Task<int> Old(params string[] arguments) {
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var command = Process.Start(start); var output = command.StandardOutput.ReadToEndAsync(); var errors = command.StandardError.ReadToEndAsync();
            await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); await output; await errors; return command.ExitCode;
        }
        Check(await Old("init", "--data-dir", data, "--room", seed.room, "--snapshot", snapshot) == 0, "preserved original 0.9 creates a real v2 room");
        string credential = RoomRegistry.Read<DiagnosticOwner>(Path.Combine(data, "owner.json")).credential;
        var commandStart = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "serve", "--data-dir", data, "--listen", "http://127.0.0.1:0" }) commandStart.ArgumentList.Add(argument);
        string identity, hash, origin; long sequence;
        using (var process = Process.Start(commandStart)) {
            var errors = process.StandardError.ReadToEndAsync();
            try {
                var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))); origin = ready.url;
                using var client = new CollaborationClient(origin, seed.room, seed.catalog, CollaborationTransport.LongPolling, recoveryFile: cache);
                await client.JoinAsync("Original nine owner", ownerCredential: credential); identity = client.Participant.id;
                Check(ready.version == CollaborationProtocol.Version && !client.SupportsDiagnostics && client.CaptureDiagnostics().selectedRoom.documentHash == DocumentModel.Hash(client.Snapshot), "new client joins original 0.9 without optional metrics capability and can export local metadata");
                Check((await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("original-nine-edit") }))).accepted, "original 0.9 accepts new-client edits without a wire change");
                hash = DocumentModel.Hash(client.Snapshot); sequence = client.Snapshot.revision;
                try { await client.ResourcesAsync(); throw new Exception("Expected unavailable capability"); } catch (CollaborationException error) { Check(error.Code == "diagnostics-unavailable" && client.IsConnected, "old helper metrics remain unavailable without affecting connection"); }
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } await errors; }
        }
        Check(await Old("backup", "--data-dir", data, "--output", archive) == 0, "preserved original 0.9 creates its unchanged v2 backup");
        string restored = Path.Combine(root, "restored"); RoomBackup.Restore(archive, restored);
        await using var server = new CollaborationServer(null, listen: origin, dataDirectory: restored); await server.StartAsync();
        using var resumed = new CollaborationClient(origin, seed.room, seed.catalog, CollaborationTransport.LongPolling, recoveryFile: cache);
        await resumed.JoinAsync("Resumed original nine");
        Check(resumed.Participant.id == identity && DocumentModel.Hash(resumed.Snapshot) == hash && resumed.Snapshot.revision == sequence && resumed.SupportsDiagnostics,
            "diagnostic release restores original 0.9 backup and membership with unchanged accepted content/history");
        Check((await resumed.ResourcesAsync()).selectedRoom.documentHash == hash && (await resumed.SubmitAsync(resumed.CreateTransaction("Undo", Array.Empty<Part>()))).accepted,
            "restored original 0.9 room supports metrics and retained personal undo");
    }
    private static async Task UnavailableDiagnosticChecks(string directory) {
        string root = Path.Combine(directory, "unavailable diagnostic registry"); RoomRegistry.Initialize(root);
        var large = LargeAsset("Diagnostic load budget mesh");
        foreach (string name in new[] { "healthy", "locked", "corrupt", "incompatible", "heavy-a", "heavy-b", "heavy-c" }) {
            var seed = HostedSeed(name); string path = Path.Combine(root, "rooms", name);
            if (name.StartsWith("heavy-", StringComparison.Ordinal)) { seed.document.assets = new[] { large }; seed.document.parts = Enumerable.Range(0, 45).Select(i => { var p = Custom("instance-" + i, large); p.version = 1; return p; }).ToArray(); }
            RecoveryFiles.CreateDirectory(path); RecoveryFiles.Write(Path.Combine(path, "owner.json"), new DiagnosticOwner { room = name, credential = seed.ownerCredential });
            using (var store = new RoomStore(path)) _ = new Room(seed, storage: store);
            RoomRegistry.WriteNew(Path.Combine(path, "registration.json"), new RoomRegistration { room = name, generation = seed.document.generation, catalogHash = seed.catalog.hash, ownerHash = RoomRegistry.Hash(seed.ownerCredential) });
        }
        string corrupt = Path.Combine(root, "rooms", "corrupt", "room.sqlite"), incompatible = Path.Combine(root, "rooms", "incompatible", "room.sqlite");
        File.WriteAllText(corrupt, "Private corrupt payload /private/path credential=never-export");
        using (var db = new SqliteConnection("Data Source=" + incompatible + ";Pooling=False")) { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        byte[] damagedBefore = File.ReadAllBytes(corrupt), incompatibleBefore = File.ReadAllBytes(incompatible);
        using var locked = new FileStream(Path.Combine(root, "rooms", "locked", "room.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var server = new CollaborationServer(null, multiRoomConfig: Path.Combine(root, "server.json")); await server.StartAsync();
        var admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(root, "admin.json"));
        var report = await DiagnosticExport.FetchAsync(server.Url, null, admin.credential);
        foreach (var expected in new[] { ("locked", "room-data-in-use"), ("corrupt", "room-storage-corrupt"), ("incompatible", "room-storage-version-mismatch"), ("heavy-c", "server-geometry-quota") }) {
            var room = report.rooms.Single(r => r.room == expected.Item1);
            Check(room.status == expected.Item2 && room.usage == null && room.revision == null && !string.IsNullOrEmpty(room.action), "unavailable " + expected.Item1 + " has actionable status with unknown usage/revision (got " + room.status + ")");
        }
        using var healthy = new CollaborationClient(server.Url, "healthy", HostedSeed("healthy").catalog, CollaborationTransport.LongPolling);
        await healthy.JoinAsync("Healthy editor", "password-healthy");
        Check((await healthy.SubmitAsync(healthy.CreateTransaction("AddParts", new[] { Cube("still-healthy") }))).accepted && report.process.rooms.used == 7, "healthy room continues editing alongside locked/corrupt/incompatible/over-budget registrations");
        Check(File.ReadAllBytes(corrupt).SequenceEqual(damagedBefore) && File.ReadAllBytes(incompatible).SequenceEqual(incompatibleBefore), "inspection/startup never initializes, migrates, repairs or replaces damaged/incompatible data");
        var cli = await RunCli("diagnostics", "--url", server.Url, "--admin-file", Path.Combine(root, "admin.json"));
        Check(cli.code == 0 && DiagnosticExport.Decode(Encoding.UTF8.GetBytes(cli.output)).rooms.Length == 7 && !cli.output.Contains("never-export"), "actual CLI inspects unavailable rooms without exposing stored corruption text");
    }
}
