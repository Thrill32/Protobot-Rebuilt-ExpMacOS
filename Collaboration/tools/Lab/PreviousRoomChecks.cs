using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task PreviousRoomChecks(string directory, string helper) {
        string root = Path.Combine(directory, "original 0.8"); Directory.CreateDirectory(root);
        string data = Path.Combine(root, "room"), archive = Path.Combine(root, "original.zip");
        var seed = HostedSeed("original-eight"); var asset = Asset("Original 0.8 geometry");
        seed.document.assets = new[] { asset }; seed.document.parts = new[] { Custom("original-custom", asset) }; seed.document.parts[0].version = 1;
        string snapshot = Path.Combine(root, "snapshot.json"); RecoveryFiles.Write(snapshot, new RoomExport { version = "0.8.0-alpha", catalog = seed.catalog, document = seed.document });
        async Task<int> Old(params string[] args) {
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); await output; await errors; return process.ExitCode;
        }
        Check(await Old("init", "--data-dir", data, "--room", seed.room, "--snapshot", snapshot) == 0, "actual original 0.8 initializes a v2 geometry room");
        string ownerSecret = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(data, "owner.json")))["credential"].GetValue<string>();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "serve", "--data-dir", data, "--listen", "http://127.0.0.1:0" }) start.ArgumentList.Add(arg);
        JoinResponse owner; Transaction accepted; string hash;
        using (var process = Process.Start(start)) {
            var errors = process.StandardError.ReadToEndAsync();
            try {
                var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))));
                Check(ready.version == "0.8.0-alpha", "compatibility fixture runs the preserved original 0.8 helper");
                owner = WireJson.Decode<JoinResponse>((await HostingRequest(ready.url, "/rooms/" + seed.room + "/join", method: HttpMethod.Post,
                    body: WireJson.Encode(new JoinRequest { version = "0.8.0-alpha", catalogHash = seed.catalog.hash, ownerCredential = ownerSecret, name = "Original eight owner" }))).bytes);
                accepted = Tx(owner.document, "AddParts", new[] { Cube("original-cube", 7) });
                var receipt = await HostingRequest(ready.url, "/rooms/" + seed.room + "/commit", owner.token, HttpMethod.Post, WireJson.Encode(accepted));
                Check(WireJson.Decode<Receipt>(receipt.bytes).accepted, "original 0.8 accepts a retained personal edit");
                var undo = Tx(owner.document, "Undo", Array.Empty<Part>(), sequence: 2);
                Check(WireJson.Decode<Receipt>((await HostingRequest(ready.url, "/rooms/" + seed.room + "/commit", owner.token, HttpMethod.Post, WireJson.Encode(undo))).bytes).accepted, "original 0.8 persists redo cursor");
                var rejoined = await HostingRequest(ready.url, "/rooms/" + seed.room + "/join", method: HttpMethod.Post,
                    body: WireJson.Encode(new JoinRequest { version = "0.8.0-alpha", catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken }));
                hash = DocumentModel.Hash(WireJson.Decode<JoinResponse>(rejoined.bytes).document);
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } await errors; }
        }
        Check(await Old("backup", "--data-dir", data, "--output", archive) == 0, "original 0.8 produces its own v2 archive");
        using (var store = new RoomStore(data)) {
            var room = new Room(null, storage: store); var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken });
            Check(DocumentModel.Hash(room.Snapshot) == hash && resumed.participant.id == owner.participant.id && room.Outcome(resumed.token, accepted.operationId).accepted,
                "0.9 reopens original 0.8/v2 without resetting document, identity or receipts");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Redo", Array.Empty<Part>(), sequence: resumed.sequence + 1)).accepted && room.GetAsset(resumed.token, asset.hash).Length > 0,
                "original 0.8 geometry and redo remain usable in 0.9");
        }
        string restored = Path.Combine(root, "single restore"); RoomBackup.Restore(archive, restored);
        using (var store = new RoomStore(restored)) Check(DocumentModel.Hash(new Room(null, storage: store).Snapshot) == hash, "0.9 restores actual 0.8/v2 archive through the single-room path");
        string registryRoot = Path.Combine(root, "registry"); RoomRegistry.Initialize(registryRoot);
        using (var registry = new RoomRegistry(Path.Combine(registryRoot, "server.json"))) {
            registry.Restore(archive); var room = registry.Resolve(seed.room);
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken });
            Check(DocumentModel.Hash(room.Snapshot) == hash && resumed.participant.id == owner.participant.id && resumed.participant.role == "owner", "0.8 archive registration preserves archived room identity and owner history");
        }
    }
}
