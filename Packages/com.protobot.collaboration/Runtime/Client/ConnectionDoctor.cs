using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    [Serializable] public sealed class ConnectionCheck {
        public string name, status, code;
        public long milliseconds;
    }
    [Serializable] public sealed class ConnectionReport {
        public bool passed;
        public string version = CollaborationProtocol.Version;
        public string room, serverVersion, catalogHash;
        public bool durable;
        public ConnectionCheck[] checks = Array.Empty<ConnectionCheck>();
    }
    public static class ConnectionDoctor {
        private static readonly HashSet<string> KnownCodes = new HashSet<string>(StringComparer.Ordinal) {
            "invalid-endpoint", "https-required-use-explicit-lan-http", "server-version-mismatch", "invalid-capabilities", "endpoint-changed",
            "catalog-or-room-mismatch", "diagnostic-role-mismatch", "event-stream-timeout", "event-stream-disconnected", "wrong-password",
            "invalid-session", "room-full", "join-rate-limit", "invalid-name", "invalid-request", "message-too-large", "schema-mismatch",
            "catalog-mismatch", "room-storage-failed-restart-required", "protocol-mismatch", "catalog-protocol-mismatch"
        };
        public static string Origin(string value, bool allowInsecureLan = false) {
            DocumentModel.Require(value != null && value.Length <= 256 && Uri.TryCreate(value.Trim(), UriKind.Absolute, out _), "invalid-endpoint");
            var uri = new Uri(value.Trim());
            DocumentModel.Require(string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" && !string.IsNullOrEmpty(uri.Host), "invalid-endpoint");
            DocumentModel.Require(uri.Scheme == "https" || uri.Scheme == "http" && (uri.IsLoopback || allowInsecureLan), "https-required-use-explicit-lan-http");
            return uri.GetLeftPart(UriPartial.Authority);
        }
        // Supplying a catalog opts into authenticated, read-only room probes.
        // Every probe uses the actual shared client and revokes its test membership.
        public static async Task<ConnectionReport> RunAsync(string origin, CatalogManifest catalog = null, string room = null, string password = "",
            CollaborationTransport[] transports = null, bool allowInsecureLan = false, bool checkIdle = true, Action<string> progress = null, CancellationToken cancellation = default) {
            origin = Origin(origin, allowInsecureLan);
            var report = new ConnectionReport(); var checks = new List<ConnectionCheck>();
            async Task<bool> Check(string name, Func<Task> action) {
                progress?.Invoke("Checking " + name + "..."); var timer = Stopwatch.StartNew();
                try { await action().ConfigureAwait(false); checks.Add(new ConnectionCheck { name = name, status = "passed", milliseconds = timer.ElapsedMilliseconds }); return true; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) { checks.Add(new ConnectionCheck { name = name, status = "failed", code = Code(error), milliseconds = timer.ElapsedMilliseconds }); return false; }
            }
            Capabilities caps = null;
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) }) {
                if (await Check("capabilities", async () => {
                    caps = await Get<Capabilities>(http, origin + "/capabilities", cancellation).ConfigureAwait(false);
                    DocumentModel.Require(caps != null && caps.protocol == 1 && caps.version == CollaborationProtocol.Version, "server-version-mismatch");
                    DocumentModel.Require(DocumentModel.ValidId(caps.room) && !string.IsNullOrEmpty(caps.catalogHash) && caps.catalogHash.Length <= 128 && !caps.catalogHash.Any(char.IsControl), "invalid-capabilities");
                    report.room = caps.room; report.serverVersion = caps.version; report.catalogHash = caps.catalogHash; report.durable = caps.durable;
                }).ConfigureAwait(false)) {
                    await Check("health", async () => {
                        var health = await Get<Capabilities>(http, origin + "/health", cancellation).ConfigureAwait(false);
                        DocumentModel.Require(health.room == caps.room && health.catalogHash == caps.catalogHash && health.version == caps.version, "endpoint-changed");
                    }).ConfigureAwait(false);
                    if (catalog != null && await Check("compatibility", () => {
                        DocumentModel.Require(catalog.hash == caps.catalogHash && (room == null || room == caps.room), "catalog-or-room-mismatch"); return Task.CompletedTask;
                    }).ConfigureAwait(false)) {
                        foreach (var transport in transports ?? new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling }) {
                            string label = transport == CollaborationTransport.WebSocket ? "websocket" : transport == CollaborationTransport.LongPolling ? "requests" : "auto";
                            using (var client = new CollaborationClient(origin, caps.room, catalog, transport, allowInsecureLan)) {
                                int frames = 0;
                                var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                var idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                client.Presence += _ => { if (Interlocked.Increment(ref frames) == 1) first.TrySetResult(true); else idle.TrySetResult(true); };
                                client.Disconnected += reason => {
                                    if (Volatile.Read(ref frames) == 0) first.TrySetException(new CollaborationException(reason));
                                    else if (checkIdle) idle.TrySetException(new CollaborationException(reason));
                                };
                                bool joined = false;
                                try {
                                    bool connected = await Check(label, async () => {
                                        await client.JoinAsync("Connection check", password, viewer: true, cancellation: cancellation).ConfigureAwait(false); joined = true;
                                        await Wait(first.Task, 12000, cancellation).ConfigureAwait(false);
                                        DocumentModel.Require(client.Participant.role == "viewer", "diagnostic-role-mismatch");
                                    }).ConfigureAwait(false);
                                    if (connected && checkIdle) await Check(label + "-idle-events", async () => {
                                        await Task.Delay(21000, cancellation).ConfigureAwait(false);
                                        await Wait(idle.Task, 5000, cancellation).ConfigureAwait(false);
                                        DocumentModel.Require(client.IsConnected, "event-stream-disconnected");
                                    }).ConfigureAwait(false);
                                    else checks.Add(new ConnectionCheck { name = label + "-idle-events", status = "skipped", code = connected ? "quick-check-only" : "connection-failed" });
                                    if (connected && transport == CollaborationTransport.Auto) checks.Add(new ConnectionCheck { name = "auto-selected-transport", status = "passed", code = client.ActiveTransport });
                                }
                                finally {
                                    if (joined) await Check(label + "-leave", () => client.LeaveAsync()).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
            }
            if (catalog == null) checks.Add(new ConnectionCheck { name = "authenticated-transports", status = "skipped", code = "provide-matching-catalog" });
            report.checks = checks.ToArray(); report.passed = checks.All(c => c.status != "failed"); return report;
        }
        private static async Task Wait(Task task, int milliseconds, CancellationToken cancellation) {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                var delay = Task.Delay(milliseconds, timeout.Token);
                if (await Task.WhenAny(task, delay).ConfigureAwait(false) != task) { cancellation.ThrowIfCancellationRequested(); throw new CollaborationException("event-stream-timeout"); }
                timeout.Cancel(); await task.ConfigureAwait(false);
            }
        }
        private static async Task<T> Get<T>(HttpClient http, string url, CancellationToken cancellation) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(12)); cancellation = timeout.Token;
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false)) {
                DocumentModel.Require(response.IsSuccessStatusCode, "http-" + (int)response.StatusCode);
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream()) {
                    var buffer = new byte[4096]; int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0) {
                        DocumentModel.Require(output.Length + count <= 65536, "invalid-capabilities"); output.Write(buffer, 0, count);
                    }
                    return WireJson.Decode<T>(output.ToArray());
                }
            }
        }
        public static string Code(Exception error) {
            if (error is CollaborationException known) {
                if (known.Code != null && known.Code.StartsWith("Connection ", StringComparison.Ordinal)) return "event-stream-disconnected";
                if (known.Code != null && (KnownCodes.Contains(known.Code) || known.Code.Length == 8 && known.Code.StartsWith("http-", StringComparison.Ordinal) && known.Code.Substring(5).All(c => c >= '0' && c <= '9'))) return known.Code;
                // A proxy/server error body is untrusted and can echo submitted
                // credentials. Reports retain only recognized diagnostic codes.
                return "server-rejected-request";
            }
            for (var cause = error; cause != null; cause = cause.InnerException) {
                if (cause is AuthenticationException) return "tls-trust-or-name-failed";
                if (cause is SocketException socket) {
                    if (socket.SocketErrorCode == SocketError.HostNotFound || socket.SocketErrorCode == SocketError.NoData || socket.SocketErrorCode == SocketError.TryAgain) return "dns-failed";
                    if (socket.SocketErrorCode == SocketError.ConnectionRefused) return "connection-refused";
                    return "network-unreachable";
                }
            }
            if (error is OperationCanceledException) return "request-timeout";
            if (error is System.Net.WebSockets.WebSocketException) return "websocket-unavailable";
            if (error is HttpRequestException) return "http-connection-failed";
            return "invalid-server-response";
        }
    }
}
