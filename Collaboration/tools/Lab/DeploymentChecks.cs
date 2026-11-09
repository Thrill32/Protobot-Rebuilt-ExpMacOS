using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task DeploymentChecks(string directory) {
        var seed = Fixture();
        var source = WireJson.Clone(seed.document); source.revision = 12;
        source.parts = new[] { Cube("exported", 17) }; source.parts[0].version = 4; source.parts[0].hasPaint = true;
        var content = new RoomExport { catalog = seed.catalog, document = source };
        string snapshot = Path.Combine(directory, "portable room.json"); RecoveryFiles.Write(snapshot, content);
        var fresh = content.CreateRoom("remote-team", "deployment-password", "new-owner-secret");
        Check(fresh.document.generation != source.generation && fresh.document.revision == 0 && fresh.document.parts[0].version == 1 && source.parts[0].version == 4,
            "portable room init starts a fresh generation/history without mutating the export");
        Check(fresh.document.parts[0].position[0] == 17 && fresh.document.parts[0].hasPaint && fresh.document.parts[0].id == "exported", "portable room preserves accepted content and stable part IDs");
        Reject(() => ConnectionDoctor.Origin("https://user:secret@example.org"), "invalid-endpoint");
        Reject(() => ConnectionDoctor.Origin("https://example.org/?token=secret"), "invalid-endpoint");
        Reject(() => ConnectionDoctor.Origin("http://192.0.2.1"), "https-required-use-explicit-lan-http");
        Check(ConnectionDoctor.Origin("http://[::1]:8787/") == "http://[::1]:8787", "doctor accepts a normalized IPv6 loopback origin");
        foreach (int port in new[] { 80, 443, 8443, 65535 }) {
            string origin = ConnectionDoctor.Origin("https://cad.example.org:" + port + "/");
            Check(new Uri(origin).Port == port && new UriBuilder(new Uri(origin)) { Scheme = "wss" }.Uri.Port == port,
                "HTTPS origins and WebSocket conversion preserve the selected port " + port);
        }
        string root = Path.Combine(directory, "initialized CLI room");
        string passwordFile = Path.Combine(directory, "private password.txt"); await File.WriteAllTextAsync(passwordFile, "deployment-password\r\n");
        string catalogFile = Path.Combine(directory, "catalog.json"); RecoveryFiles.Write(catalogFile, seed.catalog);
        var initialized = await RunCli("init", "--data-dir", root, "--room", "remote-team", "--snapshot", snapshot, "--password-file", passwordFile);
        Check(initialized.code == 0 && File.Exists(Path.Combine(root, "room.sqlite")) && File.Exists(Path.Combine(root, "owner.json")), "CLI init creates a durable room and protected owner capability from exported content");
        Check(!initialized.output.Contains("deployment-password") && !initialized.error.Contains("deployment-password"), "CLI initialization output excludes password contents");
        byte[] ownerBefore = File.ReadAllBytes(Path.Combine(root, "owner.json"));
        var duplicate = await RunCli("init", "--data-dir", root, "--room", "remote-team", "--catalog", catalogFile);
        Check(duplicate.code == 1 && duplicate.error.Contains("init-requires-new-directory") && File.ReadAllBytes(Path.Combine(root, "owner.json")).SequenceEqual(ownerBefore), "CLI init refuses to overwrite existing room data or credentials");
        var bad = await RunCli("init", "--data-dir", Path.Combine(directory, "never-created"), "--room", "a", "--catalog", catalogFile, "--unknown");
        Check(bad.code == 1 && !Directory.Exists(Path.Combine(directory, "never-created")), "unknown CLI options fail before creating room data");
        string empty = Path.Combine(directory, "empty CLI room");
        Check((await RunCli("init", "--data-dir", empty, "--room", "empty", "--catalog", catalogFile)).code == 0, "CLI can initialize an empty room from the matching catalog");
        using (var store = new RoomStore(empty)) Check(new Room(null, storage: store).Snapshot.parts.Length == 0, "empty catalog init restores through the same RoomStore");
        await using (var child = await RecoveryChild.Start(root, "http://127.0.0.1:0", null)) {
            Check(child.Ready.recovered && child.Ready.revision == 0 && child.Ready.room == "remote-team", "standalone CLI reopens an initialized room without bootstrap configuration");
            string reportPath = Path.Combine(directory, "connection-report.json");
            var diagnostic = await RunCli("doctor", "--url", child.Ready.url, "--catalog", catalogFile, "--room", "remote-team", "--password-file", passwordFile, "--transport", "requests", "--quick", "--output", reportPath);
            var report = RecoveryFiles.Read<ConnectionReport>(reportPath);
            Check(diagnostic.code == 0 && report.passed && report.checks.Any(c => c.name == "requests" && c.status == "passed") && report.checks.Any(c => c.name == "requests-idle-events" && c.status == "skipped"), "CLI doctor authenticates a real read-only client and labels skipped idle checks");
            Check(!diagnostic.output.Contains("deployment-password") && !diagnostic.error.Contains("deployment-password") && !File.ReadAllText(reportPath).Contains("credential"), "doctor JSON, file and progress output redact credentials");
            var capabilities = await RunCli("doctor", "--url", child.Ready.url);
            var caps = WireJson.Decode<ConnectionReport>(Encoding.UTF8.GetBytes(capabilities.output));
            Check(capabilities.code == 0 && caps.checks.Any(c => c.name == "authenticated-transports" && c.status == "skipped"), "capability-only doctor explicitly skips authenticated transport acceptance");
            var mismatch = await RunCli("doctor", "--url", child.Ready.url, "--catalog", catalogFile, "--room", "wrong-room", "--quick");
            Check(mismatch.code == 2 && mismatch.output.Contains("catalog-or-room-mismatch"), "doctor returns a failing exit code for wrong-room compatibility");
            await File.WriteAllTextAsync(passwordFile, "wrong-secret");
            var wrong = await RunCli("doctor", "--url", child.Ready.url, "--catalog", catalogFile, "--password-file", passwordFile, "--transport", "requests", "--quick");
            Check(wrong.code == 2 && wrong.output.Contains("wrong-password") && !wrong.output.Contains("wrong-secret"), "doctor reports rejected authentication without leaking the entered password");
            using var observer = new CollaborationClient(child.Ready.url, "remote-team", seed.catalog, CollaborationTransport.LongPolling);
            await observer.JoinAsync("Observer", "deployment-password");
            Check(observer.Snapshot.revision == 0 && observer.Snapshot.parts.Single().position[0] == 17, "read-only doctor leaves accepted content/revision unchanged");
            await observer.LeaveAsync();
        }
        await StandaloneProcessChecks(root);
        await using (var server = new CollaborationServer(seed)) {
            await server.StartAsync();
            var direct = await ConnectionDoctor.RunAsync(server.Url, seed.catalog, "team", transports: new[] { CollaborationTransport.WebSocket }, checkIdle: false);
            Check(direct.passed && direct.checks.Any(c => c.name == "websocket" && c.status == "passed"), "doctor proves a real authenticated WebSocket event frame");
            await using var proxy = new LoopbackProxy(server.Url); await proxy.StartAsync();
            var blocked = await ConnectionDoctor.RunAsync(proxy.Url, seed.catalog, "team", checkIdle: false);
            Check(!blocked.passed && blocked.checks.Any(c => c.name == "websocket" && c.status == "failed") && blocked.checks.Any(c => c.name == "requests" && c.status == "passed"), "doctor distinguishes blocked upgrades from working HTTP requests");
            var automatic = await ConnectionDoctor.RunAsync(proxy.Url, seed.catalog, "team", transports: new[] { CollaborationTransport.Auto }, checkIdle: false);
            Check(automatic.passed && automatic.checks.Any(c => c.name == "auto-selected-transport" && c.code == "long-polling"), "doctor exercises the shared client's actual Auto fallback");
            proxy.RejectedJoinError = "echoed-diagnostic-password";
            var echoed = await ConnectionDoctor.RunAsync(proxy.Url, seed.catalog, "team", password: proxy.RejectedJoinError, transports: new[] { CollaborationTransport.LongPolling }, checkIdle: false);
            Check(!echoed.passed && echoed.checks.Any(c => c.code == "server-rejected-request") && !WireJson.Text(echoed).Contains(proxy.RejectedJoinError), "doctor cannot leak a password echoed in an untrusted server/proxy error body");
            proxy.RejectedJoinError = null;
            var sustained = await ConnectionDoctor.RunAsync(proxy.Url, seed.catalog, "team", transports: new[] { CollaborationTransport.LongPolling });
            Check(sustained.passed && sustained.checks.Single(c => c.name == "requests-idle-events").milliseconds >= 21000, "doctor sustains held HTTP requests and receives the server's idle event batch");
            var monitor = server.Room.Join(new JoinRequest { name = "Monitor", catalogHash = seed.catalog.hash, viewer = true });
            var presence = await server.Room.ReadAsync(monitor.token, 0, -1, CancellationToken.None);
            Check(presence.participants.All(p => p.name != "Connection check"), "doctor revokes every successfully joined test membership");
            server.Room.Leave(monitor.token);
        }
        await HttpsDoctorChecks(seed);
    }
    [DllImport("libc", EntryPoint = "kill")] private static extern int SignalProcess(int pid, int signal);
    private static async Task StandaloneProcessChecks(string directory) {
        var start = new ProcessStartInfo { FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { Path.GetFullPath("src/Cli/bin/Debug/net10.0/protobot-collab.dll"), "serve", "--data-dir", directory,
            "--listen", "http://127.0.0.1:0", "--public-url", "https://cad.example.org:80" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        var errors = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
        try {
            var line = process.StandardOutput.ReadLineAsync();
            Check(await Task.WhenAny(line, Task.Delay(15000)) == line, "standalone CLI readiness is bounded");
            using var ready = System.Text.Json.JsonDocument.Parse(await line ?? "{}");
            Check(ready.RootElement.GetProperty("publicUrl").GetString() == "https://cad.example.org:80" && ready.RootElement.GetProperty("url").GetString().StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
                "standalone readiness distinguishes the advertised HTTPS origin from its bound listener");
            await Task.Delay(100); Check(!process.HasExited, "standalone service survives stdin EOF");
            if (!OperatingSystem.IsWindows()) {
                Check(SignalProcess(process.Id, 15) == 0, "standalone service receives the systemd termination signal");
                using var timeout = new CancellationTokenSource(10000); await process.WaitForExitAsync(timeout.Token);
                Check(process.ExitCode == 0, "standalone service shuts down cleanly on SIGTERM");
            }
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } await errors; }
    }
    private static async Task<(int code, string output, string error)> RunCli(params string[] arguments) {
        var start = new ProcessStartInfo { FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath("src/Cli/bin/Debug/net10.0/protobot-collab.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
        try {
            using var timeout = new CancellationTokenSource(45000); await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
    private static async Task HttpsDoctorChecks(Bootstrap seed) {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("https://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        await using var app = builder.Build();
        app.MapGet("/capabilities", context => context.Response.WriteAsync(WireJson.Text(seed.catalog))); await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
        var result = await ConnectionDoctor.RunAsync(address);
        Check(!result.passed && result.checks.Single(c => c.name == "capabilities").code == "tls-trust-or-name-failed", "doctor rejects an untrusted HTTPS certificate without any trust bypass");
        await app.StopAsync();
    }
}
