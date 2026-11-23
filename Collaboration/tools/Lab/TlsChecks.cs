using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private const string TlsPassword = "private-test-pfx-password";
    private static string TlsJson(string certificate = "server.pfx", string password = TlsPassword) =>
        JsonSerializer.Serialize(new { format = "protobot-tls", schema = 1, certificate, password });
    private static void TlsFile(string path, byte[] bytes) {
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows()) { RecoveryFiles.PrivateFile(path); return; }
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(identity.User);
        acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
    private static void TlsText(string path, string text) => TlsFile(path, System.Text.Encoding.UTF8.GetBytes(text));
    private static X509Certificate2 TlsTestCertificate(bool expired = false, bool clientOnly = false) {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
    }
    private static async Task TlsChecks(string directory) {
        string root = Path.Combine(directory, "private TLS"); RecoveryFiles.CreateDirectory(root);
        string config = Path.Combine(root, "tls.json"), pfx = Path.Combine(root, "server.pfx");
        using var certificate = TlsTestCertificate();
        TlsFile(pfx, certificate.Export(X509ContentType.Pfx, TlsPassword)); TlsText(config, TlsJson());
        using (var loaded = await TlsConfiguration.LoadAsync(config)) Check(loaded != null, "private schema-1 TLS config resolves a relative PFX independently of working directory");
        foreach (var test in new[] {
            ("{}", "tls-config-invalid"), ("null", "tls-config-invalid"), ("{", "tls-config-invalid"),
            (TlsJson().Replace("\"schema\":1", "\"schema\":2"), "tls-config-version"),
            (TlsJson().Replace("protobot-tls", "future-tls"), "tls-config-version"),
            (TlsJson().Replace("\"schema\":1", "\"schema\":1,\"schema\":1"), "tls-config-invalid"),
            (TlsJson().Replace("\"schema\":1", "\"schema\":1,\"secret-extra\":true"), "tls-config-invalid"),
            (TlsJson(password: "wrong-private-password"), "tls-certificate-invalid-or-password"),
            (TlsJson("missing-private-certificate.pfx"), "tls-certificate-unreadable"),
            (TlsJson(certificate: ""), "tls-config-invalid"), (new string(' ', 4097), "tls-config-size"), ("", "tls-config-size")
        }) {
            TlsText(config, test.Item1); await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, test.Item2);
        }
        TlsText(config, TlsJson());
        foreach (var test in new[] { (new byte[] { 1, 2, 3 }, "tls-certificate-invalid-or-password"), (new byte[1024 * 1024 + 1], "tls-certificate-size"),
            (Array.Empty<byte>(), "tls-certificate-size"), (new X509Certificate2Collection(certificate).Export(X509ContentType.Pkcs7), "tls-certificate-invalid-or-password") }) {
            TlsFile(pfx, test.Item1); await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, test.Item2);
        }
        using (var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData)) {
            TlsFile(pfx, publicOnly.Export(X509ContentType.Pfx, TlsPassword));
            await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, "tls-certificate-private-key-required");
        }
        using (var expired = TlsTestCertificate(expired: true)) {
            TlsFile(pfx, expired.Export(X509ContentType.Pfx, TlsPassword));
            await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, "tls-certificate-validity");
        }
        using (var wrongUsage = TlsTestCertificate(clientOnly: true)) {
            TlsFile(pfx, wrongUsage.Export(X509ContentType.Pfx, TlsPassword));
            await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, "tls-certificate-server-usage-required");
        }
        TlsFile(pfx, certificate.Export(X509ContentType.Pfx, TlsPassword));
        var tooMany = new X509Certificate2Collection(); for (int i = 0; i < 9; i++) tooMany.Add(certificate);
        TlsFile(pfx, tooMany.Export(X509ContentType.Pfx, TlsPassword));
        await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, "tls-certificate-invalid-or-password");
        TlsFile(pfx, certificate.Export(X509ContentType.Pfx, TlsPassword));
        if (!OperatingSystem.IsWindows()) {
            foreach (string file in new[] { config, pfx }) {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, file == config ? "tls-config-private-file-required" : "tls-certificate-private-file-required");
                RecoveryFiles.PrivateFile(file);
                string link = file + ".link"; File.CreateSymbolicLink(link, file);
                if (file == pfx) TlsText(config, TlsJson(Path.GetFileName(link)));
                await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(file == config ? link : config); }, file == config ? "tls-config-private-file-required" : "tls-certificate-private-file-required");
                TlsText(config, TlsJson());
            }
            File.SetUnixFileMode(pfx, UnixFileMode.None);
            await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(config); }, "tls-certificate-unreadable");
            RecoveryFiles.PrivateFile(pfx);
        }
        await RejectAsync(async () => { using var ignored = await TlsConfiguration.LoadAsync(Path.Combine(root, "missing-config.json")); }, "tls-config-unreadable");
        using (var stop = new CancellationTokenSource()) {
            stop.Cancel(); await Canceled(TlsConfiguration.LoadAsync(config, stop.Token));
        }
        using var tls = await TlsConfiguration.LoadAsync(config);
        Reject(() => new CollaborationServer(Fixture(), tls: tls), "tls-requires-https-listener");
        Reject(() => new CollaborationServer(Fixture(), "https://localhost:0", tls: tls), "tls-listener-requires-ip");
        await TlsListenerChecks(root, config, tls, certificate);
        await TlsMultiRoomChecks(root, tls, certificate);
        await TlsCliChecks(root, config);
    }
    private static async Task TlsMultiRoomChecks(string root, TlsConfiguration tls, X509Certificate2 certificate) {
        string directory = Path.Combine(root, "multi"); RoomRegistry.Initialize(directory);
        string config = Path.Combine(directory, "server.json");
        using (var registry = new RoomRegistry(config)) {
            registry.Create(HostedSeed("tls-alpha")); registry.Create(HostedSeed("tls-beta")); registry.Create(HostedSeed("tls-broken"));
        }
        File.WriteAllText(Path.Combine(directory, "rooms", "tls-broken", "room.sqlite"), "deliberately corrupt test room");
        await using var server = new CollaborationServer(null, "https://127.0.0.1:0", multiRoomConfig: config, tls: tls); await server.StartAsync();
        string url = new UriBuilder(server.Url) { Host = "localhost" }.Uri.GetLeftPart(UriPartial.Authority);
        using var a = new CollaborationClient(url, "tls-alpha", HostedSeed("tls-alpha").catalog, CollaborationTransport.WebSocket);
        using var b = new CollaborationClient(url, "tls-beta", HostedSeed("tls-beta").catalog, CollaborationTransport.ShortPolling);
        TlsFixtureClient(a, certificate); TlsFixtureClient(b, certificate);
        await a.JoinAsync("Alpha", "password-tls-alpha"); await b.JoinAsync("Beta", "password-tls-beta");
        Check((await a.SubmitAsync(a.CreateTransaction("AddParts", new[] { Cube("same-id", 10) }))).accepted &&
            (await b.SubmitAsync(b.CreateTransaction("AddParts", new[] { Cube("same-id", 20) }))).accepted, "independent healthy rooms accept TLS edits beside a corrupt registered room");
        Check(a.Snapshot.parts.Single().position[0] == 10 && b.Snapshot.parts.Single().position[0] == 20 && server.Registry.List().rooms.Single(r => r.room == "tls-broken").usage == null,
            "TLS listener preserves exact room isolation and unavailable status");
        using var http = TlsFixtureHttp(certificate);
        using var request = new HttpRequestMessage(HttpMethod.Get, url + "/admin/rooms");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", RoomRegistry.Read<ServerAdministration>(Path.Combine(directory, "admin.json")).credential);
        using var response = await http.SendAsync(request);
        Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("tls-broken"), "protected server administration uses the same HTTPS listener");
        string ownership = Path.Combine(root, "tls-created-owners");
        using (var untrustedCreation = new RemoteRoomCreation(url, network: NetworkPolicy.Direct)) {
            try { await untrustedCreation.CreateAsync(CreationContent(), "tls-untrusted-creation", "", null, true, ownership, CancellationToken.None); throw new Exception("Expected TLS rejection"); }
            catch (HttpRequestException error) { Check(ConnectionDoctor.Code(error) == "tls-trust-or-name-failed" && !Directory.Exists(ownership), "production room creation rejects untrusted TLS before retaining ownership or sending a mutation"); }
        }
        using (var trustedCreation = new RemoteRoomCreation(url, network: NetworkPolicy.Direct)) {
            var field = typeof(RemoteRoomCreation).GetField("http", BindingFlags.Instance | BindingFlags.NonPublic);
            ((HttpClient)field.GetValue(trustedCreation)).Dispose(); field.SetValue(trustedCreation, TlsFixtureHttp(certificate));
            var admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(directory, "admin.json"));
            var created = await trustedCreation.CreateAsync(CreationContent(), "tls-created", "", admin.credential, true, ownership, CancellationToken.None);
            await trustedCreation.CheckAsync(created, CancellationToken.None);
            Check(created.confirmed && (await trustedCreation.ListAsync(admin.credential, CancellationToken.None)).rooms.Any(r => r.room == created.room), "test-scoped ephemeral TLS trust covers creation, owner check and admin listing on one HTTPS listener");
        }
        await a.LeaveAsync(); await b.LeaveAsync();
    }
    // Test-scoped trust of this ephemeral leaf, retaining the platform's name
    // check. No production callback, OS trust store, or public acceptance claim.
    private static HttpClient TlsFixtureHttp(X509Certificate2 certificate) => new HttpClient(new HttpClientHandler {
        UseProxy = false, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, peer, _, errors) => peer != null && peer.RawData.SequenceEqual(certificate.RawData) &&
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private static void TlsFixtureClient(CollaborationClient client, X509Certificate2 certificate) {
        var field = typeof(CollaborationClient).GetField("http", BindingFlags.Instance | BindingFlags.NonPublic);
        ((HttpClient)field.GetValue(client)).Dispose(); field.SetValue(client, TlsFixtureHttp(certificate));
    }
    private static async Task TlsListenerChecks(string root, string config, TlsConfiguration tls, X509Certificate2 certificate) {
        var seed = Fixture();
        string ambient = Environment.GetEnvironmentVariable("Kestrel__Endpoints__Injected__Url");
        // Kestrel must ignore ambient endpoints, even if they would expose HTTP.
        CollaborationServer server;
        try {
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Injected__Url", "http://127.0.0.1:0");
            server = new CollaborationServer(seed, "https://0.0.0.0:0", dataDirectory: Path.Combine(root, "room"), tls: tls);
        }
        finally { Environment.SetEnvironmentVariable("Kestrel__Endpoints__Injected__Url", ambient); }
        await using var owned = server;
        await server.StartAsync();
        string url = new UriBuilder(server.Url) { Host = "localhost" }.Uri.GetLeftPart(UriPartial.Authority);
        Check(server.Url.StartsWith("https://", StringComparison.Ordinal), "explicit TLS starts one HTTPS listener");
        using (var http = TlsFixtureHttp(certificate)) {
            Check((await http.GetAsync(url + "/health")).IsSuccessStatusCode, "configured listener negotiates the provided PFX");
            try { await http.GetAsync(url.Replace("localhost", "127.0.0.1") + "/health"); throw new Exception("Wrong name accepted"); }
            catch (HttpRequestException error) { Check(ConnectionDoctor.Code(error) == "tls-trust-or-name-failed", "test-scoped trust still rejects the wrong hostname"); }
        }
        var report = await ConnectionDoctor.RunAsync(url, networkPolicy: NetworkPolicy.Direct);
        Check(!report.passed && report.checks.Single(c => c.name == "capabilities").code == "tls-trust-or-name-failed", "unchanged production doctor rejects ephemeral untrusted TLS");
        using (var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }) {
            bool accepted = false;
            try { using var response = await plain.GetAsync(url.Replace("https://", "http://") + "/health"); accepted = response.IsSuccessStatusCode; } catch (HttpRequestException) { }
            Check(!accepted, "configured HTTPS listener never falls back to plaintext HTTP");
        }
        using (var proxy = new OutboundProxyFixture(url, "proxy-user", "private-proxy-password")) {
            report = await ConnectionDoctor.RunAsync(url, networkPolicy: ProxyPolicy(proxy));
            Check(!report.passed && report.checks.Single(c => c.name == "capabilities").code == "tls-trust-or-name-failed" && proxy.Connects > 0 && !proxy.OriginCredentialInConnect,
                "configured listener through CONNECT preserves production trust rejection and proxy separation");
        }
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
            using var client = new CollaborationClient(url, seed.room, seed.catalog, transport, networkPolicy: NetworkPolicy.Direct, recoveryFile: Path.Combine(root, "membership-" + transport + ".json"));
            TlsFixtureClient(client, certificate);
            int frames = 0; client.Presence += _ => Interlocked.Increment(ref frames);
            await client.JoinAsync("TLS fixture"); await Until(() => frames > 0);
            string identity = client.Participant.id;
            var asset = Asset("TLS geometry " + transport);
            var edit = await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Custom("tls-" + transport, asset) }, assetWrites: new[] { asset }));
            Check(edit.accepted && client.IsConnected && client.ActiveTransport == (transport == CollaborationTransport.WebSocket ? "websocket" : transport == CollaborationTransport.LongPolling ? "long-polling" : "short-polling"), "test-scoped TLS supports selected event transport and immutable geometry/edit paths " + transport);
            await client.PrepareReconnectAsync(); await client.JoinAsync("TLS resume");
            Check(client.Participant.id == identity && client.Snapshot.assets.Any(a => a.hash == asset.hash && a.vertices.Length > 0), "explicit TLS reconnect preserves membership and hydrates geometry " + transport);
            string diagnostic = System.Text.Encoding.UTF8.GetString(DiagnosticExport.Encode(client.CaptureDiagnostics()));
            Check(!diagnostic.Contains(TlsPassword) && !diagnostic.Contains(config) && !diagnostic.Contains("server.pfx"), "TLS input never enters client diagnostic exports");
            await client.LeaveAsync();
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await using (var canceled = new CollaborationServer(Fixture(), "https://127.0.0.1:0", tls: tls)) await Canceled(canceled.StartAsync(cancel.Token));
        Check((await ConnectionDoctor.RunAsync(url, networkPolicy: NetworkPolicy.Direct)).checks.Single(c => c.name == "capabilities").code == "tls-trust-or-name-failed", "canceling another TLS startup leaves the original listener running");
    }
    private static async Task TlsCliChecks(string root, string config) {
        string room = Path.Combine(root, "room"), database = Path.Combine(room, "room.sqlite");
        byte[] before = File.ReadAllBytes(database);
        TlsText(config, TlsJson(password: "wrong-private-password"));
        var failed = await RunCli("serve", "--data-dir", room, "--listen", "https://127.0.0.1:0", "--tls-file", config);
        Check(failed.code == 1 && failed.output == "" && failed.error.Trim() == "tls-certificate-invalid-or-password" && before.SequenceEqual(File.ReadAllBytes(database)), "actual CLI certificate failure is redacted and precedes room access/readiness");
        TlsText(config, TlsJson());
        failed = await RunCli("serve", "--bootstrap-stdin", "--ephemeral", "--tls-file", config);
        Check(failed.code == 1 && failed.error.Trim() == "tls-standalone-only", "owned-helper bootstrap refuses explicit standalone TLS before reading input");
        using (var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) {
            blocker.Start(); int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            failed = await RunCli("serve", "--data-dir", room, "--listen", "https://127.0.0.1:" + port, "--tls-file", config);
            Check(failed.code == 1 && failed.output == "" && failed.error.Trim() == "tls-listener-start-failed", "actual CLI bind failure emits a fixed error without readiness or HTTP fallback");
        }
        string legacy = Environment.GetEnvironmentVariable("PROTOBOT_TEST_TLS_LEGACY");
        if (legacy != null) {
            var old = new ProcessStartInfo(legacy) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "serve", "--ephemeral", "--tls-file", config }) old.ArgumentList.Add(value);
            using var processOld = Process.Start(old); processOld.StandardInput.Close();
            var outputOld = processOld.StandardOutput.ReadToEndAsync(); var errorOld = processOld.StandardError.ReadToEndAsync();
            try {
                using var timeout = new CancellationTokenSource(10000); await processOld.WaitForExitAsync(timeout.Token);
                Check(processOld.ExitCode == 1 && (await outputOld) == "" && (await errorOld).Trim() == "unknown-option", "preserved pre-TLS helper refuses new local config without starting a listener");
            }
            finally { if (!processOld.HasExited) { processOld.Kill(); await processOld.WaitForExitAsync(); } }
        }
        var start = new ProcessStartInfo { FileName = Environment.GetEnvironmentVariable("PROTOBOT_TEST_CLI") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Environment.GetEnvironmentVariable("PROTOBOT_TEST_CLI") == null) start.ArgumentList.Add(Path.GetFullPath("src/Cli/bin/Debug/net10.0/protobot-collab.dll"));
        foreach (string value in new[] { "serve", "--data-dir", room, "--listen", "https://127.0.0.1:0", "--tls-file", config, "--public-url", "https://cad.example.org:8443" }) start.ArgumentList.Add(value);
        start.Environment["Kestrel__Endpoints__Injected__Url"] = "http://127.0.0.1:0";
        using var process = Process.Start(start); process.StandardInput.Close(); var errors = process.StandardError.ReadToEndAsync();
        try {
            var line = process.StandardOutput.ReadLineAsync(); Check(await Task.WhenAny(line, Task.Delay(15000)) == line, "actual TLS CLI has bounded readiness");
            string text = await line; using var ready = JsonDocument.Parse(text ?? "{}");
            string url = ready.RootElement.GetProperty("url").GetString();
            Check(url.StartsWith("https://127.0.0.1:", StringComparison.Ordinal) && ready.RootElement.GetProperty("publicUrl").GetString() == "https://cad.example.org:8443" && !text.Contains(TlsPassword) && !text.Contains(config) && !text.Contains("server.pfx"), "actual CLI separates HTTPS binding/public advertisement and omits private input despite ambient HTTP configuration");
            var doctor = await RunCli("doctor", "--url", url, "--network-route", "direct");
            Check(doctor.code == 2 && doctor.output.Contains("tls-trust-or-name-failed") && !doctor.output.Contains(TlsPassword) && !doctor.error.Contains(TlsPassword), "actual published doctor retains production TLS rejection without leaking secrets");
            if (!OperatingSystem.IsWindows()) {
                Check(SignalProcess(process.Id, 15) == 0, "TLS CLI accepts service shutdown");
                using var timeout = new CancellationTokenSource(10000); await process.WaitForExitAsync(timeout.Token);
                Check(process.ExitCode == 0, "TLS standalone shutdown is clean and independent of stdin EOF");
            }
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        Check(string.IsNullOrWhiteSpace(await errors), "successful TLS service emits no private configuration or certificate errors");
        using var store = new RoomStore(room); var recovered = new Room(null, storage: store);
        Check(recovered.Snapshot.parts.Length == 3, "TLS process restart retains accepted durable edits and geometry");
    }
}
