using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    public static async Task<int> Main(string[] args) {
        try {
            if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(CollaborationProtocol.Version); return 0; }
            if (args.Length == 0 || args[0] == "--help") {
                Console.WriteLine("protobot-collab init --data-dir <new-directory> --room <id> (--catalog <catalog.json> | --snapshot <room-export.json>) [--password-prompt | --password-file <file>]\n" +
                    "protobot-collab serve --data-dir <local-directory> [--config <initial-bootstrap.json>] [--listen http://127.0.0.1:8787] [--public-url <https-origin>] [--allow-lan-http]\n" +
                    "protobot-collab serve --ephemeral --config <protected-bootstrap.json>\n" +
                    "protobot-collab backup --data-dir <stopped-room> --output <new-archive.zip>\n" +
                    "protobot-collab restore --input <archive.zip> --data-dir <new-directory>\n" +
                    "protobot-collab doctor --url <https-origin> [--catalog <catalog.json>] [--room <id>] [--password-prompt | --password-file <file>] [--transport all|auto|websocket|requests] [--quick] [--output <report.json>]\n" +
                    "Development alpha: one room; server recovery and participant Save are independent. Debian deployment: Collaboration/deploy/README.md"); return 0;
            }
            if (args[0] == "init") return Initialize(new Options(args, new[] { "--data-dir", "--room", "--catalog", "--snapshot", "--password-file" }, new[] { "--password-prompt" }));
            if (args[0] == "doctor") return await Doctor(new Options(args, new[] { "--url", "--catalog", "--room", "--password-file", "--transport", "--output" }, new[] { "--password-prompt", "--quick", "--allow-lan-http" }));
            if (args[0] == "backup") {
                var backup = new Options(args, new[] { "--data-dir", "--output" }, Array.Empty<string>());
                RoomBackup.Backup(backup.Required("--data-dir"), backup.Required("--output"));
                Console.WriteLine("Backup completed."); return 0;
            }
            if (args[0] == "restore") {
                var restore = new Options(args, new[] { "--input", "--data-dir" }, Array.Empty<string>());
                RoomBackup.Restore(restore.Required("--input"), restore.Required("--data-dir"));
                Console.WriteLine("Restore completed."); return 0;
            }
            DocumentModel.Require(args[0] == "serve", "unknown-command");
            var options = new Options(args, new[] { "--data-dir", "--config", "--listen", "--public-url" }, new[] { "--bootstrap-stdin", "--ephemeral", "--allow-lan-http" });
            bool parentOwned = options.Has("--bootstrap-stdin");
            string dataDirectory = options.Get("--data-dir");
            bool ephemeral = options.Has("--ephemeral");
            DocumentModel.Require((dataDirectory != null) != ephemeral, "use-data-dir-or-explicit-ephemeral");
            string config = options.Get("--config");
            DocumentModel.Require(!parentOwned || config == null, "use-config-or-private-stdin");
            string publicUrl = options.Get("--public-url");
            if (publicUrl != null) { publicUrl = ConnectionDoctor.Origin(publicUrl); DocumentModel.Require(new Uri(publicUrl).Scheme == "https", "public-url-requires-https"); }
            string json = parentOwned ? await Console.In.ReadLineAsync() : config == null ? "null" : ReadText(config, WireJson.MaxMessageBytes);
            DocumentModel.Require(json != null && Encoding.UTF8.GetByteCount(json) <= WireJson.MaxMessageBytes, "invalid-bootstrap");
            var seed = WireJson.Decode<Bootstrap>(Encoding.UTF8.GetBytes(json));
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await using var server = new CollaborationServer(seed, options.Get("--listen", "http://127.0.0.1:8787"), options.Has("--allow-lan-http"), dataDirectory);
            await server.StartAsync(stop.Token);
            var ready = new Ready { url = server.Url, room = server.Room.Name, durable = server.Room.IsDurable,
                recovered = server.Room.Recovered, revision = server.Room.Snapshot.revision };
            Console.WriteLine(publicUrl == null ? WireJson.Text(ready) : System.Text.Json.JsonSerializer.Serialize(new {
                ready.type, ready.url, ready.room, ready.version, ready.durable, ready.recovered, ready.revision, publicUrl }));
            await Console.Out.FlushAsync();
            if (parentOwned) {
                // EOF (including a crashed parent) or an explicit line closes only this owned server.
                _ = Task.Run(async () => { await Console.In.ReadLineAsync(); stop.Cancel(); });
            }
            await server.WaitAsync(stop.Token); return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception error) {
            // Never print a bootstrap payload, URL credentials or request body.
            Console.Error.WriteLine(error is CollaborationException known ? known.Code : error.GetType().Name + ": server startup or configuration failed."); return 1;
        }
    }
}
