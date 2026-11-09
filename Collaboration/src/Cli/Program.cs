using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static class Program {
    public static async Task<int> Main(string[] args) {
        try {
            if (args.Length == 0 || args[0] == "--help") {
                Console.WriteLine("protobot-collab serve --data-dir <local-directory> [--config <initial-bootstrap.json>] [--listen http://127.0.0.1:8787] [--allow-lan-http]\n" +
                    "protobot-collab serve --ephemeral --config <protected-bootstrap.json>\n" +
                    "protobot-collab doctor --url <https-origin>\nDevelopment alpha: one room; server recovery and participant Save are independent."); return 0;
            }
            string Option(string key, string fallback = null) {
                int i = Array.IndexOf(args, key);
                return i < 0 ? fallback : i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing " + key);
            }
            if (args[0] == "doctor") {
                var uri = new Uri(Option("--url") ?? throw new ArgumentException("--url is required"));
                DocumentModel.Require(uri.Scheme == "https" || uri.IsLoopback, "https-required");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var result = WireJson.Decode<Capabilities>(await http.GetByteArrayAsync(new Uri(uri, "/capabilities")));
                Console.WriteLine(WireJson.Text(result));
                Console.WriteLine("Capability request passed using system TLS trust. Room transport/real-network acceptance requires the lab/player check."); return 0;
            }
            DocumentModel.Require(args[0] == "serve", "unknown-command");
            bool parentOwned = Array.IndexOf(args, "--bootstrap-stdin") >= 0;
            string dataDirectory = Option("--data-dir");
            bool ephemeral = Array.IndexOf(args, "--ephemeral") >= 0;
            DocumentModel.Require((dataDirectory != null) != ephemeral, "use-data-dir-or-explicit-ephemeral");
            string config = Option("--config");
            string json = parentOwned ? await Console.In.ReadLineAsync() : config == null ? "null" : await File.ReadAllTextAsync(config);
            DocumentModel.Require(json != null && Encoding.UTF8.GetByteCount(json) <= WireJson.MaxMessageBytes, "invalid-bootstrap");
            var seed = WireJson.Decode<Bootstrap>(Encoding.UTF8.GetBytes(json));
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await using var server = new CollaborationServer(seed, Option("--listen", "http://127.0.0.1:8787"), Array.IndexOf(args, "--allow-lan-http") >= 0, dataDirectory);
            await server.StartAsync(stop.Token);
            Console.WriteLine(WireJson.Text(new Ready { url = server.Url, room = server.Room.Name, durable = server.Room.IsDurable,
                recovered = server.Room.Recovered, revision = server.Room.Snapshot.revision }));
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
