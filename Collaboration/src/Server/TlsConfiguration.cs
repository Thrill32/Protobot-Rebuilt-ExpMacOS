using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration.Server {
    // Operator-local input only. Never serialize this object into readiness,
    // bootstrap, recovery, invitations or diagnostics. The caller owns its lifetime.
    public sealed class TlsConfiguration : IDisposable {
        public const int Version = 1, MaxConfigurationBytes = 4096, MaxCertificateBytes = 1024 * 1024;
        internal X509Certificate2 Certificate { get; private set; }
        internal X509Certificate2Collection Chain { get; private set; }
        private X509Certificate2Collection certificates;
        private sealed class Input {
            [JsonRequired] public string format { get; set; }
            [JsonRequired] public int schema { get; set; }
            [JsonRequired] public string certificate { get; set; }
            [JsonRequired] public string password { get; set; }
        }
        public static async Task<TlsConfiguration> LoadAsync(string path, CancellationToken cancellation = default) {
            byte[] json = null, pfx = null;
            var result = new TlsConfiguration();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try {
                json = await ReadPrivate(path, MaxConfigurationBytes, "tls-config", deadline.Token);
                Input input;
                try {
                    // Refuse duplicates as well as unknown fields; never let two
                    // tools interpret a private configuration differently.
                    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
                    DocumentModel.Require(document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == document.RootElement.EnumerateObject().Count(), "tls-config-invalid");
                    input = JsonSerializer.Deserialize<Input>(json, new JsonSerializerOptions { MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
                }
                catch (JsonException) { throw new CollaborationException("tls-config-invalid"); }
                DocumentModel.Require(input != null && input.format == "protobot-tls" && input.schema == Version, "tls-config-version");
                DocumentModel.Require(!string.IsNullOrWhiteSpace(input.certificate) && input.certificate.Length <= 2048 && !input.certificate.Any(char.IsControl) &&
                    input.password != null && input.password.Length <= 1024 && !input.password.Contains('\0'), "tls-config-invalid");
                string certificatePath;
                try { certificatePath = Path.GetFullPath(input.certificate, Path.GetDirectoryName(Path.GetFullPath(path))); }
                catch (Exception error) when (error is ArgumentException || error is NotSupportedException) { throw new CollaborationException("tls-config-invalid"); }
                pfx = await ReadPrivate(certificatePath, MaxCertificateBytes, "tls-certificate", deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                try {
                    // Retain the runtime KDF limits and bound certificate/key counts.
                    // DefaultKeySet supports macOS temporary keychains; never persist
                    // or export a key into an operator's certificate store.
                    result.certificates = X509CertificateLoader.LoadPkcs12Collection(pfx, input.password,
                        X509KeyStorageFlags.DefaultKeySet, new Pkcs12LoaderLimits { MaxCertificates = 8, MaxKeys = 1 });
                }
                catch (CryptographicException) { throw new CollaborationException("tls-certificate-invalid-or-password"); }
                result.Certificate = result.certificates.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey);
                DocumentModel.Require(result.Certificate != null, "tls-certificate-private-key-required");
                DateTime now = DateTime.UtcNow;
                DocumentModel.Require(result.Certificate.NotBefore.ToUniversalTime() <= now && result.Certificate.NotAfter.ToUniversalTime() > now, "tls-certificate-validity");
                var eku = result.Certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
                DocumentModel.Require(eku == null || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1" || o.Value == "2.5.29.37.0"), "tls-certificate-server-usage-required");
                result.Chain = new X509Certificate2Collection();
                foreach (var certificate in result.certificates) if (certificate != result.Certificate) result.Chain.Add(certificate);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { result.Dispose(); throw new CollaborationException("tls-config-timeout"); }
            catch { result.Dispose(); throw; }
            finally { if (json != null) CryptographicOperations.ZeroMemory(json); if (pfx != null) CryptographicOperations.ZeroMemory(pfx); }
        }
        private static async Task<byte[]> ReadPrivate(string path, int maximum, string prefix, CancellationToken cancellation) {
            cancellation.ThrowIfCancellationRequested();
            try {
                DocumentModel.Require((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) == 0, prefix + "-private-file-required");
                if (OperatingSystem.IsWindows()) {
                    using var identity = WindowsIdentity.GetCurrent();
                    var acl = new FileInfo(path).GetAccessControl();
                    DocumentModel.Require(acl.GetOwner(typeof(SecurityIdentifier)).Equals(identity.User), prefix + "-private-file-required");
                    foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        DocumentModel.Require(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(identity.User), prefix + "-private-file-required");
                }
                else DocumentModel.Require((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0, prefix + "-private-file-required");
                DocumentModel.Require(new FileInfo(path).Length is > 0 && new FileInfo(path).Length <= maximum, prefix + "-size");
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                // Read at most the configured limit plus one, even if the file grows.
                byte[] bytes = new byte[maximum + 1]; int count = 0, read;
                try {
                    while (count < bytes.Length && (read = await file.ReadAsync(bytes.AsMemory(count), cancellation)) > 0) count += read;
                    DocumentModel.Require(count > 0 && count <= maximum, prefix + "-size");
                    return bytes.AsSpan(0, count).ToArray();
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException) {
                throw new CollaborationException(prefix + "-unreadable");
            }
        }
        public void Dispose() {
            if (certificates != null) foreach (var certificate in certificates) certificate.Dispose();
            certificates = null; Certificate = null; Chain = null;
        }
    }
}
