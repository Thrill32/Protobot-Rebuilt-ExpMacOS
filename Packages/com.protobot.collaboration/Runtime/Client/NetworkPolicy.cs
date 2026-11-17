using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    // Local input only. Never embed in recovery, bootstrap, preferences, invites or reports.
    [Serializable] public sealed class NetworkConfiguration {
        public string format = "protobot-network";
        public int schema = 1;
        public string route = "system";
        public string authentication = "none";
        [System.Runtime.Serialization.OptionalField] public string proxy, username, password;
    }
    public sealed class NetworkPolicy {
        public const int MaxConfigurationBytes = 4096;
        public static readonly NetworkPolicy SystemDefault = new NetworkPolicy("system", null, null);
        public static readonly NetworkPolicy Direct = new NetworkPolicy("direct", null, null);
        public string Route { get; }
        public string Authentication => credentials == null ? "none" : "basic";
        private readonly Uri proxy;
        private readonly ICredentials credentials;
        private NetworkPolicy(string route, Uri address, ICredentials secret) { Route = route; proxy = address; credentials = secret; }
        public static NetworkPolicy Create(NetworkConfiguration value) {
            DocumentModel.Require(value != null && value.schema == 1 && value.format == "protobot-network", "proxy-config-version");
            DocumentModel.Require(value.route == "system" || value.route == "direct" || value.route == "http-proxy", "proxy-route-unsupported");
            DocumentModel.Require(value.authentication == "none" || value.authentication == "basic", "proxy-auth-unsupported");
            if (value.route != "http-proxy") {
                DocumentModel.Require(string.IsNullOrEmpty(value.proxy) && value.authentication == "none" && string.IsNullOrEmpty(value.username) && string.IsNullOrEmpty(value.password), "proxy-auth-route-mismatch");
                return value.route == "direct" ? Direct : SystemDefault;
            }
            DocumentModel.Require(value.proxy != null && value.proxy.Length <= 256 && !value.proxy.Any(char.IsControl) && Uri.TryCreate(value.proxy, UriKind.Absolute, out _), "proxy-address-invalid");
            var address = new Uri(value.proxy);
            DocumentModel.Require(address.Scheme == "http" && !string.IsNullOrEmpty(address.Host) && string.IsNullOrEmpty(address.UserInfo) &&
                address.AbsolutePath == "/" && string.IsNullOrEmpty(address.Query) && string.IsNullOrEmpty(address.Fragment), "proxy-address-invalid");
            ICredentials secret = null;
            if (value.authentication == "basic") {
                bool Valid(string text) => text != null && text.Length > 0 && text.Length <= 256 && text.All(c => c >= 32 && c <= 126);
                DocumentModel.Require(Valid(value.username) && !value.username.Contains(":") && Valid(value.password), "proxy-credentials-invalid");
                // A bare NetworkCredential can negotiate other schemes. This cache
                // offers Basic only, and only to the selected proxy authority.
                var cache = new CredentialCache(); cache.Add(address, "Basic", new NetworkCredential(value.username, value.password)); secret = cache;
            }
            else DocumentModel.Require(string.IsNullOrEmpty(value.username) && string.IsNullOrEmpty(value.password), "proxy-auth-route-mismatch");
            return new NetworkPolicy("http-proxy", address, secret);
        }
        public static Task<NetworkPolicy> LoadAsync(string path, Action<string> requirePrivate, CancellationToken cancellation = default) => Task.Run(async () => {
            try {
                cancellation.ThrowIfCancellationRequested();
                DocumentModel.Require(requirePrivate != null, "proxy-config-private-file-required"); requirePrivate(path);
                DocumentModel.Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "proxy-config-private-file-required");
                long length = new FileInfo(path).Length;
                DocumentModel.Require(length > 0 && length <= MaxConfigurationBytes, "proxy-config-size");
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                DocumentModel.Require(file.Length > 0 && file.Length <= MaxConfigurationBytes, "proxy-config-size");
                using var bytes = new MemoryStream(); var buffer = new byte[1024]; int read;
                while ((read = await file.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0) {
                    DocumentModel.Require(bytes.Length + read <= MaxConfigurationBytes, "proxy-config-size"); bytes.Write(buffer, 0, read);
                }
                cancellation.ThrowIfCancellationRequested(); return Create(WireJson.Decode<NetworkConfiguration>(bytes.ToArray()));
            }
            catch (OperationCanceledException) { throw; }
            catch (CollaborationException) { throw; }
            catch { throw new CollaborationException("proxy-config-unreadable"); }
        }, cancellation);
        private sealed class RouteProxy : IWebProxy {
            private readonly Uri address;
            public ICredentials Credentials { get; set; }
            public RouteProxy(Uri uri, ICredentials secret) { address = uri; Credentials = secret; }
            public Uri GetProxy(Uri destination) => address;
            public bool IsBypassed(Uri destination) => false;
        }
        public HttpClient CreateHttpClient() {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (Route == "direct") handler.UseProxy = false;
            else if (Route == "http-proxy") { handler.Proxy = new RouteProxy(proxy, credentials); handler.UseProxy = true; }
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
        // Check status before reading any body (including huge/reflected 407 HTML).
        public static void CheckProxyResponse(HttpResponseMessage response) {
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) throw new CollaborationException("proxy-authentication-required");
        }
        public static string FailureCode(Exception error) {
            for (var cause = error; cause != null; cause = cause.InnerException) {
                if (cause is CollaborationException known && IsCode(known.Code)) return known.Code;
                if (cause is WebException web && web.Response is HttpWebResponse response && (int)response.StatusCode == 407) return "proxy-authentication-required";
                // HttpRequestException.StatusCode is not in netstandard2.1/Unity.
                // Inspect only that typed numeric property; never parse exception text.
                if (cause is HttpRequestException && cause.GetType().GetProperty("StatusCode")?.GetValue(cause) is HttpStatusCode status && (int)status == 407) return "proxy-authentication-required";
            }
            return null;
        }
        public static bool IsCode(string code) => code != null && new[] { "proxy-config-version", "proxy-route-unsupported", "proxy-auth-unsupported", "proxy-auth-route-mismatch",
            "proxy-address-invalid", "proxy-credentials-invalid", "proxy-config-private-file-required", "proxy-config-size", "proxy-config-unreadable", "proxy-authentication-required" }.Contains(code);
        public override string ToString() => Route; // Never stringify an address or credential.
    }
}
