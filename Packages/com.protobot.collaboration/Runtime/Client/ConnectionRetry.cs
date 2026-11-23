using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    // Explicit opt-in at safe call sites only. Never wrap admission, commands or
    // event reads. No queues, shared host/IP maps, route changes or reconnects.
    public static class ConnectionRetry {
        public const int MaxAttempts = 3, ReadDeadlineMilliseconds = 12000, TransferDeadlineMilliseconds = 120000;
        private static readonly object randomGate = new object();
        private static readonly Random random = new Random();
        public static bool Busy(string code) => code == "server-request-limit" || code == "room-request-limit" ||
            code == "asset-transfer-busy" || code == "server-transfer-busy";
        public static int CooldownMilliseconds(string code, int hint = 0) {
            int fallback = code == "join-rate-limit" ? 60000 : code == "room-full" ? 10000 : Busy(code) ? 1000 : 0;
            return fallback == 0 ? 0 : hint <= 0 ? fallback : Math.Max(250, Math.Min(60000, hint));
        }
        public static int DelayMilliseconds(int failedAttempt, int jitter, int hint = 0) =>
            Math.Max((failedAttempt <= 1 ? 250 : 500) + Math.Max(0, Math.Min(250, jitter)),
                hint <= 0 ? 0 : Math.Max(250, Math.Min(5000, hint)));
        // Accept only one bounded delta-seconds value, never dates or reflected
        // header text. Huge/malformed values use the fixed local default.
        public static int RetryHint(HttpResponseMessage response) {
            if (!response.Headers.TryGetValues("Retry-After", out var values)) return 0;
            var items = values.Take(2).ToArray();
            if (items.Length != 1 || items[0].Length == 0 || items[0].Length > 10 || !items[0].All(c => c >= '0' && c <= '9') ||
                !long.TryParse(items[0], out var seconds)) return 0;
            return (int)Math.Min(60000, seconds * 1000);
        }
        public static CollaborationException Rejection(HttpResponseMessage response, byte[] bytes) {
            NetworkPolicy.CheckProxyResponse(response);
            string code = null;
            try { code = WireFailure.Code(WireJson.Decode<WireError>(bytes)?.error); } catch { }
            if (bytes.Length == 0 && response.Headers.TryGetValues("X-Protobot-Rejection", out var headers)) {
                var values = headers.Take(2).ToArray();
                if (values.Length == 1 && values[0].Length <= 80) code = WireFailure.Code(values[0]);
            }
            code = code ?? "http-" + (int)response.StatusCode;
            if (((int)response.StatusCode == 401 || (int)response.StatusCode == 403) && Busy(code)) code = "http-" + (int)response.StatusCode;
            return new CollaborationException(code, CooldownMilliseconds(code, RetryHint(response)), (int)response.StatusCode);
        }
        public static string WaitingText(ConnectionProgress progress) =>
            "Waiting " + (progress.RetryAfterMilliseconds / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
            " s before safe retry " + progress.Attempt + " of " + MaxAttempts + " (" + progress.RetryCode + "). Cancel to stop.";

        public static Task<T> ReadAsync<T>(Func<int, CancellationToken, Task<T>> operation, CancellationToken cancellation,
            IProgress<ConnectionProgress> progress = null) => RunAsync(operation, false, cancellation, progress);
        public static Task<T> TransferAsync<T>(Func<int, CancellationToken, Task<T>> operation, CancellationToken cancellation,
            IProgress<ConnectionProgress> progress = null) => RunAsync(operation, true, cancellation, progress);
        private static async Task<T> RunAsync<T>(Func<int, CancellationToken, Task<T>> operation, bool transfer,
            CancellationToken cancellation, IProgress<ConnectionProgress> progress) {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(transfer ? TransferDeadlineMilliseconds : ReadDeadlineMilliseconds);
            for (int attempt = 1; ; attempt++) {
                cancellation.ThrowIfCancellationRequested();
                try {
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    if (!transfer) request.CancelAfter(4000);
                    var value = await operation(attempt, request.Token).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) throw new CollaborationException("safe-retry-exhausted");
                    return value;
                }
                catch (Exception error) {
                    cancellation.ThrowIfCancellationRequested();
                    string code = error is CollaborationException known ? known.Code : ConnectionDoctor.Code(error);
                    if (error is IOException && code != "tls-trust-or-name-failed" && !NetworkPolicy.IsCode(code)) code = "http-connection-failed";
                    bool network = code == "dns-failed" || code == "connection-refused" || code == "network-unreachable" ||
                        code == "http-connection-failed" || error is IOException || error is OperationCanceledException;
                    if (code == "tls-trust-or-name-failed" || NetworkPolicy.IsCode(code)) throw;
                    // Some CONNECT failures throw with a typed status rather than
                    // returning a response. No status-only response authorizes retry.
                    for (var cause = error; cause != null; cause = cause.InnerException)
                        if (cause is HttpRequestException && cause.GetType().GetProperty("StatusCode")?.GetValue(cause) is System.Net.HttpStatusCode) throw;
                    if (!network && !(Busy(code) && (transfer || code == "server-request-limit" || code == "room-request-limit"))) throw;
                    if (attempt == MaxAttempts || deadline.IsCancellationRequested) throw new CollaborationException("safe-retry-exhausted");
                    int jitter; lock (randomGate) jitter = random.Next(251);
                    int delay = DelayMilliseconds(attempt, jitter, (error as CollaborationException)?.RetryAfterMilliseconds ?? 0);
                    progress?.Report(new ConnectionProgress(ConnectionStage.WaitingToRetry, attempt: attempt + 1,
                        retryAfterMilliseconds: delay, retryCode: code));
                    try { await Task.Delay(delay, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); throw new CollaborationException("safe-retry-exhausted"); }
                }
            }
        }
        // Bounded read used by doctor and explicit operator reads. Each attempt
        // owns/disposes its response before waiting; bearer scope stays unchanged.
        public static Task<T> GetAsync<T>(HttpClient http, string url, CancellationToken cancellation, string credential = null,
            int maximum = 65536, IProgress<ConnectionProgress> progress = null) => ReadAsync(async (_, token) => {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (credential != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                NetworkPolicy.CheckProxyResponse(response);
                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
                int limit = response.IsSuccessStatusCode ? maximum : 16384;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0) {
                    DocumentModel.Require(output.Length + read <= limit, "invalid-server-response"); output.Write(buffer, 0, read);
                }
                if (!response.IsSuccessStatusCode) throw Rejection(response, output.ToArray());
                return WireJson.Decode<T>(output.ToArray());
            }, cancellation, progress);
    }
}
