using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Test-only forward proxy. Fixed upstream, loopback listener, bounded headers and
// connection lifetime. Raw duplex forwarding also exercises WebSocket and CONNECT.
internal sealed class OutboundProxyFixture : IDisposable {
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly Uri upstream;
    private readonly string expectedBasic;
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new ConcurrentDictionary<TcpClient, byte>();
    public readonly ConcurrentQueue<string> Paths = new ConcurrentQueue<string>();
    public string Url { get; }
    public int Connections, Forwarded, Connects, Challenges, Authenticated;
    public bool RejectAuthentication { get; set; }
    public bool RejectAll { get; set; }
    public bool Pause { get; set; }
    public string ReflectedError = "untrusted-proxy-error";
    public string ChallengeScheme { get; set; } = "Basic";
    public string Redirect { get; set; }
    public bool OriginCredentialInConnect, ProxyCredentialInOrigin;
    public OutboundProxyFixture(string origin, string username = null, string password = null) {
        upstream = new Uri(origin); expectedBasic = username == null ? null : "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(username + ":" + password));
        listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Accept();
    }
    private async Task Accept() {
        while (!stop.IsCancellationRequested) {
            try { var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); clients.TryAdd(client, 0); Interlocked.Increment(ref Connections); _ = Handle(client); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
        }
    }
    private async Task Handle(TcpClient client) {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
            timeout.CancelAfter(TimeSpan.FromMinutes(2)); var cancellation = timeout.Token;
            try {
                var input = client.GetStream(); using var header = new MemoryStream(); var one = new byte[1];
                while (true) {
                    if (await input.ReadAsync(one, 0, 1, cancellation).ConfigureAwait(false) == 0) return;
                    header.WriteByte(one[0]); if (header.Length > 32768) return;
                    var bytes = header.GetBuffer(); int n = (int)header.Length;
                    if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' '); if (first.Length != 3) return;
                string auth = lines.FirstOrDefault(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))?.Substring(20).Trim();
                bool connect = first[0] == "CONNECT";
                if (connect) { Interlocked.Increment(ref Connects); if (lines.Any(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))) OriginCredentialInConnect = true; }
                while (Pause) await Task.Delay(20, cancellation).ConfigureAwait(false);
                if (RejectAll || RejectAuthentication || expectedBasic != null && auth != expectedBasic) {
                    Interlocked.Increment(ref Challenges);
                    string body = ReflectedError;
                    await Write(input, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: " + ChallengeScheme + " realm=\"fixture\"\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body, cancellation).ConfigureAwait(false); return;
                }
                if (auth != null) Interlocked.Increment(ref Authenticated);
                if (Redirect != null) { await Write(input, "HTTP/1.1 302 Found\r\nLocation: " + Redirect + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellation).ConfigureAwait(false); return; }
                Uri target;
                if (connect) target = new Uri("http://" + first[1]);
                else if (!Uri.TryCreate(first[1], UriKind.Absolute, out target)) return;
                if (target.Host != upstream.Host || target.Port != upstream.Port) { await Write(input, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellation); return; }
                using var remote = new TcpClient(); using (cancellation.Register(() => remote.Close())) await remote.ConnectAsync(upstream.Host, upstream.Port).ConfigureAwait(false);
                var output = remote.GetStream(); Interlocked.Increment(ref Forwarded);
                if (connect) await Write(input, "HTTP/1.1 200 Connection Established\r\n\r\n", cancellation).ConfigureAwait(false);
                else {
                    Paths.Enqueue(first[0] + " " + target.PathAndQuery);
                    bool upgrade = lines.Any(l => l.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));
                    var forwarded = lines.Skip(1).Where(l => l.Length > 0 && !l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) && (upgrade || !l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))).ToList();
                    if (!upgrade) forwarded.Add("Connection: close");
                    ProxyCredentialInOrigin |= forwarded.Any(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                    await Write(output, first[0] + " " + target.PathAndQuery + " " + first[2] + "\r\n" + string.Join("\r\n", forwarded) + "\r\n\r\n", cancellation).ConfigureAwait(false);
                }
                var send = input.CopyToAsync(output, 8192, cancellation); var receive = output.CopyToAsync(input, 8192, cancellation);
                await Task.WhenAny(send, receive).ConfigureAwait(false); timeout.Cancel();
                try { await Task.WhenAll(send, receive).ConfigureAwait(false); } catch { }
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is OperationCanceledException || error is ObjectDisposedException) { }
            finally { clients.TryRemove(client, out _); }
        }
    }
    private static Task Write(Stream stream, string text, CancellationToken token) { var bytes = Encoding.UTF8.GetBytes(text); return stream.WriteAsync(bytes, 0, bytes.Length, token); }
    public void Dispose() { stop.Cancel(); listener.Stop(); foreach (var client in clients.Keys) client.Close(); }
}

internal sealed class UntrustedTlsFixture : IDisposable {
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly X509Certificate2 certificate;
    private readonly byte[] certificateBytes;
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new ConcurrentDictionary<TcpClient, byte>();
    public string Url { get; }
    public UntrustedTlsFixture() {
#if NET10_0_OR_GREATER
        using (var key = RSA.Create(2048)) {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            certificateBytes = temporary.Export(X509ContentType.Pfx); certificate = X509CertificateLoader.LoadPkcs12(certificateBytes, null);
        }
#else
        // Unity's reference assemblies expose certificate-construction APIs that
        // its Mono runtime lacks. The lab generates this untrusted test-only PFX;
        // no certificate is installed into a trust store.
        certificateBytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("PROTOBOT_TEST_CERTIFICATE")); certificate = new X509Certificate2(certificateBytes, "");
#endif
        listener.Start(); Url = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; _ = Accept();
    }
    public void Export(string path) => File.WriteAllBytes(path, certificateBytes);
    private async Task Accept() {
        while (!stop.IsCancellationRequested) {
            try { var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); clients.TryAdd(client, 0); _ = Handshake(client); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
        }
    }
    private async Task Handshake(TcpClient client) {
        using (client) using (var ssl = new SslStream(client.GetStream())) {
            try { await ssl.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false); }
            catch (Exception error) when (error is AuthenticationException || error is IOException || error is ObjectDisposedException) { }
            finally { clients.TryRemove(client, out _); }
        }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); foreach (var client in clients.Keys) client.Close(); certificate.Dispose(); }
}
