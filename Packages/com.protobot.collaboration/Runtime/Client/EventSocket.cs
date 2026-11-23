using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    public sealed partial class CollaborationClient {
        // Use the same redirect-disabled handler for the upgrade and every request.
        // Framing/heartbeat/cancellation remain the runtime WebSocket implementation.
        private async Task<WebSocket> OpenEventSocketAsync(CancellationToken cancellation) {
            var nonce = new byte[16]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
            string key = Convert.ToBase64String(nonce);
            using var request = new HttpRequestMessage(HttpMethod.Get, EventUri());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Connection", "Upgrade"); request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13"); request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", key);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            try {
                NetworkPolicy.CheckProxyResponse(response);
                // A stopping listener can reject before an upgrade is established.
                // Keep this terminal reason out of Auto's upgrade fallback path.
                if ((int)response.StatusCode == 503 && response.Headers.TryGetValues("X-Protobot-Rejection", out var reasons) && reasons.SequenceEqual(new[] { "server-stopping" }))
                    throw new CollaborationException("server-stopping");
                DocumentModel.Require((int)response.StatusCode == 101 && response.Headers.Upgrade.Any(u => string.Equals(u.Name, "websocket", StringComparison.OrdinalIgnoreCase)) &&
                    response.Headers.Connection.Any(c => string.Equals(c, "Upgrade", StringComparison.OrdinalIgnoreCase)), "websocket-unavailable");
                string expected;
                using (var hash = SHA1.Create()) expected = Convert.ToBase64String(hash.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                DocumentModel.Require(response.Headers.TryGetValues("Sec-WebSocket-Accept", out var accepted) && accepted.SequenceEqual(new[] { expected }), "websocket-unavailable");
                var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                DocumentModel.Require(stream.CanRead && stream.CanWrite, "websocket-unavailable");
                return WebSocket.CreateFromStream(stream, false, null, TimeSpan.FromSeconds(30));
            }
            catch { response.Dispose(); throw; }
            // The socket owns the upgraded stream. Disposing the response here
            // would close it; disposal of the socket closes the connection.
        }
    }
}
