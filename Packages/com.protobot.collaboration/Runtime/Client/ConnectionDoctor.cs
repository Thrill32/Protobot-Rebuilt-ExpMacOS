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
        public string format = "protobot-connection-report";
        public int schema = 1;
        public DiagnosticReport[] diagnostics = Array.Empty<DiagnosticReport>();
        public bool passed;
        public string version = CollaborationProtocol.Version;
        public string room, serverVersion, catalogHash;
        public bool durable;
        public ConnectionCheck[] checks = Array.Empty<ConnectionCheck>();
    }
    public static class ConnectionDoctor {
        private static readonly HashSet<string> KnownCodes = new HashSet<string>(StringComparer.Ordinal) {
            "connection-attempt-timeout", "safe-retry-exhausted", "room-request-limit", "short-polling-unavailable", "invalid-event-wait", "invalid-endpoint", "https-required-use-explicit-lan-http", "server-version-mismatch", "invalid-capabilities", "endpoint-changed",
            "server-stopping", "catalog-or-room-mismatch", "diagnostic-role-mismatch", "event-stream-timeout", "event-stream-disconnected", "wrong-password",
            "invalid-session", "room-full", "join-rate-limit", "invalid-name", "invalid-request", "message-too-large", "schema-mismatch",
            "room-required", "room-not-found", "room-unavailable", "server-request-limit", "server-event-limit", "room-event-limit",
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
            CollaborationTransport[] transports = null, bool allowInsecureLan = false, bool checkIdle = true, Action<string> progress = null, CancellationToken cancellation = default, NetworkPolicy networkPolicy = null) {
            networkPolicy = networkPolicy ?? NetworkPolicy.SystemDefault;
            origin = Origin(origin, allowInsecureLan);
            DocumentModel.Require(transports == null || transports.Length > 0 && transports.Length <= 4 && transports.Distinct().Count() == transports.Length &&
                transports.All(t => Enum.IsDefined(typeof(CollaborationTransport), t)), "invalid-transport");
            var report = new ConnectionReport(); var checks = new List<ConnectionCheck>(); var diagnostics = new List<DiagnosticReport>();
            async Task<bool> Check(string name, Func<Task> action) {
                progress?.Invoke("Checking " + name + "..."); var timer = Stopwatch.StartNew();
                try { await action().ConfigureAwait(false); checks.Add(new ConnectionCheck { name = name, status = "passed", milliseconds = timer.ElapsedMilliseconds }); return true; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) {
                    string code = Code(error);
                    if (error is CollaborationException rejection) {
                        int delay = ConnectionRetry.CooldownMilliseconds(code, rejection.RetryAfterMilliseconds);
                        if (delay > 0) progress?.Invoke("Admission/read rejected (" + code + "). Retry explicitly after " + ((delay + 999) / 1000) + " s.");
                    }
                    checks.Add(new ConnectionCheck { name = name, status = "failed", code = code, milliseconds = timer.ElapsedMilliseconds }); return false;
                }
            }
            DocumentModel.Require(room == null || DocumentModel.ValidId(room), "invalid-room");
            Capabilities caps = null; string healthPath = "/health";
            using (var http = networkPolicy.CreateHttpClient()) {
                if (await Check("capabilities", async () => {
                    caps = await Get<Capabilities>(http, origin + "/capabilities", cancellation, progress).ConfigureAwait(false);
                    DocumentModel.Require(caps != null && caps.protocol == 1 && caps.version == CollaborationProtocol.Version, "server-version-mismatch");
                    if (caps.multiRoom && room == null) {
                        DocumentModel.Require(catalog == null, "room-required");
                        report.serverVersion = caps.version; report.durable = caps.durable; return;
                    }
                    DocumentModel.Require(caps.multiRoom || room == null || room == caps.room, "catalog-or-room-mismatch");
                    string selected = room ?? caps.room;
                    DocumentModel.Require(DocumentModel.ValidId(selected), "invalid-capabilities");
                    caps = await Get<Capabilities>(http, origin + "/rooms/" + selected + "/capabilities", cancellation, progress).ConfigureAwait(false);
                    DocumentModel.Require(caps.room == selected && caps.protocol == 1 && caps.version == CollaborationProtocol.Version, "endpoint-changed");
                    healthPath = "/rooms/" + selected + "/health";
                    DocumentModel.Require(DocumentModel.ValidId(caps.room) && !string.IsNullOrEmpty(caps.catalogHash) && caps.catalogHash.Length <= 128 && !caps.catalogHash.Any(char.IsControl), "invalid-capabilities");
                    report.room = caps.room; report.serverVersion = caps.version; report.catalogHash = DiagnosticExport.Catalog(caps.catalogHash); report.durable = caps.durable;
                }).ConfigureAwait(false)) {
                    bool healthy = await Check("health", async () => {
                        var health = await Get<Capabilities>(http, origin + healthPath, cancellation, progress).ConfigureAwait(false);
                        DocumentModel.Require(health.room == caps.room && health.catalogHash == caps.catalogHash && health.version == caps.version, "endpoint-changed");
                    }).ConfigureAwait(false);
                    if (healthy && catalog != null && await Check("compatibility", () => {
                        DocumentModel.Require(catalog.hash == caps.catalogHash && (room == null || room == caps.room), "catalog-or-room-mismatch"); return Task.CompletedTask;
                    }).ConfigureAwait(false)) {
                        foreach (var transport in transports ?? (caps.shortPollingVersion == CollaborationProtocol.ShortPollingVersion ?
                            new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling } :
                            new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling })) {
                            string label = transport == CollaborationTransport.WebSocket ? "websocket" : transport == CollaborationTransport.LongPolling ? "requests" : transport == CollaborationTransport.ShortPolling ? "short-polling" : "auto";
                            using (var client = new CollaborationClient(origin, caps.room, catalog, transport, allowInsecureLan, networkPolicy: networkPolicy)) {
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
                                        await client.JoinAsync("Connection check", password, viewer: true, cancellation: cancellation, progress: new CallbackProgress(value => { if (value.Stage == ConnectionStage.WaitingToRetry) progress?.Invoke(ConnectionRetry.WaitingText(value)); })).ConfigureAwait(false); joined = true;
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
                                    var diagnostic = client.CaptureDiagnostics(); diagnostic.source = "doctor"; diagnostics.Add(diagnostic);
                                    if (joined) await Check(label + "-leave", () => client.LeaveAsync()).ConfigureAwait(false);
                                }
                                // A failed admission may have created a membership. No
                                // later transport probe may submit another fresh join.
                                if (!joined) break;
                            }
                        }
                    }
                }
            }
            if (catalog == null) checks.Add(new ConnectionCheck { name = "authenticated-transports", status = "skipped", code = "provide-matching-catalog" });
            report.diagnostics = diagnostics.ToArray(); report.checks = checks.ToArray(); report.passed = checks.All(c => c.status != "failed"); return report;
        }
        private static async Task Wait(Task task, int milliseconds, CancellationToken cancellation) {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                var delay = Task.Delay(milliseconds, timeout.Token);
                if (await Task.WhenAny(task, delay).ConfigureAwait(false) != task) { cancellation.ThrowIfCancellationRequested(); throw new CollaborationException("event-stream-timeout"); }
                timeout.Cancel(); await task.ConfigureAwait(false);
            }
        }
        private sealed class CallbackProgress : IProgress<ConnectionProgress> {
            private readonly Action<ConnectionProgress> callback;
            public CallbackProgress(Action<ConnectionProgress> callback) { this.callback = callback; }
            public void Report(ConnectionProgress value) => callback(value);
        }
        private static Task<T> Get<T>(HttpClient http, string url, CancellationToken cancellation, Action<string> progress) =>
            ConnectionRetry.GetAsync<T>(http, url, cancellation, progress: new CallbackProgress(value => progress?.Invoke(ConnectionRetry.WaitingText(value))));
        public static string Code(Exception error) {
            string proxyCode = NetworkPolicy.FailureCode(error); if (proxyCode != null) return proxyCode;
            if (error is CollaborationException known) {
                if (NetworkPolicy.IsCode(known.Code)) return known.Code;
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
