using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using Protobot.Collaboration;
using UnityEngine;

namespace Protobot {
    public sealed class CollaborationHost : IDisposable {
        private Process process;
        private Task stdout, stderr;
        private string lastError;
        public string Url { get; private set; }
        public string DataDirectory { get; private set; }
        public int BoundPort { get; private set; }
        public bool AllowLan { get; private set; }
        public bool Recovered { get; private set; }
        public bool IsRunning { get { try { return process != null && !process.HasExited; } catch (InvalidOperationException) { return false; } } }
        public static string HelperPath {
            get {
                bool windows = Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer;
                string name = windows ? "protobot-collab.exe" : "protobot-collab";
                if (Application.isEditor) return Path.GetFullPath(Path.Combine(Application.dataPath, "../Collaboration/artifacts", windows ? "win-x64" : "osx-arm64", name));
                return Application.platform == RuntimePlatform.OSXPlayer ? Path.GetFullPath(Path.Combine(Application.dataPath, "Helpers/Collaboration", name)) :
                    Path.GetFullPath(Path.Combine(Application.dataPath, "../Collaboration", name));
            }
        }
        public async Task StartAsync(Bootstrap bootstrap, int port, bool lan, string dataDirectory = null) {
            if (!File.Exists(HelperPath)) throw new FileNotFoundException("Publish the matching collaboration helper first; see Collaboration/README.md.");
            DataDirectory = dataDirectory == null ? null : Path.GetFullPath(dataDirectory); AllowLan = lan; lastError = null;
            var start = new ProcessStartInfo {
                FileName = HelperPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "serve --bootstrap-stdin --listen http://" + (lan ? "0.0.0.0" : "127.0.0.1") + ":" + port + (lan ? " --allow-lan-http" : "") +
                    (DataDirectory == null ? " --ephemeral" : " --data-dir " + Quote(DataDirectory))
            };
            process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("Could not launch the collaboration helper.");
            // Drain both channels immediately. Secrets travel exclusively over inherited stdin.
            stderr = DrainErrors(process.StandardError);
            try {
                var first = process.StandardOutput.ReadLineAsync();
                await process.StandardInput.WriteLineAsync(WireJson.Text(bootstrap));
                await process.StandardInput.FlushAsync();
                if (await Task.WhenAny(first, Task.Delay(30000)) != first) throw new TimeoutException("Collaboration server did not become ready.");
                var line = await first;
                if (line == null) { await stderr; throw new InvalidOperationException("Collaboration server exited: " + (lastError ?? "startup failed")); }
                var ready = WireJson.Decode<Ready>(System.Text.Encoding.UTF8.GetBytes(line));
                if (ready.type != "ready" || ready.version != CollaborationProtocol.Version || bootstrap != null && ready.room != bootstrap.room)
                    throw new InvalidOperationException("Collaboration helper version mismatch. Republish the matching helper; this build requires " + CollaborationProtocol.Version + ".");
                var bound = new Uri(ready.url);
                BoundPort = bound.Port; Recovered = ready.recovered;
                Url = new UriBuilder(bound) { Host = "127.0.0.1" }.Uri.GetLeftPart(UriPartial.Authority);
                stdout = Drain(process.StandardOutput);
            }
            catch { await StopAsync(); throw; }
        }
        public async Task RestartAsync() {
            if (DataDirectory == null) throw new InvalidOperationException("This temporary room has no recovery directory.");
            int port = BoundPort; bool lan = AllowLan; string directory = DataDirectory;
            await StopAsync(); await StartAsync(null, port, lan, directory);
        }
        private async Task DrainErrors(StreamReader reader) { string line; while ((line = await reader.ReadLineAsync()) != null) lastError = line.Substring(0, Math.Min(line.Length, 256)); }
        internal static string Quote(string value) {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(c);
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        private static async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() != null) { } }
        public async Task StopAsync() {
            var owned = process; process = null;
            if (owned == null) return;
            try {
                try { owned.StandardInput.Close(); } catch (IOException) { }
                var exited = Task.Run(() => owned.WaitForExit());
                if (await Task.WhenAny(exited, Task.Delay(7000)) != exited) { owned.Kill(); await exited; }
                if (stdout != null) await stdout;
                if (stderr != null) await stderr;
            }
            finally { owned.Dispose(); }
        }
        public void Dispose() {
            // Closing the private parent channel also covers application exit/domain reload.
            if (process == null) return;
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
        }
    }
}
