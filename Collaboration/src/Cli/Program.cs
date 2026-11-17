using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task<string> ReadBootstrapLine() {
        var text = new StringBuilder(); var buffer = new char[8192]; int bytes = 0, count;
        while ((count = await Console.In.ReadAsync(buffer, 0, buffer.Length)) > 0) {
            int newline = Array.IndexOf(buffer, '\n', 0, count), used = newline >= 0 ? newline : count;
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(buffer, 0, used));
            DocumentModel.Require(bytes <= LocalJson.MaxBytes, "invalid-bootstrap"); text.Append(buffer, 0, used);
            if (newline >= 0) return text.ToString();
        }
        return text.Length == 0 ? null : text.ToString();
    }
    public static async Task<int> Main(string[] args) {
        try {
            if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(CollaborationProtocol.Version); return 0; }
            if (args.Length == 0 || args[0] == "--help") {
                Console.WriteLine("protobot-collab init --data-dir <new-directory> --room <id> (--catalog <catalog.json> | --snapshot <room-export.json>) [--password-prompt | --password-file <file>]\n" +
                    "protobot-collab serve --data-dir <local-directory> [--config <initial-bootstrap.json>] [--listen http://127.0.0.1:8787] [--public-url <https-origin>] [--allow-lan-http]\n" +
                    "protobot-collab server-init --data-dir <new-server-directory>\n" +
                    "protobot-collab serve --multi-room --config <server-directory/server.json> [--listen <origin>] [--public-url <https-origin>]\n" +
                    "protobot-collab room create --url <origin> --admin-file <private-admin.json> --room <id> (--catalog <file> | --snapshot <file>) --owner-output <new-private-file> [--password-file <file> | --password-prompt]\n" +
                    "protobot-collab room list --url <origin> --admin-file <private-admin.json>\n" +
                    "protobot-collab room restore --config <server.json> --admin-file <private-admin.json> --input <archive.zip> # stopped server\n" +
                    "protobot-collab serve --ephemeral --config <protected-bootstrap.json>\n" +
                    "protobot-collab backup --data-dir <stopped-room> --output <new-archive.zip>\n" +
                    "protobot-collab restore --input <archive.zip> --data-dir <new-directory>\n" +
                    "protobot-collab participants --url <origin> --owner-file <protected-owner.json> [--allow-lan-http]\n" +
                    "protobot-collab remove-participant --url <origin> --owner-file <protected-owner.json> --participant <stable-id> [--allow-lan-http]\n" +
                    "protobot-collab diagnostics --url <origin> (--owner-file <private-owner.json> | --admin-file <private-admin.json>) [--output <new-report.json>]\n" +
                    "protobot-collab doctor --url <https-origin> [--catalog <catalog.json>] [--room <id>] [--password-prompt | --password-file <file>] [--transport all|auto|websocket|requests|short-polling] [--quick] [--output <report.json>]\n" +
                    "Standalone serve: [--tls-file <private-protobot-tls-v1.json>] with --listen https://<IP>:<port>. Existing PFX/password are private file input only.\n" +
                    "Remote commands: [--network-route system|direct | --network-file <private-protobot-network-v1.json>]. Proxy passwords are file input only.\n" +
                    "Development alpha: bounded rooms; server recovery and participant Save are independent. Debian deployment: Collaboration/deploy/README.md"); return 0;
            }
            if (args[0] == "server-init") return InitializeServer(new Options(args, new[] { "--data-dir" }, Array.Empty<string>()));
            if (args[0] == "room") {
                DocumentModel.Require(args.Length >= 2, "missing-room-operation"); string operation = args[1];
                DocumentModel.Require(operation == "create" || operation == "list" || operation == "restore", "unknown-room-operation");
                var roomArgs = new string[args.Length - 1]; roomArgs[0] = "room"; Array.Copy(args, 2, roomArgs, 1, args.Length - 2);
                return await Hosting(operation, new Options(roomArgs,
                    operation == "list" ? new[] { "--url", "--admin-file", "--network-route", "--network-file" } : operation == "restore" ? new[] { "--config", "--admin-file", "--input" } :
                        new[] { "--url", "--admin-file", "--room", "--catalog", "--snapshot", "--password-file", "--owner-output", "--network-route", "--network-file" },
                    operation == "restore" ? Array.Empty<string>() : operation == "list" ? new[] { "--allow-lan-http" } : new[] { "--password-prompt", "--allow-lan-http" }));
            }
            if (args[0] == "init") return Initialize(new Options(args, new[] { "--data-dir", "--room", "--catalog", "--snapshot", "--password-file" }, new[] { "--password-prompt" }));
            if (args[0] == "diagnostics") return await Diagnostics(new Options(args, new[] { "--url", "--owner-file", "--admin-file", "--output", "--network-route", "--network-file" }, new[] { "--allow-lan-http" }));
            if (args[0] == "doctor") return await Doctor(new Options(args, new[] { "--url", "--catalog", "--room", "--password-file", "--transport", "--output", "--network-route", "--network-file" }, new[] { "--password-prompt", "--quick", "--allow-lan-http" }));
            if (args[0] == "participants" || args[0] == "remove-participant") return await Administer(args[0],
                new Options(args, args[0] == "participants" ? new[] { "--url", "--owner-file", "--network-route", "--network-file" } : new[] { "--url", "--owner-file", "--participant", "--network-route", "--network-file" }, new[] { "--allow-lan-http" }));
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
            var options = new Options(args, new[] { "--data-dir", "--config", "--listen", "--public-url", "--tls-file" }, new[] { "--bootstrap-stdin", "--ephemeral", "--allow-lan-http", "--multi-room" });
            bool multiRoom = options.Has("--multi-room");
            bool parentOwned = options.Has("--bootstrap-stdin");
            string dataDirectory = options.Get("--data-dir");
            bool ephemeral = options.Has("--ephemeral");
            DocumentModel.Require(multiRoom ? dataDirectory == null && !ephemeral && !parentOwned && options.Has("--config") : (dataDirectory != null) != ephemeral, "use-data-dir-or-explicit-ephemeral");
            string config = options.Get("--config");
            DocumentModel.Require(!parentOwned || config == null, "use-config-or-private-stdin");
            DocumentModel.Require(!parentOwned || !options.Has("--tls-file"), "tls-standalone-only");
            string publicUrl = options.Get("--public-url");
            if (publicUrl != null) { publicUrl = ConnectionDoctor.Origin(publicUrl); DocumentModel.Require(new Uri(publicUrl).Scheme == "https", "public-url-requires-https"); }
            using var stop = CommandCancellation();
            using var tls = options.Has("--tls-file") ? await TlsConfiguration.LoadAsync(options.Get("--tls-file"), stop.Token) : null;
            string json = multiRoom ? "null" : parentOwned ? await ReadBootstrapLine() : config == null ? "null" : ReadText(config, LocalJson.MaxBytes);
            DocumentModel.Require(json != null && Encoding.UTF8.GetByteCount(json) <= LocalJson.MaxBytes, "invalid-bootstrap");
            var seed = LocalJson.Decode<Bootstrap>(Encoding.UTF8.GetBytes(json));
            await using var server = new CollaborationServer(seed, options.Get("--listen", "http://127.0.0.1:8787"), options.Has("--allow-lan-http"), dataDirectory, multiRoom ? config : null, tls);
            try { await server.StartAsync(stop.Token); }
            catch (OperationCanceledException) { throw; }
            catch when (tls != null) { throw new CollaborationException("tls-listener-start-failed"); }
            var ready = new Ready { url = server.Url, room = server.Room?.Name, durable = multiRoom || server.Room.IsDurable, multiRoom = multiRoom, registryVersion = multiRoom ? RoomRegistry.Version : 0,
                recovered = multiRoom || server.Room.Recovered, revision = server.Room?.Snapshot.revision ?? 0 };
            Console.WriteLine(publicUrl == null ? WireJson.Text(ready) : System.Text.Json.JsonSerializer.Serialize(new {
                ready.type, ready.url, ready.room, ready.version, ready.durable, ready.recovered, ready.revision, ready.multiRoom, ready.registryVersion, publicUrl }));
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
            Console.Error.WriteLine(NetworkPolicy.FailureCode(error) ?? (error is CollaborationException known ? known.Code : error.GetType().Name + ": server startup or configuration failed.")); return 1;
        }
    }
}
