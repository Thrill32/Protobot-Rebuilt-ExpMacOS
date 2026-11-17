using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private sealed class Options {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        public Options(string[] args, string[] parameters, string[] flags) {
            for (int i = 1; i < args.Length; i++) {
                string key = args[i];
                DocumentModel.Require(!values.ContainsKey(key), "duplicate-option");
                if (parameters.Contains(key)) {
                    DocumentModel.Require(i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal), "missing-option-value");
                    values.Add(key, args[++i]);
                }
                else { DocumentModel.Require(flags.Contains(key), "unknown-option"); values.Add(key, null); }
            }
        }
        public string Get(string key, string fallback = null) => values.TryGetValue(key, out var value) ? value : fallback;
        public bool Has(string key) => values.ContainsKey(key);
        public string Required(string key) { DocumentModel.Require(Has(key), "missing-required-option"); return Get(key); }
    }
    [Serializable] private sealed class OwnerFile { public int schema = 1; public string room, credential; }
    [Serializable] private sealed class Initialized { public string room, dataDirectory, ownerFile; public bool durable = true; public int parts; }

    private static int Initialize(Options options) {
        string target = Path.GetFullPath(options.Required("--data-dir")); string room = options.Required("--room");
        DocumentModel.Require(DocumentModel.ValidId(room), "invalid-room");
        DocumentModel.Require(!Directory.Exists(target) && !File.Exists(target), "init-requires-new-directory");
        DocumentModel.Require(options.Has("--catalog") != options.Has("--snapshot"), "use-catalog-or-snapshot");
        RoomExport content;
        if (options.Has("--snapshot")) content = LocalJson.Decode<RoomExport>(Encoding.UTF8.GetBytes(ReadText(options.Get("--snapshot"), LocalJson.MaxBytes)));
        else {
            var catalog = WireJson.Decode<CatalogManifest>(Encoding.UTF8.GetBytes(ReadText(options.Get("--catalog"), WireJson.MaxMessageBytes)));
            content = new RoomExport { catalog = catalog, document = new Document { catalogHash = catalog.hash } };
        }
        content.Validate();
        string password = Password(options);
        var owner = new OwnerFile { room = room, credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
        var seed = content.CreateRoom(room, password, owner.credential);
        // Initialize in a private sibling, then publish the complete room in one
        // rename. A failed init never overwrites a room or leaves half its secrets.
        string parent = Path.GetDirectoryName(target); Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".protobot-init-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) {
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(identity.User);
            acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(staging).Create(acl);
        }
        else Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try {
            RecoveryFiles.Write(Path.Combine(staging, "owner.json"), owner);
            ProtectOwnerFile(Path.Combine(staging, "owner.json"));
            using (var store = new RoomStore(staging)) { _ = new Room(seed, storage: store); }
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        Console.WriteLine(WireJson.Text(new Initialized { room = room, dataDirectory = target, ownerFile = Path.Combine(target, "owner.json"), parts = seed.document.parts.Length }));
        return 0;
    }
    private static async Task<int> Doctor(Options options) {
        string url = ConnectionDoctor.Origin(options.Required("--url"), options.Has("--allow-lan-http"));
        CatalogManifest catalog = options.Has("--catalog") ? WireJson.Decode<CatalogManifest>(Encoding.UTF8.GetBytes(ReadText(options.Get("--catalog"), WireJson.MaxMessageBytes))) : null;
        string selection = options.Get("--transport", "all");
        DocumentModel.Require(new[] { "all", "auto", "websocket", "requests", "short-polling" }.Contains(selection), "invalid-transport");
        var transports = selection == "all" ? null : new[] {
            selection == "auto" ? CollaborationTransport.Auto : selection == "websocket" ? CollaborationTransport.WebSocket : selection == "short-polling" ? CollaborationTransport.ShortPolling : CollaborationTransport.LongPolling };
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var network = await Network(options, stop.Token);
        var report = await ConnectionDoctor.RunAsync(url, catalog, options.Get("--room"), Password(options), transports, options.Has("--allow-lan-http"), !options.Has("--quick"),
            progress: step => Console.Error.WriteLine(step), cancellation: stop.Token, networkPolicy: network);
        if (options.Has("--output")) RecoveryFiles.Write(options.Get("--output"), report);
        Console.WriteLine(WireJson.Text(report));
        return report.passed ? 0 : 2;
    }
    private static void ProtectOwnerFile(string path) {
        if (!OperatingSystem.IsWindows()) { RecoveryFiles.PrivateFile(path); return; }
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(identity.User);
        acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
    private static OwnerFile ReadOwnerFile(string path) {
        RequirePrivateCredential(path);
        var owner = WireJson.Decode<OwnerFile>(Encoding.UTF8.GetBytes(ReadText(path, 16384)));
        DocumentModel.Require(owner != null && owner.schema == 1 && DocumentModel.ValidId(owner.room) &&
            !string.IsNullOrEmpty(owner.credential) && owner.credential.Length < 256 && !owner.credential.Any(char.IsControl), "invalid-owner-file");
        return owner;
    }
    private static void RequirePrivateCredential(string path) {
        DocumentModel.Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "owner-file-link-refused");
        // Refuse broad permissions rather than silently repairing a credential already exposed.
        if (OperatingSystem.IsWindows()) {
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new FileInfo(path).GetAccessControl();
            DocumentModel.Require(acl.GetOwner(typeof(SecurityIdentifier)).Equals(identity.User), "owner-file-must-be-private");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                DocumentModel.Require(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(identity.User), "owner-file-must-be-private");
        }
        else DocumentModel.Require((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0, "owner-file-must-be-private");
    }

    private static async Task<int> Administer(string operation, Options options) {
        string origin = ConnectionDoctor.Origin(options.Required("--url"), options.Has("--allow-lan-http"));
        var owner = ReadOwnerFile(options.Required("--owner-file"));
        using var stop = CommandCancellation(); var network = await Network(options, stop.Token);
        using var http = network.CreateHttpClient();
        async Task<T> Request<T>(HttpMethod method, string path, byte[] body = null, bool authenticate = true) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(method, origin + path);
            if (authenticate) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.credential);
            if (body != null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            NetworkPolicy.CheckProxyResponse(response);
            using var input = await response.Content.ReadAsStreamAsync(); using var output = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0) { DocumentModel.Require(output.Length + count <= WireJson.MaxMessageBytes, "message-too-large"); output.Write(buffer, 0, count); }
            if (!response.IsSuccessStatusCode) {
                // Only these fixed codes may leave the response body. Proxy HTML or a
                // reflected credential must never become terminal/log output.
                string reason = null;
                try { reason = WireJson.Decode<WireError>(output.ToArray())?.error; } catch { }
                bool known = new[] { "owner-required", "owner-removal-protected", "participant-not-found", "invalid-participant-id",
                    "invalid-session", "room-storage-failed-restart-required" }.Contains(reason);
                throw new CollaborationException("participant-administration-http-" + (int)response.StatusCode + (known ? ": " + reason : ""));
            }
            return WireJson.Decode<T>(output.ToArray());
        }
        var capabilities = await Request<Capabilities>(HttpMethod.Get, "/rooms/" + owner.room + "/capabilities", authenticate: false);
        DocumentModel.Require(capabilities.protocol == 1 && capabilities.version == CollaborationProtocol.Version && capabilities.room == owner.room, "owner-room-or-version-mismatch");
        DocumentModel.Require(capabilities.participantAdministration, "participant-administration-unavailable-update-helper");
        string path = "/rooms/" + owner.room + "/participants";
        if (operation == "participants") Console.WriteLine(WireJson.Text(await Request<MembershipList>(HttpMethod.Get, path)));
        else {
            string id = options.Required("--participant"); DocumentModel.Require(DocumentModel.ValidId(id), "invalid-participant-id");
            Console.WriteLine(WireJson.Text(await Request<RemovalResult>(HttpMethod.Post, path + "/remove", WireJson.Encode(new RemoveParticipant { participantId = id }))));
        }
        return 0;
    }
    private static string ReadText(string path, int limit) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        DocumentModel.Require(file.Length <= limit, "input-file-too-large");
        using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) > 0) { DocumentModel.Require(output.Length + count <= limit, "input-file-too-large"); output.Write(buffer, 0, count); }
        string text = new UTF8Encoding(false, true).GetString(output.ToArray()); return text.TrimStart('\uFEFF');
    }
    private static string Password(Options options) {
        DocumentModel.Require(!options.Has("--password-file") || !options.Has("--password-prompt"), "use-password-file-or-prompt");
        string password = "";
        if (options.Has("--password-file")) password = ReadText(options.Get("--password-file"), 1024).TrimEnd('\r', '\n');
        else if (options.Has("--password-prompt")) {
            DocumentModel.Require(!Console.IsInputRedirected, "password-prompt-requires-terminal-use-file");
            Console.Error.Write("Room password (blank permits joining without a password): ");
            var entered = new StringBuilder();
            while (true) {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (entered.Length > 0) entered.Length--; }
                else if (!char.IsControl(key.KeyChar) && entered.Length < 128) entered.Append(key.KeyChar);
            }
            Console.Error.WriteLine(); password = entered.ToString();
        }
        DocumentModel.Require(password.Length <= 128 && !password.Any(char.IsControl), "invalid-password-file"); return password;
    }
}
