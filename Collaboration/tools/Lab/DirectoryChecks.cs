using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task DirectoryChecks(string directory) {
        string root = Path.Combine(directory, "directory"), config = Path.Combine(root, "server.json");
        Check((await RunCli("server-init", "--data-dir", root)).code == 0, "new servers default to hidden discovery");
        var admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(root, "admin.json"));
        // An absent optional field retains the pre-directory visibility contract.
        var legacy = RoomRegistry.Read<ServerConfiguration>(config); legacy.directoryPolicy = null; RecoveryFiles.Write(config, legacy);
        using (var registry = new RoomRegistry(config)) {
            Check(registry.DirectoryPolicy == "hidden", "legacy servers stay private");
            Reject(() => registry.ConfigureDirectory(new ServerAdministration { serverId = admin.serverId, credential = new string('a', 64) }, "public"), "server-admin-required");
            for (int i = 0; i < ServerRooms.DirectoryLimit; i++) { var seed = HostedSeed("room-" + i); seed.password = i % 2 == 0 ? "private-password" : ""; registry.Create(seed); }
        }
        await using (var hidden = new CollaborationServer(null, multiRoomConfig: config)) {
            await hidden.StartAsync(); using var remote = new RemoteRoomDirectory(hidden.Url, network: NetworkPolicy.Direct);
            await RejectAsync(() => remote.ReadAsync(CancellationToken.None), "room-directory-disabled");
            Check((await HostingRequest(hidden.Url, "/rooms")).code == 403, "hidden directory refuses direct unauthenticated lookup");
        }
        Check((await RunCli("server-directory", "--config", config, "--admin-file", Path.Combine(root, "admin.json"), "--directory-policy", "public")).code == 0,
            "operator explicitly enables public discovery offline");
        Check((await RunCli("server-policy", "--config", config, "--admin-file", Path.Combine(root, "admin.json"), "--creation-policy", "open")).code == 0,
            "creation policy can change independently");
        string cache = Path.Combine(directory, "directory-access", "member.json"), identity;
        await using (var server = new CollaborationServer(null, multiRoomConfig: config)) {
            await server.StartAsync(); using var remote = new RemoteRoomDirectory(server.Url, network: NetworkPolicy.Direct);
            var found = await remote.ReadAsync(CancellationToken.None);
            Check(found.rooms.Length == 8 && found.rooms[0].passwordRequired && !found.rooms[1].passwordRequired, "bounded directory distinguishes password requirements");
            string json = WireJson.Text(found);
            Check(json.Length < 4096 && !new[] { "generation", "catalogHash", "owner", "revision", "usage", "credential", "private-password" }.Any(json.Contains), "directory exposes minimal metadata only");
            await using (var proxy = new LoopbackProxy(server.Url)) {
                await proxy.StartAsync(); using var guarded = new RemoteRoomDirectory(proxy.Url, network: NetworkPolicy.Direct);
                proxy.DirectoryBody = json.PadRight(4096, ' '); Check((await guarded.ReadAsync(CancellationToken.None)).rooms.Length == 8, "exact 4 KiB directory accepted");
                proxy.DirectoryBody += " "; await RejectAsync(() => guarded.ReadAsync(CancellationToken.None), "invalid-server-response");
                var invalid = WireJson.Clone(found); invalid.rooms = found.rooms.Concat(new[] { found.rooms[0] }).ToArray();
                proxy.DirectoryBody = WireJson.Text(invalid); await RejectAsync(() => guarded.ReadAsync(CancellationToken.None), "invalid-server-response");
                invalid.rooms = new[] { found.rooms[0], found.rooms[0] }; proxy.DirectoryBody = WireJson.Text(invalid);
                await RejectAsync(() => guarded.ReadAsync(CancellationToken.None), "invalid-server-response");
                invalid.rooms = found.rooms; invalid.serverId = "wrong-server"; proxy.DirectoryBody = WireJson.Text(invalid);
                await RejectAsync(() => guarded.ReadAsync(CancellationToken.None), "invalid-server-response");
                Check(proxy.JoinRequests == 0 && !proxy.Requests.Keys.Any(p => p.StartsWith("/admin", StringComparison.Ordinal)), "discovery never admits membership or falls back to admin listing");
            }
            Check(server.Registry.Resolve("room-0").Memberships(HostedSeed("room-0").ownerCredential).memberships.Length == 0,
                "listing creates no membership");
            Check((await HostingRequest(server.Url, "/admin/rooms")).code != 200 && (await HostingRequest(server.Url, "/admin/diagnostics")).code != 200,
                "public discovery grants no administrator authority");
            Check((await RunCli("server-directory", "--config", config, "--admin-file", Path.Combine(root, "admin.json"), "--directory-policy", "hidden")).code != 0, "live policy mutation refuses");
            using var first = new CollaborationClient(server.Url, "room-0", HostedSeed("room-0").catalog, recoveryFile: cache);
            await RejectAsync(() => first.JoinAsync("Wrong password"), "wrong-password");
            await first.JoinAsync("Remembered viewer", "private-password", viewer: true); identity = first.Participant.id;
            using var peer = new CollaborationClient(server.Url, "room-0", HostedSeed("room-0").catalog, CollaborationTransport.ShortPolling);
            await peer.JoinAsync("Independent editor", "private-password");
            Check(peer.Participant.id != identity && peer.Participant.role == "editor", "two independent players still require admission");
            Check(CollaborationClient.HasSavedAccess(cache, server.Url, "room-0", first.Snapshot.catalogHash), "reusable participant credential retained privately");
            Check(CollaborationClient.SavedRooms(Path.GetDirectoryName(cache), server.Url, first.Snapshot.catalogHash).SequenceEqual(new[] { "room-0" }), "saved-room metadata scoped to origin and catalog");
            Check(CollaborationClient.SavedRooms(Path.GetDirectoryName(cache), "https://elsewhere.example", first.Snapshot.catalogHash).Length == 0, "access never crosses servers");
            first.Dispose();
            using var resumed = new CollaborationClient(server.Url, "room-0", HostedSeed("room-0").catalog, recoveryFile: cache);
            await resumed.JoinAsync("Rejoin without password");
            Check(resumed.Participant.id == identity && resumed.Participant.role == "viewer", "password-free rejoin retains identity and viewer permission");
            resumed.Dispose();
            byte[] savedBytes = File.ReadAllBytes(cache); var held = System.Text.Json.Nodes.JsonNode.Parse(savedBytes);
            held["pendingIds"] = new System.Text.Json.Nodes.JsonArray("unresolved-edit"); File.WriteAllText(cache, held.ToJsonString());
            Reject(() => CollaborationClient.ForgetRecovery(cache), "saved-access-has-pending-edits");
            Check(File.Exists(cache), "Forget retains access needed to settle pending edit outcomes"); File.WriteAllBytes(cache, savedBytes);
            CollaborationClient.ForgetRecovery(cache); Check(!File.Exists(cache), "Forget deletes only selected participant access");
            using var forgotten = new CollaborationClient(server.Url, "room-0", HostedSeed("room-0").catalog, recoveryFile: cache);
            await RejectAsync(() => forgotten.JoinAsync("Forgotten"), "wrong-password");
            Check(File.Exists(Path.Combine(root, "rooms", "room-0", "owner.json")), "Forget preserves private ownership");
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Canceled(remote.ReadAsync(canceled.Token));
        }
        // Unavailable registrations are absent from public metadata, retained for admin repair.
        File.Move(Path.Combine(root, "rooms", "room-7", "room.sqlite"), Path.Combine(directory, "unavailable.sqlite"));
        await using (var restarted = new CollaborationServer(null, multiRoomConfig: config)) {
            await restarted.StartAsync(); using var remote = new RemoteRoomDirectory(restarted.Url, network: NetworkPolicy.Direct);
            Check((await remote.ReadAsync(CancellationToken.None)).rooms.Length == 7 && restarted.Registry.List().rooms.Length == 8, "unavailable rooms hidden from discovery, retained in admin list after restart");
        }
        var oldSeed = HostedSeed("old-server");
        await using (var dedicated = new CollaborationServer(oldSeed)) {
            await dedicated.StartAsync(); using var remote = new RemoteRoomDirectory(dedicated.Url, network: NetworkPolicy.Direct);
            await RejectAsync(() => remote.ReadAsync(CancellationToken.None), "room-directory-unavailable");
            using var direct = new CollaborationClient(dedicated.Url, oldSeed.room, oldSeed.catalog); await direct.JoinAsync("Direct ID", oldSeed.password);
            Check(direct.IsConnected, "no-directory servers still support direct room-ID joining");
        }
        string original = Environment.GetEnvironmentVariable("PROTOBOT_TEST_DIRECTORY_LEGACY");
        if (!string.IsNullOrEmpty(original)) {
            var start = new System.Diagnostics.ProcessStartInfo(original, "serve --bootstrap-stdin --ephemeral --listen http://127.0.0.1:0") {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var child = System.Diagnostics.Process.Start(start); var errors = child.StandardError.ReadToEndAsync();
            try {
                await child.StandardInput.WriteLineAsync(WireJson.Text(oldSeed)); await child.StandardInput.FlushAsync();
                var ready = WireJson.Decode<Ready>(System.Text.Encoding.UTF8.GetBytes(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))));
                using var remote = new RemoteRoomDirectory(ready.url, network: NetworkPolicy.Direct);
                await RejectAsync(() => remote.ReadAsync(CancellationToken.None), "room-directory-unavailable");
                using var direct = new CollaborationClient(ready.url, oldSeed.room, oldSeed.catalog); await direct.JoinAsync("Original helper direct join", oldSeed.password);
                Check(direct.IsConnected, "retained pre-directory helper supports direct ID admission with updated client");
            } finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } await errors; }
        }
    }
}
