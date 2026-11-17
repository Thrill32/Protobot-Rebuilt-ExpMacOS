using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task SimultaneousRoomTransfers(CollaborationServer server, Bootstrap a, Bootstrap b, JoinResponse rawA, JoinResponse rawB,
        CollaborationClient alice, CollaborationClient bob) {
        var assetA = Asset("Paused alpha"); var assetB = Asset("Paused beta");
        using var http = new HttpClient();
        using var bodyA = new PausedAssetContent(GeometryTransfer.Encode(assetA)); using var bodyB = new PausedAssetContent(GeometryTransfer.Encode(assetB));
        using var requestA = new HttpRequestMessage(HttpMethod.Put, server.Url + "/rooms/alpha/assets/" + assetA.hash) { Content = bodyA };
        using var requestB = new HttpRequestMessage(HttpMethod.Put, server.Url + "/rooms/beta/assets/" + assetB.hash) { Content = bodyB };
        requestA.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawA.token); requestB.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawB.token);
        var uploadA = http.SendAsync(requestA); var uploadB = http.SendAsync(requestB);
        await Task.WhenAll(bodyA.Started.Task, bodyB.Started.Task).WaitAsync(TimeSpan.FromSeconds(10));
        try {
            await Until(() => server.Registry.Budget.Transfers.CurrentCount == 0);
            var metricsA = await DiagnosticExport.FetchAsync(server.Url, a.room, a.ownerCredential);
            var metricsB = await DiagnosticExport.FetchAsync(server.Url, b.room, b.ownerCredential);
            Check(metricsA.selectedRoom.usage.transfers.used == 1 && metricsB.selectedRoom.usage.transfers.used == 1 && (alice.Transport == CollaborationTransport.ShortPolling || metricsA.selectedRoom.usage.eventStreams.used > 0), "owner reports observe actual active room transfers and event streams");
            Check(!uploadA.IsCompleted && !uploadB.IsCompleted, "two room uploads run concurrently with independent transfer gates");
            var busy = await HostingRequest(server.Url, "/rooms/alpha/assets/" + assetA.hash, rawA.token, HttpMethod.Put, GeometryTransfer.Encode(assetA));
            Check(Encoding.UTF8.GetString(busy.bytes).Contains("asset-transfer-busy"), "one room has a bounded transfer slot");
            Check((await alice.SubmitAsync(alice.CreateTransaction("AddParts", new[] { Cube("during-alpha-upload", 51) }))).accepted &&
                (await bob.SubmitAsync(bob.CreateTransaction("AddParts", new[] { Cube("during-beta-upload", 61) }))).accepted, "slow uploads hold no edit gate in either room");
            var third = HostedSeed("transfer-third"); server.Registry.Create(third);
            var raw = server.Registry.Resolve(third.room).Join(new JoinRequest { name = "Third", catalogHash = third.catalog.hash, password = third.password });
            var refused = await HostingRequest(server.Url, "/rooms/transfer-third/assets/" + assetA.hash, raw.token, HttpMethod.Put, GeometryTransfer.Encode(assetA));
            Check(Encoding.UTF8.GetString(refused.bytes).Contains("server-transfer-busy"), "process transfer slots bound work across rooms");
        }
        finally { bodyA.Resume.TrySetResult(true); bodyB.Resume.TrySetResult(true); }
        using var doneA = await uploadA; using var doneB = await uploadB;
        Check(doneA.IsSuccessStatusCode && doneB.IsSuccessStatusCode, "simultaneous paused transfers complete independently");
    }
    private static async Task ContendedCreation(CollaborationServer server, ServerAdministration admin, Bootstrap existing, CollaborationClient editor) {
        var seed = HostedSeed("creation-paused");
        using var http = new HttpClient(); using var content = new PausedAssetContent(LocalJson.Encode(seed));
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url + "/admin/rooms") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.credential);
        var creating = http.SendAsync(request); await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try {
            await Until(() => server.Registry.Creation.CurrentCount == 0);
            var denied = await HostingRequest(server.Url, "/admin/rooms", admin.credential, HttpMethod.Post, LocalJson.Encode(existing));
            Check(Encoding.UTF8.GetString(denied.bytes).Contains("server-creation-busy"), "admin creation work is bounded before reading another seed");
            Check((await editor.SubmitAsync(editor.CreateTransaction("AddParts", new[] { Cube("during-room-creation", 71) }))).accepted, "slow admin seed does not block existing-room edits");
        }
        finally { content.Resume.TrySetResult(true); }
        using var response = await creating;
        Check(response.IsSuccessStatusCode && server.Registry.Resolve(seed.room).IsHealthy, "staged concurrent creation publishes one complete room");
    }
    private static async Task RegistryRestoreChecks(string directory, CollaborationTransport transport, string originalRoot, string originalConfig,
        string archiveA, string archiveB, Bootstrap a, Bootstrap b, string hashA, string hashB, JoinResponse revoked) {
        string restored = Path.Combine(directory, "restored registry " + transport); RoomRegistry.Initialize(restored);
        string config = Path.Combine(restored, "server.json"), adminPath = Path.Combine(restored, "admin.json");
        foreach (string archive in new[] { archiveA, archiveB }) {
            var result = await RunCli("room", "restore", "--config", config, "--admin-file", adminPath, "--input", archive);
            Check(result.code == 0, "offline CLI atomically registers a restored room with its archived identity");
        }
        Check((await RunCli("room", "restore", "--config", config, "--admin-file", adminPath, "--input", archiveA)).code == 1, "restore registration refuses an existing room, including a restored copy");
        using (var registry = new RoomRegistry(config)) {
            var roomA = registry.Resolve(a.room); var roomB = registry.Resolve(b.room);
            Check(DocumentModel.Hash(roomA.Snapshot) == hashA && DocumentModel.Hash(roomB.Snapshot) == hashB, "per-room restore preserves documents/generations/catalogs and geometry references");
            Reject(() => roomA.Join(new JoinRequest { catalogHash = a.catalog.hash, resumeToken = revoked.resumeToken }), "membership-revoked");
            var owner = roomB.Join(new JoinRequest { catalogHash = b.catalog.hash, ownerCredential = b.ownerCredential, name = "Restored owner" });
            Check(owner.participant.role == "owner" && roomB.GetAsset(owner.token, Asset("Same hash, isolated rooms").hash).Length > 0, "restored owner and geometry remain usable");
            // Resume the archived editor, preserving personal undo/redo rather than creating new history.
            string cache = Path.Combine(directory, "beta-" + transport + ".json");
            string resume = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(cache))["resumeToken"].GetValue<string>();
            var resumed = roomB.Join(new JoinRequest { catalogHash = b.catalog.hash, resumeToken = resume });
            Check(roomB.Commit(resumed.token, Tx(roomB.Snapshot, "Undo", Array.Empty<Part>(), sequence: resumed.sequence + 1)).accepted &&
                roomB.Commit(resumed.token, Tx(roomB.Snapshot, "Redo", Array.Empty<Part>(), sequence: resumed.sequence + 2)).accepted, "registered restore retains participant history and cursor");
        }
        // Failed/locked/incompatible room recovery must never replace data or prevent other rooms from loading.
        string corruptPath = Path.Combine(originalRoot, "rooms", "alpha", "room.sqlite"); byte[] saved = File.ReadAllBytes(corruptPath);
        File.WriteAllBytes(corruptPath, Encoding.UTF8.GetBytes("corrupt room database"));
        using (var registry = new RoomRegistry(originalConfig)) {
            Check(registry.List().rooms.Single(r => r.room == "alpha").status != "ready" && registry.Resolve("beta").IsHealthy, "corrupt room stays unavailable alongside healthy room");
            Reject(() => registry.Resolve("alpha"), "room-unavailable");
            Check(File.ReadAllBytes(corruptPath).SequenceEqual(Encoding.UTF8.GetBytes("corrupt room database")), "corrupt database is never replaced with empty data");
        }
        File.WriteAllBytes(corruptPath, saved);
        using (var locked = new RoomStore(Path.Combine(originalRoot, "rooms", "alpha")))
        using (var registry = new RoomRegistry(originalConfig)) {
            Check(registry.List().rooms.Single(r => r.room == "alpha").status == "room-data-in-use" && registry.Resolve("beta").IsHealthy, "locked room reports its ownership conflict while another room recovers");
        }
        string reg = Path.Combine(originalRoot, "rooms", "alpha", "registration.json"); byte[] originalRegistration = File.ReadAllBytes(reg);
        var incompatible = RoomRegistry.Read<RoomRegistration>(reg); incompatible.catalogHash = "wrong-catalog"; File.Delete(reg); RoomRegistry.WriteNew(reg, incompatible);
        using (var registry = new RoomRegistry(originalConfig)) Check(registry.List().rooms.Single(r => r.room == "alpha").status == "registered-room-identity-mismatch" &&
            registry.Resolve("beta").IsHealthy, "registered identity/catalog mismatch never silently changes catalog or room");
        File.WriteAllBytes(reg, originalRegistration);
        string missing = Path.Combine(originalRoot, "rooms", "alpha", "room.sqlite"); File.Move(missing, missing + ".saved");
        using (var registry = new RoomRegistry(originalConfig)) Check(registry.List().rooms.Single(r => r.room == "alpha").status == "registered-room-database-missing" && !File.Exists(missing), "missing registered database is not initialized");
        File.Move(missing + ".saved", missing);
    }
    private static async Task RegistryFailureChecks(string directory) {
        string root = Path.Combine(directory, "registry bounds"); RoomRegistry.Initialize(root); string config = Path.Combine(root, "server.json");
        using (var registry = new RoomRegistry(config)) {
            for (int i = 0; i < ProcessBudget.MaxRooms; i++) registry.Create(HostedSeed("room-" + i));
            Reject(() => registry.Create(HostedSeed("overflow")), "server-room-limit");
            Check(registry.List().rooms.Length == ProcessBudget.MaxRooms && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging")).Any(), "room bound rejects before staging or publication");
        }
        await using var server = new CollaborationServer(null, multiRoomConfig: config); await server.StartAsync();
        var raw = server.Registry.Resolve("room-0").Join(new JoinRequest { name = "Connections", catalogHash = "catalog-small-v1", password = "password-room-0" });
        var sockets = new System.Collections.Generic.List<ClientWebSocket>();
        try {
            for (int i = 0; i < 10; i++) {
                var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + raw.token); sockets.Add(socket);
                await socket.ConnectAsync(new Uri(server.Url.Replace("http:", "ws:") + "/rooms/room-0/events?after=0&transient=-1"), CancellationToken.None);
            }
            var busy = await HostingRequest(server.Url, "/rooms/room-0/events?after=0&transient=-1", raw.token);
            Check(Encoding.UTF8.GetString(busy.bytes).Contains("room-event-limit"), "one room cannot exhaust process event connections");
            Check((await HostingRequest(server.Url, "/rooms/room-1/health")).code == 200, "room connection saturation leaves another room available");
            for (int i = 1; i <= 3; i++) {
                var member = server.Registry.Resolve("room-" + i).Join(new JoinRequest { name = "Global connections", catalogHash = "catalog-small-v1", password = "password-room-" + i });
                for (int n = 0; n < (i == 3 ? 2 : 10); n++) {
                    var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + member.token); sockets.Add(socket);
                    await socket.ConnectAsync(new Uri(server.Url.Replace("http:", "ws:") + "/rooms/room-" + i + "/events?after=0&transient=-1"), CancellationToken.None);
                }
            }
            var extra = server.Registry.Resolve("room-4").Join(new JoinRequest { name = "Over global", catalogHash = "catalog-small-v1", password = "password-room-4" });
            var limited = await HostingRequest(server.Url, "/rooms/room-4/events?after=0&transient=-1", extra.token);
            Check(Encoding.UTF8.GetString(limited.bytes).Contains("server-event-limit"), "process event streams are bounded across rooms");
            Check((await HostingRequest(server.Url, "/rooms/room-5/health")).code == 200, "event saturation reserves capacity for ordinary requests");
        }
        finally { foreach (var socket in sockets) { socket.Abort(); socket.Dispose(); } }
    }
}
