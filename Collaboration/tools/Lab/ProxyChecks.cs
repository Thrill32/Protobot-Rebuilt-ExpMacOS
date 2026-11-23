using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task ProxyChecks(string directory) {
        var seed = Fixture();
        string oldDiagnostic = WireJson.Text(new DiagnosticReport()).Replace("\"networkRoute\":null,", "").Replace("\"proxyAuthentication\":null,", "");
        Check(DiagnosticExport.Decode(Encoding.UTF8.GetBytes(oldDiagnostic)).networkRoute == null, "diagnostic schema 1 remains readable when original helper/export metadata omits new optional fields");
        await using var server = new CollaborationServer(seed, dataDirectory: Path.Combine(directory, "proxy-room")); await server.StartAsync();
        foreach (bool basic in new[] { false, true }) {
            using var proxy = new OutboundProxyFixture(server.Url, basic ? "proxy-user" : null, basic ? "private-proxy-password" : null);
            var policy = NetworkPolicy.Create(new NetworkConfiguration { route = "http-proxy", proxy = proxy.Url,
                authentication = basic ? "basic" : "none", username = basic ? "proxy-user" : null, password = basic ? "private-proxy-password" : null });
            foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
                using var client = new CollaborationClient(server.Url, seed.room, seed.catalog, transport, networkPolicy: policy);
                int frames = 0; client.Presence += _ => Interlocked.Increment(ref frames);
                await client.JoinAsync("Proxy runtime proof", ownerCredential: seed.ownerCredential); await Until(() => frames > 0);
                Check(client.IsConnected && client.ActiveTransport == (transport == CollaborationTransport.WebSocket ? "websocket" : transport == CollaborationTransport.LongPolling ? "long-polling" : "short-polling"), "actual pinned runtime supports selected explicit proxy transport");
                var asset = Asset("Proxy geometry " + basic + transport); string id = "proxy-" + basic + transport;
                var edit = await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Custom(id, asset) }, assetWrites: new[] { asset }));
                using (var observer = new CollaborationClient(server.Url, seed.room, seed.catalog, transport, networkPolicy: policy)) {
                    await observer.JoinAsync("Independent proxy profile", viewer: true);
                    Check(edit.accepted && observer.Snapshot.assets.Any(a => a.hash == asset.hash && a.vertices.Length > 0) && DocumentModel.Hash(observer.Snapshot) == DocumentModel.Hash(client.Snapshot), "proxy edit/event/geometry paths hydrate accepted content in an independent client");
                    await observer.LeaveAsync();
                }
                var reservation = await client.ReserveAsync(new[] { id });
                var moved = Custom(id, asset, 2); await client.PreviewAsync(new Preview { lease = reservation.token, sequence = 1, parts = new[] { moved } });
                var changed = await client.SubmitAsync(client.CreateTransaction("SetParts", new[] { moved }, lease: reservation.token));
                await client.ReleaseAsync(reservation.token);
                Check(changed.accepted && (await client.SubmitAsync(client.CreateTransaction("Undo", Array.Empty<Part>()))).accepted &&
                    (await client.SubmitAsync(client.CreateTransaction("Redo", Array.Empty<Part>()))).accepted, "leases/previews/history use the selected route");
                var usage = await client.ResourcesAsync();
                Check(usage.selectedRoom.usage.parts.used == server.Room.Snapshot.parts.Length && client.CaptureDiagnostics().networkRoute == "http-proxy" &&
                    client.CaptureDiagnostics().proxyAuthentication == (basic ? "basic" : "none"), "protected resource fetch follows policy and exports safe policy enums only");
                await client.LeaveAsync();
            }
            Check(proxy.Forwarded > 0 && (!basic || proxy.Authenticated > 0) && !proxy.OriginCredentialInConnect && !proxy.ProxyCredentialInOrigin, "proxy proof authenticates only at proxy and forwards selected origin");
            Check(proxy.Paths.Any(p => p.EndsWith("/capabilities")) && proxy.Paths.Any(p => p.Contains("/assets/")) && proxy.Paths.Any(p => p.EndsWith("/diagnostics")) && proxy.Paths.Any(p => p.EndsWith("/commit")), "capabilities, geometry, edits and diagnostics actually reach the forward proxy");
        }
        await ProxyFailureChecks(directory, server, seed);
        await ProxyCliChecks(directory, server, seed);
        await ProxyRecoveryChecks(directory, server, seed);
        await HttpsDoctorChecks(seed);
    }

    private static NetworkPolicy ProxyPolicy(OutboundProxyFixture proxy, bool basic = true) => NetworkPolicy.Create(new NetworkConfiguration { route = "http-proxy", proxy = proxy.Url,
        authentication = basic ? "basic" : "none", username = basic ? "proxy-user" : null, password = basic ? "private-proxy-password" : null });
    private static async Task ProxyFailureChecks(string directory, CollaborationServer server, Bootstrap seed) {
        foreach (var config in new[] {
            new NetworkConfiguration { schema = 2 }, new NetworkConfiguration { route = "socks" }, new NetworkConfiguration { authentication = "ntlm" },
            new NetworkConfiguration { route = "direct", password = "forbidden" }, new NetworkConfiguration { route = "http-proxy", proxy = "http://user:secret@localhost" },
            new NetworkConfiguration { route = "http-proxy", proxy = "https://localhost" }, new NetworkConfiguration { route = "http-proxy", proxy = "http://localhost/path" },
            new NetworkConfiguration { route = "http-proxy", proxy = "http://localhost", authentication = "basic", username = "bad:user", password = "secret" }
        }) {
            try { NetworkPolicy.Create(config); throw new Exception("Invalid network config accepted"); }
            catch (CollaborationException error) { Check(NetworkPolicy.IsCode(error.Code), "unsupported configuration gets a fixed code before creating a client"); }
        }
        using var proxy = new OutboundProxyFixture(server.Url, "proxy-user", "private-proxy-password") { RejectAll = true };
        string[] secrets = { "private-proxy-password", Uri.EscapeDataString("proxy-user:private-proxy-password"), Convert.ToBase64String(Encoding.ASCII.GetBytes("proxy-user:private-proxy-password")), proxy.Url, "/private/configuration/network.json" };
        string cache = Path.Combine(directory, "proxy-refused-membership.json");
        foreach (string secret in secrets) {
            proxy.ReflectedError = WireJson.Text(new WireError { error = secret });
            using var client = new CollaborationClient(server.Url, seed.room, seed.catalog, networkPolicy: ProxyPolicy(proxy), recoveryFile: cache);
            await RejectAsync(() => client.JoinAsync("Refused proxy"), "proxy-authentication-required");
            var report = Encoding.UTF8.GetString(DiagnosticExport.Encode(client.CaptureDiagnostics()));
            Check(!File.Exists(cache) && client.Snapshot == null && client.Participant == null && secrets.All(s => !report.Contains(s)), "malicious reflected and encoded 407 text never enters diagnostics or admission/cache state");
        }
        proxy.ReflectedError = new string('x', 200000);
        var refused = await ConnectionDoctor.RunAsync(server.Url, networkPolicy: ProxyPolicy(proxy), checkIdle: false);
        Check(!refused.passed && refused.checks.Single(c => c.name == "capabilities").code == "proxy-authentication-required" && proxy.Forwarded == 0, "large 407 body is rejected at headers without bypassing the failed explicit proxy");
        using (var direct = new CollaborationClient(server.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, networkPolicy: NetworkPolicy.Direct)) {
            await direct.JoinAsync("Independent direct profile"); Check(direct.IsConnected && direct.NetworkRoute == "direct", "explicit policy does not affect a separate direct profile"); await direct.LeaveAsync();
        }
        proxy.Pause = true; int connections = proxy.Connections;
        using (var cancel = new CancellationTokenSource()) {
            using var client = new CollaborationClient(server.Url, seed.room, seed.catalog, networkPolicy: ProxyPolicy(proxy), recoveryFile: cache);
            var connecting = client.JoinAsync("Cancelable proxy", cancellation: cancel.Token); await Until(() => proxy.Connections > connections);
            var timer = System.Diagnostics.Stopwatch.StartNew(); cancel.Cancel(); await Canceled(connecting);
            Check(timer.ElapsedMilliseconds < 1500 && !File.Exists(cache), "connecting through an unresponsive proxy cancels promptly without admitting or creating a cache");
        }
        proxy.Pause = false;
        using (var redirect = new OutboundProxyFixture(server.Url) { Redirect = server.Url }) {
            var report = await ConnectionDoctor.RunAsync(server.Url, networkPolicy: ProxyPolicy(redirect, false));
            Check(!report.passed && report.checks.Single(c => c.name == "capabilities").code == "http-302" && redirect.Forwarded == 0 && redirect.Connections == 1, "explicit proxy retains redirect refusal before origin admission");
        }
        using (var negotiate = new OutboundProxyFixture(server.Url, "proxy-user", "private-proxy-password") { ChallengeScheme = "Negotiate" }) {
            var report = await ConnectionDoctor.RunAsync(server.Url, networkPolicy: ProxyPolicy(negotiate));
            Check(!report.passed && report.checks.Single(c => c.name == "capabilities").code == "proxy-authentication-required" && negotiate.Authenticated == 0 && negotiate.Forwarded == 0, "explicit Basic input never negotiates integrated proxy authentication or bypasses a 407");
        }
        using (var authentication = new OutboundProxyFixture(server.Url, "proxy-user", "private-proxy-password")) {
            var wrong = NetworkPolicy.Create(new NetworkConfiguration { route = "http-proxy", proxy = authentication.Url, authentication = "basic", username = "proxy-user", password = "wrong-secret" });
            var report = await ConnectionDoctor.RunAsync(server.Url, networkPolicy: wrong);
            Check(!report.passed && report.checks.Single(c => c.name == "capabilities").code == "proxy-authentication-required" && authentication.Forwarded == 0, "incorrect Basic input cannot admit or bypass the explicit proxy");
        }
        await using (var origin = new LoopbackProxy(server.Url) { OriginBasicChallenge = true }) {
            await origin.StartAsync(); using var confinement = new OutboundProxyFixture(origin.Url, "proxy-user", "private-proxy-password");
            var report = await ConnectionDoctor.RunAsync(origin.Url, networkPolicy: ProxyPolicy(confinement));
            Check(!report.passed && !origin.OriginProxyAuthorizationObserved && !origin.OriginBasicAuthorizationObserved && confinement.Authenticated > 0, "origin 401 challenge cannot obtain either proxy credential channel");
        }
    }
    private static async Task ProxyCliChecks(string directory, CollaborationServer server, Bootstrap seed) {
        using var proxy = new OutboundProxyFixture(server.Url, "proxy-user", "private-proxy-password");
        string config = Path.Combine(directory, "private-network.json"), catalog = Path.Combine(directory, "proxy-catalog.json"), owner = Path.Combine(directory, "proxy-owner.json");
        RecoveryFiles.Write(config, new NetworkConfiguration { route = "http-proxy", proxy = proxy.Url, authentication = "basic", username = "proxy-user", password = "private-proxy-password" });
        RecoveryFiles.Write(catalog, seed.catalog); File.WriteAllText(owner, System.Text.Json.JsonSerializer.Serialize(new { schema = 1, room = seed.room, credential = seed.ownerCredential })); RecoveryFiles.PrivateFile(owner);
        var doctor = await RunCli("doctor", "--url", server.Url, "--catalog", catalog, "--room", seed.room, "--quick", "--network-file", config);
        Check(doctor.code == 0 && WireJson.Decode<ConnectionReport>(Encoding.UTF8.GetBytes(doctor.output)).diagnostics.Length == 3 && proxy.Paths.Any(p => p.EndsWith("/health")), "actual CLI all-mode doctor applies private config to health/capabilities and all three shared transports");
        var direct = await RunCli("doctor", "--url", server.Url, "--network-route", "direct", "--quick");
        Check(direct.code == 0, "actual CLI direct selection preserves capability-only workflow");
        using (var anonymousProxy = new OutboundProxyFixture(server.Url)) {
            string anonymousFile = Path.Combine(directory, "anonymous-network.json");
            File.WriteAllText(anonymousFile, System.Text.Json.JsonSerializer.Serialize(new { format = "protobot-network", schema = 1, route = "http-proxy", proxy = anonymousProxy.Url, authentication = "none" })); RecoveryFiles.PrivateFile(anonymousFile);
            var anonymous = await RunCli("doctor", "--url", server.Url, "--network-file", anonymousFile, "--quick");
            Check(anonymous.code == 0 && anonymousProxy.Forwarded > 0, "actual CLI accepts the documented anonymous file with absent credential fields");
        }
        var ambiguous = await RunCli("doctor", "--url", server.Url, "--network-route", "direct", "--network-file", config);
        Check(ambiguous.code == 1 && ambiguous.error.Trim() == "proxy-auth-route-mismatch", "actual CLI rejects ambiguous route configuration before networking");
        var members = await RunCli("participants", "--url", server.Url, "--owner-file", owner, "--network-file", config);
        Check(members.code == 0 && proxy.Paths.Any(p => p.EndsWith("/participants")), "actual CLI owner inspection follows the proxy policy");
        var member = server.Room.Join(new JoinRequest { name = "Proxy removal", catalogHash = seed.catalog.hash });
        var removed = await RunCli("remove-participant", "--url", server.Url, "--owner-file", owner, "--participant", member.participant.id, "--network-file", config);
        Check(removed.code == 0 && proxy.Paths.Any(p => p.EndsWith("/participants/remove")), "actual protected CLI removal follows policy");
        var diagnostics = await RunCli("diagnostics", "--url", server.Url, "--owner-file", owner, "--network-file", config);
        Check(diagnostics.code == 0 && !diagnostics.output.Contains(proxy.Url) && !diagnostics.output.Contains(config) && !diagnostics.output.Contains("private-proxy-password"), "actual owner CLI diagnostics excludes private config/address/secret");
        proxy.RejectAll = true; proxy.ReflectedError = WireJson.Text(new WireError { error = Convert.ToBase64String(Encoding.UTF8.GetBytes("proxy-user:private-proxy-password")) });
        var failed = await RunCli("participants", "--url", server.Url, "--owner-file", owner, "--network-file", config);
        Check(failed.code == 1 && failed.error.Trim() == "proxy-authentication-required" && !failed.output.Contains("private-proxy"), "actual CLI owner failure prints only a fixed 407 code");
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var broad = await RunCli("doctor", "--url", server.Url, "--network-file", config);
            Check(broad.code == 1 && broad.error.Trim() == "proxy-config-private-file-required", "CLI refuses a proxy file with broad permissions"); RecoveryFiles.PrivateFile(config);
        }
        string root = Path.Combine(directory, "proxy-registry"); RoomRegistry.Initialize(root);
        await using var multi = new CollaborationServer(null, multiRoomConfig: Path.Combine(root, "server.json")); await multi.StartAsync();
        using var adminProxy = new OutboundProxyFixture(multi.Url, "proxy-user", "private-proxy-password");
        RecoveryFiles.Write(config, new NetworkConfiguration { route = "http-proxy", proxy = adminProxy.Url, authentication = "basic", username = "proxy-user", password = "private-proxy-password" });
        var created = await RunCli("room", "create", "--url", multi.Url, "--admin-file", Path.Combine(root, "admin.json"), "--room", "proxied", "--catalog", catalog, "--owner-output", Path.Combine(directory, "proxy-new-owner.json"), "--network-file", config);
        var listed = await RunCli("room", "list", "--url", multi.Url, "--admin-file", Path.Combine(root, "admin.json"), "--network-file", config);
        var adminDiagnostics = await RunCli("diagnostics", "--url", multi.Url, "--admin-file", Path.Combine(root, "admin.json"), "--network-file", config);
        Check(created.code == 0 && listed.code == 0 && adminDiagnostics.code == 0 && adminProxy.Paths.Any(p => p == "POST /admin/rooms") && adminProxy.Paths.Any(p => p == "GET /admin/diagnostics"), "actual CLI server-admin create/list/diagnostics all follow the configured authenticated proxy");
    }
    private static async Task ProxyRecoveryChecks(string directory, CollaborationServer server, Bootstrap seed) {
        await using var relay = new LoopbackProxy(server.Url); await relay.StartAsync();
        using var proxy = new OutboundProxyFixture(relay.Url, "proxy-user", "private-proxy-password");
        string cache = Path.Combine(directory, "proxy-route-membership.json");
        using var client = new CollaborationClient(relay.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, recoveryFile: cache, networkPolicy: ProxyPolicy(proxy));
        await client.JoinAsync("Proxy recovery"); string identity = client.Participant.id;
        await client.PrepareReconnectAsync(); int joins = relay.JoinRequests; byte[] originalCache = File.ReadAllBytes(cache);
        relay.ReportedGeneration = "different-generation"; await client.SetNetworkPolicyAsync(ProxyPolicy(proxy));
        await RejectAsync(() => client.JoinAsync("Changed generation"), "saved-room-does-not-match");
        Check(relay.JoinRequests == joins && originalCache.SequenceEqual(File.ReadAllBytes(cache)) && client.Participant.id == identity, "route changes cannot relax the original generation scope or admit against a replacement room");
        relay.ReportedGeneration = null; await client.JoinAsync("Original generation");
        relay.DropNextCommitResponse = true;
        try { await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("proxy-lost-ack") })); throw new Exception("Expected lost reply"); } catch (HttpRequestException) { }
        await client.PrepareReconnectAsync(); long revision = server.Room.Revision;
        string before = DocumentModel.Hash(client.Snapshot); byte[] saved = File.ReadAllBytes(cache);
        using (var cancel = new CancellationTokenSource()) {
            cancel.Cancel(); await Canceled(client.SetNetworkPolicyAsync(NetworkPolicy.Direct, cancel.Token));
            Check(client.PendingCount == 1 && client.NetworkRoute == "http-proxy" && client.Participant.id == identity && saved.SequenceEqual(File.ReadAllBytes(cache)), "canceled configuration change retains the prior route, same identity and pending receipt file");
        }
        proxy.RejectAll = true; await client.SetNetworkPolicyAsync(ProxyPolicy(proxy));
        await RejectAsync(() => client.JoinAsync("Failed changed route"), "proxy-authentication-required");
        Check(client.PendingCount == 1 && client.Participant.id == identity && DocumentModel.Hash(client.Snapshot) == before && saved.SequenceEqual(File.ReadAllBytes(cache)), "failed route change retains origin/room/catalog/generation membership and pending IDs with accepted content unchanged");
        await client.SetNetworkPolicyAsync(NetworkPolicy.Direct); await client.JoinAsync("Direct route retry");
        Check(client.Participant.id == identity && client.PendingCount == 0 && server.Room.Revision == revision, "explicit direct reconnect resolves the same receipt without replay or replacement membership");
        relay.DropNextCommitResponse = true;
        try { await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("proxy-second-lost-ack") })); throw new Exception("Expected lost reply"); } catch (HttpRequestException) { }
        await client.PrepareReconnectAsync(); revision = server.Room.Revision; proxy.RejectAll = false;
        await client.SetNetworkPolicyAsync(ProxyPolicy(proxy)); await client.JoinAsync("Proxy receipt retry");
        Check(client.Participant.id == identity && client.PendingCount == 0 && server.Room.Revision == revision, "switching back to a working proxy resolves the ambiguous receipt without command replay");
        await client.PrepareReconnectAsync(); saved = File.ReadAllBytes(cache); before = DocumentModel.Hash(client.Snapshot);
        relay.AssetPauseAfterBytes = 1; relay.ContinueAssetDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAsset = Asset("Proxy canceled download"); var joined = server.Room.Join(new JoinRequest { name = "New geometry", catalogHash = seed.catalog.hash });
        using (var editor = new CollaborationClient(server.Url, seed.room, seed.catalog, CollaborationTransport.LongPolling, networkPolicy: NetworkPolicy.Direct)) {
            await editor.JoinAsync("Geometry publisher");
            await editor.SubmitAsync(editor.CreateTransaction("AddParts", new[] { Custom("proxy-new-model", newAsset) }, assetWrites: new[] { newAsset })); await editor.LeaveAsync();
        }
        proxy.RejectAll = false; await client.SetNetworkPolicyAsync(ProxyPolicy(proxy));
        using (var cancel = new CancellationTokenSource()) {
            var reconnect = client.JoinAsync("Cancel proxy model", cancellation: cancel.Token);
            await relay.PartialAssetSent.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Canceled(reconnect);
            Check(client.Participant.id == identity && DocumentModel.Hash(client.Snapshot) == before && client.PendingCount == 0, "mid-transfer proxy cancellation preserves accepted content and same membership");
        }
        relay.ContinueAssetDownload.TrySetResult(true); relay.AssetPauseAfterBytes = 0;
        await client.JoinAsync("Retry model through proxy"); Check(client.Participant.id == identity && client.Snapshot.assets.Any(a => a.hash == newAsset.hash), "canceled proxy transfer retries as immutable content and resumes the original identity");
        Check(proxy.Paths.Any(p => p.Contains("/receipt?")), "pending receipt recovery also traverses the explicit proxy");
        await client.LeaveAsync(); server.Room.Leave(joined.token);
    }
}
