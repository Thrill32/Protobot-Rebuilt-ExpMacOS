using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task AdmissionChecks(string directory) {
        // Exhaustively check deterministic delay bounds, including hostile inputs.
        foreach (int jitter in new[] { int.MinValue, 0, 125, 250, int.MaxValue })
        foreach (int hint in new[] { int.MinValue, 0, 1, 250, 1000, int.MaxValue }) {
            int first = ConnectionRetry.DelayMilliseconds(1, jitter, hint), second = ConnectionRetry.DelayMilliseconds(2, jitter, hint);
            Check(first >= 250 && first <= 5000 && second >= 500 && second <= 5000 && second >= first,
                "deterministic jitter/backoff and hostile hint clamps");
        }
        foreach (string value in new[] { "-1", "999999999999999999999", "secret reflected header", "Fri, 01 Jan 2100 00:00:00 GMT", "1, 2" }) {
            using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", value);
            Check(ConnectionRetry.RetryHint(response) == 0, "malformed hint uses local default without reflecting text");
        }
        using (var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) {
            response.Headers.TryAddWithoutValidation("Retry-After", "9999999999");
            Check(ConnectionRetry.RetryHint(response) == 60000 && ConnectionRetry.DelayMilliseconds(1, 0, ConnectionRetry.RetryHint(response)) == 5000,
                "valid huge hints clamp separately for manual cooldown and safe waits");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden }) {
            using var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("Retry-After", "60"); int sends = 0;
            await RejectAsync(() => ConnectionRetry.ReadAsync<int>((_, _) => { sends++; throw ConnectionRetry.Rejection(response, WireJson.Encode(new WireError { error = "server-request-limit" })); }, default), "http-" + (int)status);
            Check(sends == 1, "authentication status stays terminal even with reflected busy code and retry hint");
        }
        using (var duplicate = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) {
            duplicate.Headers.TryAddWithoutValidation("Retry-After", new[] { "1", "2" });
            Check(ConnectionRetry.RetryHint(duplicate) == 0, "duplicate hints use local default");
        }
        foreach (string terminal in new[] { "wrong-password", "membership-revoked", "proxy-authentication-required", "tls-trust-or-name-failed", "invalid-server-response", "server-version-mismatch", "room-full", "join-rate-limit" }) {
            int sends = 0;
            await RejectAsync(() => ConnectionRetry.ReadAsync<int>((_, _) => { sends++; throw new CollaborationException(terminal); }, default), terminal);
            Check(sends == 1, "terminal rejection never retries: " + terminal);
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.ProxyAuthenticationRequired, HttpStatusCode.ServiceUnavailable }) {
            int sends = 0;
            try { await ConnectionRetry.ReadAsync<int>((_, _) => { sends++; throw new HttpRequestException("private CONNECT failure", null, status); }, default); throw new Exception("Expected terminal typed HTTP failure"); }
            catch (HttpRequestException) { Check(sends == 1, "typed CONNECT/HTTP rejection never triggers status-only retries: " + status); }
        }
        int attempts = 0, active = 0, maxActive = 0; var observed = new ConnectionObserver(); var clock = Stopwatch.StartNew();
        await RejectAsync(() => ConnectionRetry.ReadAsync<int>(async (_, token) => {
            attempts++; maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
            try { await Task.Yield(); throw new IOException("private network error must not be reflected"); }
            finally { Interlocked.Decrement(ref active); }
        }, default, observed), "safe-retry-exhausted");
        Check(attempts == 3 && maxActive == 1 && active == 0 && clock.ElapsedMilliseconds >= 750 && clock.ElapsedMilliseconds < 4000 &&
            observed.Items.Count == 2 && observed.Items.All(p => p.Stage == ConnectionStage.WaitingToRetry && p.Attempt >= 2 && p.Attempt <= 3),
            "safe failures are serial, jittered, bounded to three sends and expose fixed progress");
        using (var cancel = new CancellationTokenSource()) {
            attempts = 0; observed = new ConnectionObserver { OnReport = _ => cancel.Cancel() };
            await Canceled(ConnectionRetry.TransferAsync<int>((_, _) => { attempts++; throw new CollaborationException("asset-transfer-busy", 60000); }, cancel.Token, observed));
            Check(attempts == 1 && observed.Items.Single().RetryAfterMilliseconds == 5000, "cancel during a clamped transfer wait sends nothing further");
        }
        // Real cancellation deadlines apply to requests and the whole read, not just waits.
        clock.Restart(); attempts = 0;
        await RejectAsync(() => ConnectionRetry.ReadAsync<int>(async (_, token) => { attempts++; await Task.Delay(30000, token); return 0; }, default), "safe-retry-exhausted");
        Check(attempts == 3 && clock.ElapsedMilliseconds >= 11500 && clock.ElapsedMilliseconds < 14000, "real stalled requests share a twelve-second total deadline");

        var seed = Fixture(); var asset = Asset("Bounded admission geometry");
        seed.document.assets = new[] { asset }; seed.document.parts = new[] { Custom("admission-model", asset) }; seed.document.parts[0].version = 1;
        await using var server = new CollaborationServer(seed, dataDirectory: Path.Combine(directory, "admission room")); await server.StartAsync();
        await using var relay = new LoopbackProxy(server.Url) { ForwardWebSockets = true }; await relay.StartAsync();
        using var proxy = new OutboundProxyFixture(relay.Url, "proxy-user", "private-proxy-password");
        var policies = new[] { NetworkPolicy.SystemDefault, NetworkPolicy.Direct, ProxyPolicy(proxy) };
        foreach (var policy in policies)
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
            string url = relay.Url;
            string cache = Path.Combine(directory, "admission-" + policy.Route + transport + ".json");
            using var client = new CollaborationClient(url, seed.room, seed.catalog, transport, recoveryFile: cache, networkPolicy: policy);
            relay.FaultPathSuffix = "/capabilities"; relay.FaultCode = "room-request-limit"; relay.FaultHint = "1"; relay.FaultRemaining = 1;
            int joins = relay.JoinRequests, members = server.Room.Memberships(seed.ownerCredential).memberships.Length;
            observed = new ConnectionObserver(); await client.JoinAsync("Bounded route test", progress: observed);
            Check(observed.Items.Count(p => p.Stage == ConnectionStage.WaitingToRetry) == 1 && client.IsConnected &&
                relay.JoinRequests == joins + 1 && server.Room.Memberships(seed.ownerCredential).memberships.Length == members + 1 && client.Snapshot.assets.Single().vertices.SequenceEqual(asset.vertices),
                "real safe preflight, one admission and hydrated geometry: " + policy.Route + "/" + transport);
            string identity = client.Participant.id; string accepted = DocumentModel.Hash(client.Snapshot); byte[] saved = File.ReadAllBytes(cache);
            await client.PrepareReconnectAsync(); relay.FaultRemaining = 100;
            using (var cancel = new CancellationTokenSource()) {
                observed = new ConnectionObserver { OnReport = p => { if (p.Stage == ConnectionStage.WaitingToRetry) cancel.Cancel(); } };
                joins = relay.JoinRequests;
                await Canceled(client.JoinAsync("Canceled wait", cancellation: cancel.Token, progress: observed));
                Check(relay.JoinRequests == joins && client.Participant.id == identity && File.ReadAllBytes(cache).SequenceEqual(saved) && DocumentModel.Hash(client.Snapshot) == accepted,
                    "cancel safe preflight keeps accepted snapshot, cache bytes and membership on " + policy.Route + "/" + transport);
            }
            relay.FaultRemaining = 0; await client.JoinAsync("Explicit resume"); await client.LeaveAsync();
        }
        relay.FaultPathSuffix = "/capabilities"; relay.FaultCode = "server-request-limit"; relay.FaultRemaining = 100; relay.FaultHint = "9999999999999999999999";
        using (var client = new CollaborationClient(relay.Url, seed.room, seed.catalog, recoveryFile: Path.Combine(directory, "no-admission.json"))) {
            int joins = relay.JoinRequests; int faults = relay.FaultRequests;
            await RejectAsync(() => client.JoinAsync("Exhausted"), "safe-retry-exhausted");
            Check(relay.FaultRequests == faults + 3 && relay.JoinRequests == joins && !File.Exists(Path.Combine(directory, "no-admission.json")),
                "hostile hint exhaustion remains three sends before admission or cache access");
        }
        relay.FaultBody = "malformed private origin response"; relay.FaultRemaining = 10;
        using (var client = new CollaborationClient(relay.Url, seed.room, seed.catalog)) {
            int faults = relay.FaultRequests;
            await RejectAsync(() => client.JoinAsync("Malformed rejection"), "http-503");
            Check(relay.FaultRequests == faults + 1, "malformed HTTP rejection is terminal, without status-only retries");
        }
        relay.FaultBody = null; relay.FaultDelayMilliseconds = 5000;
        using (var cancel = new CancellationTokenSource(100))
        using (var client = new CollaborationClient(relay.Url, seed.room, seed.catalog)) {
            int joins = relay.JoinRequests;
            await Canceled(client.JoinAsync("Cancel HTTP request", cancellation: cancel.Token));
            Check(relay.JoinRequests == joins, "cancellation of a real in-flight preflight cannot reach admission");
        }
        relay.FaultDelayMilliseconds = 0; relay.FaultRemaining = 0;
        // A received definitive rejection informs manual retry; it never resends admission.
        foreach (string rejection in new[] { "room-full", "join-rate-limit", "wrong-password" }) {
            relay.RejectedJoinError = rejection; int joins = relay.JoinRequests;
            using var client = new CollaborationClient(relay.Url, seed.room, seed.catalog);
            try { await client.JoinAsync("Rejected"); throw new Exception("Expected rejection"); }
            catch (CollaborationException error) { Check(error.Code == rejection && error.RetryAfterMilliseconds == ConnectionRetry.CooldownMilliseconds(rejection), "fixed admission cooldown: " + rejection); }
            Check(relay.JoinRequests == joins + 1 && client.Participant == null && client.Snapshot == null, "definitive admission rejection is single-send with no publication");
        }
        relay.RejectedJoinError = null;
        int before = server.Room.Memberships(seed.ownerCredential).memberships.Length;
        using (var client = new CollaborationClient(relay.Url, seed.room, seed.catalog)) {
            relay.DropNextJoinResponse = true; int joins = relay.JoinRequests;
            try { await client.JoinAsync("Lost admission"); throw new Exception("Expected lost admission reply"); } catch (HttpRequestException) { }
            Check(relay.JoinRequests == joins + 1 && server.Room.Memberships(seed.ownerCredential).memberships.Length == before + 1 && client.Snapshot == null,
                "lost sent join reply creates exactly one membership, never an automatic resend");
        }
        // Geometry busy/cancel and lost commit outcomes use the same original identity.
        using (var client = new CollaborationClient(relay.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling,
            recoveryFile: Path.Combine(directory, "transfer-admission.json"))) {
            relay.FaultPathSuffix = "/assets/" + asset.hash; relay.FaultCode = "asset-transfer-busy"; relay.FaultRemaining = 100; relay.FaultHint = "1";
            using (var cancel = new CancellationTokenSource()) {
                observed = new ConnectionObserver { OnReport = p => { if (p.Stage == ConnectionStage.WaitingToRetry) cancel.Cancel(); } };
                await Canceled(client.JoinAsync("Cancel geometry wait", cancellation: cancel.Token, progress: observed));
            }
            string identity = client.Participant.id; Check(client.Snapshot == null && !client.IsConnected, "post-admission transfer wait cancellation retains membership without partial snapshot");
            relay.FaultRemaining = 2; await client.JoinAsync("Retry geometry"); Check(client.Participant.id == identity && client.Snapshot.assets.Length == 1, "immutable transfer retries resume known identity");
            relay.DropNextCommitResponse = true; int commits = relay.CommitRequests;
            try { await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("admission-commit") })); throw new Exception("Expected lost commit"); } catch (HttpRequestException) { }
            await client.PrepareReconnectAsync(); long revision = server.Room.Revision;
            Check(client.PendingCount == 1 && relay.CommitRequests == commits + 1, "ambiguous commit stays pending and is never automatically replayed");
            relay.FaultPathSuffix = "/capabilities"; relay.FaultCode = "room-request-limit"; relay.FaultRemaining = 100;
            byte[] pendingCache = File.ReadAllBytes(Path.Combine(directory, "transfer-admission.json"));
            string pendingHash = DocumentModel.Hash(client.Snapshot);
            using (var cancel = new CancellationTokenSource()) {
                observed = new ConnectionObserver { OnReport = _ => cancel.Cancel() };
                await Canceled(client.JoinAsync("Cancel pending preflight", cancellation: cancel.Token, progress: observed));
            }
            Check(client.PendingCount == 1 && DocumentModel.Hash(client.Snapshot) == pendingHash &&
                File.ReadAllBytes(Path.Combine(directory, "transfer-admission.json")).SequenceEqual(pendingCache) && relay.CommitRequests == commits + 1,
                "canceled backoff preserves pending IDs, cache bytes, scene and sent mutation count");
            relay.FaultRemaining = 0; await client.JoinAsync("Receipt resolution");
            Check(client.PendingCount == 0 && relay.CommitRequests == commits + 1 && server.Room.Revision == revision && client.Participant.id == identity, "explicit reconnect inspects receipt without command replay");
            await client.LeaveAsync();
        }
        await AdmissionServerLimits(directory);
        await AdmissionCliReads(directory, relay, seed);
    }
    private static async Task AdmissionServerLimits(string directory) {
        string root = Path.Combine(directory, "admission registry"); RoomRegistry.Initialize(root);
        await using var server = new CollaborationServer(null, multiRoomConfig: Path.Combine(root, "server.json")); await server.StartAsync();
        var a = HostedSeed("admission-alpha"); var b = HostedSeed("admission-beta"); server.Registry.Create(a); server.Registry.Create(b);
        using var http = NetworkPolicy.Direct.CreateHttpClient();
        using (var warm = await http.GetAsync(server.Url + "/rooms/" + a.room + "/health")) Check(warm.IsSuccessStatusCode, "room gate warm-up completes without retained response");
        var limits = typeof(CollaborationServer).GetField("roomLimits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        object roomLimits = limits.GetType().GetProperty("Item").GetValue(limits, new object[] { a.room });
        var requests = (SemaphoreSlim)roomLimits.GetType().GetField("requests").GetValue(roomLimits);
        for (int i = 0; i < ProcessBudget.MaxRoomRequests; i++) await requests.WaitAsync();
        try {
            using var blocked = await http.PostAsync(server.Url + "/rooms/" + a.room + "/join", new ByteArrayContent(WireJson.Encode(new JoinRequest { name = "Blocked", catalogHash = a.catalog.hash, password = a.password })));
            Check(ConnectionRetry.Rejection(blocked, await blocked.Content.ReadAsByteArrayAsync()).Code == "room-request-limit" && ConnectionRetry.RetryHint(blocked) == 1000 &&
                server.Registry.Resolve(a.room).Memberships(a.ownerCredential).memberships.Length == 0, "request exhaustion is definitively before admission with bounded guidance");
            using var healthy = new CollaborationClient(server.Url, b.room, b.catalog, CollaborationTransport.ShortPolling); await healthy.JoinAsync("Other room", b.password);
            Check(healthy.IsConnected && server.Registry.Resolve(b.room).Snapshot.parts.Length == 0 && requests.CurrentCount == 0, "busy room holds no other room permits, queues or identity maps"); await healthy.LeaveAsync();
        }
        finally { requests.Release(ProcessBudget.MaxRoomRequests); }
        await using (var relay = new LoopbackProxy(server.Url)) {
            await relay.StartAsync(); relay.FaultPathSuffix = "/admin/rooms"; relay.FaultCode = "server-request-limit"; relay.FaultRemaining = 1; relay.FaultHint = "1";
            var listed = await RunCli("room", "list", "--url", relay.Url, "--admin-file", Path.Combine(root, "admin.json"), "--network-route", "direct");
            Check(listed.code == 0 && listed.error.Contains("safe retry 2 of 3"), "actual protected admin CLI list retries only its safe GET");
            string catalog = Path.Combine(directory, "create-admission-catalog.json"); RecoveryFiles.Write(catalog, a.catalog);
            relay.FaultRemaining = 0; relay.DropNextCreationResponse = true;
            var created = await RunCli("room", "create", "--url", relay.Url, "--admin-file", Path.Combine(root, "admin.json"), "--network-route", "direct",
                "--room", "ambiguous-created", "--catalog", catalog, "--owner-output", Path.Combine(directory, "ambiguous-created-owner.json"));
            Check(created.code != 0 && relay.Requests["/admin/rooms"] == 3 && server.Registry.Resolve("ambiguous-created").Name == "ambiguous-created",
                "actual CLI lost room-create reply publishes once without automatically resending the mutation");
        }
        // Rate limiter remains per registered room and saturates at sixty, including rejections.
        for (int i = 0; i < 60; i++) {
            using var response = await http.PostAsync(server.Url + "/rooms/" + a.room + "/join", new ByteArrayContent(WireJson.Encode(new JoinRequest { name = "Wrong password", catalogHash = a.catalog.hash, password = "wrong" })));
        }
        using var rate = await http.PostAsync(server.Url + "/rooms/" + a.room + "/join", new ByteArrayContent(WireJson.Encode(new JoinRequest { name = "Rate limited", catalogHash = a.catalog.hash, password = a.password })));
        Check(ConnectionRetry.Rejection(rate, await rate.Content.ReadAsByteArrayAsync()).Code == "join-rate-limit" && ConnectionRetry.RetryHint(rate) > 0 &&
            (int)roomLimits.GetType().GetField("attempts").GetValue(roomLimits) == 60 && server.Registry.Resolve(a.room).Memberships(a.ownerCredential).memberships.Length == 0,
            "rate-limit rejection has a remaining-window hint and saturating bounded per-room counter");
        var processRequests = (SemaphoreSlim)typeof(CollaborationServer).GetField("requests", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(server);
        for (int i = 0; i < ProcessBudget.MaxRequests; i++) await processRequests.WaitAsync();
        try {
            using var response = await http.GetAsync(server.Url + "/capabilities");
            Check((int)response.StatusCode == 503 && ConnectionRetry.Rejection(response, await response.Content.ReadAsByteArrayAsync()).Code == "server-request-limit" &&
                ConnectionRetry.RetryHint(response) == 1000, "process exhaustion rejects immediately with bounded guidance and no request queue");
        } finally { processRequests.Release(ProcessBudget.MaxRequests); }
        for (int i = 0; i < 10; i++) server.Registry.Resolve(b.room).Join(new JoinRequest { name = "Full " + i, catalogHash = b.catalog.hash, password = b.password });
        using var full = await http.PostAsync(server.Url + "/rooms/" + b.room + "/join", new ByteArrayContent(WireJson.Encode(new JoinRequest { name = "Room full", catalogHash = b.catalog.hash, password = b.password })));
        Check(ConnectionRetry.Rejection(full, await full.Content.ReadAsByteArrayAsync()).Code == "room-full" && ConnectionRetry.RetryHint(full) == 10000 &&
            server.Registry.Resolve(b.room).Memberships(b.ownerCredential).memberships.Length == 11, "room-full is before membership creation, without changing retained membership bounds");
    }
    private static async Task AdmissionCliReads(string directory, LoopbackProxy relay, Bootstrap seed) {
        string catalog = Path.Combine(directory, "admission-catalog.json"), owner = Path.Combine(directory, "admission-owner.json");
        RecoveryFiles.Write(catalog, seed.catalog); File.WriteAllText(owner, System.Text.Json.JsonSerializer.Serialize(new { schema = 1, room = seed.room, credential = seed.ownerCredential })); RecoveryFiles.PrivateFile(owner);
        foreach (string command in new[] { "doctor", "participants", "diagnostics" }) {
            relay.FaultPathSuffix = command == "doctor" ? "/capabilities" : command == "participants" ? "/participants" : "/diagnostics";
            relay.FaultCode = "server-request-limit"; relay.FaultHint = "1"; relay.FaultRemaining = 1;
            var args = command == "doctor" ? new[] { command, "--url", relay.Url, "--catalog", catalog, "--room", seed.room, "--transport", "short-polling", "--quick", "--network-route", "direct" } :
                new[] { command, "--url", relay.Url, "--owner-file", owner, "--network-route", "direct" };
            var result = await RunCli(args);
            Check(result.code == 0 && result.error.Contains("safe retry 2 of 3") && !result.error.Contains(seed.ownerCredential) && !result.error.Contains("private-proxy-password"), "actual CLI bounded read/wait feedback: " + command);
        }
        relay.FaultRemaining = 0; relay.RejectedJoinError = "join-rate-limit";
        int joins = relay.JoinRequests;
        var rejected = await RunCli("doctor", "--url", relay.Url, "--catalog", catalog, "--room", seed.room, "--quick");
        Check(rejected.code == 2 && relay.JoinRequests == joins + 1 && rejected.error.Contains("Retry explicitly after"), "actual all-mode doctor stops admission after definitive rejection and shows cooldown");
        relay.RejectedJoinError = null; relay.DropNextJoinResponse = true; joins = relay.JoinRequests;
        var lost = await RunCli("doctor", "--url", relay.Url, "--catalog", catalog, "--room", seed.room, "--quick");
        Check(lost.code == 2 && relay.JoinRequests == joins + 1, "actual all-mode doctor never advances to another join after a lost admission reply");
    }
}
