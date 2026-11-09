using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        if (options.Has("--snapshot")) content = WireJson.Decode<RoomExport>(Encoding.UTF8.GetBytes(ReadText(options.Get("--snapshot"), WireJson.MaxMessageBytes)));
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
        RecoveryFiles.CreateDirectory(staging);
        try {
            RecoveryFiles.Write(Path.Combine(staging, "owner.json"), owner);
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
        DocumentModel.Require(new[] { "all", "auto", "websocket", "requests" }.Contains(selection), "invalid-transport");
        var transports = selection == "all" ? new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling } : new[] {
            selection == "auto" ? CollaborationTransport.Auto : selection == "websocket" ? CollaborationTransport.WebSocket : CollaborationTransport.LongPolling };
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var report = await ConnectionDoctor.RunAsync(url, catalog, options.Get("--room"), Password(options), transports, options.Has("--allow-lan-http"), !options.Has("--quick"),
            progress: step => Console.Error.WriteLine(step), cancellation: stop.Token);
        if (options.Has("--output")) RecoveryFiles.Write(options.Get("--output"), report);
        Console.WriteLine(WireJson.Text(report));
        return report.passed ? 0 : 2;
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
