using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Protobot.Collaboration;
using UnityEngine;

namespace Protobot {
    public sealed class CollaborationHost : IDisposable {
        private Process process;
        private Task stdout, stderr;
        public string Url { get; private set; }
        public static string HelperPath {
            get {
                bool windows = Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer;
                string name = windows ? "protobot-collab.exe" : "protobot-collab";
                if (Application.isEditor) return Path.GetFullPath(Path.Combine(Application.dataPath, "../Collaboration/artifacts", windows ? "win-x64" : "osx-arm64", name));
                return Application.platform == RuntimePlatform.OSXPlayer ? Path.GetFullPath(Path.Combine(Application.dataPath, "Helpers/Collaboration", name)) :
                    Path.GetFullPath(Path.Combine(Application.dataPath, "../Collaboration", name));
            }
        }
        public async Task StartAsync(Bootstrap bootstrap, int port, bool lan) {
            if (!File.Exists(HelperPath)) throw new FileNotFoundException("Publish the matching collaboration helper first; see Collaboration/README.md.");
            var start = new ProcessStartInfo {
                FileName = HelperPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "serve --bootstrap-stdin --listen http://" + (lan ? "0.0.0.0" : "127.0.0.1") + ":" + port + (lan ? " --allow-lan-http" : "")
            };
            process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("Could not launch the collaboration helper.");
            // Drain both channels immediately. Secrets travel exclusively over inherited stdin.
            stderr = Drain(process.StandardError);
            try {
                var first = process.StandardOutput.ReadLineAsync();
                await process.StandardInput.WriteLineAsync(WireJson.Text(bootstrap));
                await process.StandardInput.FlushAsync();
                if (await Task.WhenAny(first, Task.Delay(15000)) != first) throw new TimeoutException("Collaboration server did not become ready.");
                var line = await first;
                if (line == null) throw new InvalidOperationException("Collaboration server exited before becoming ready.");
                var ready = WireJson.Decode<Ready>(System.Text.Encoding.UTF8.GetBytes(line));
                if (ready.type != "ready" || ready.version != "0.1.0-alpha" || ready.room != bootstrap.room) throw new InvalidOperationException("Collaboration helper version mismatch.");
                var bound = new Uri(ready.url);
                Url = new UriBuilder(bound) { Host = "127.0.0.1" }.Uri.GetLeftPart(UriPartial.Authority);
                stdout = Drain(process.StandardOutput);
            }
            catch { await StopAsync(); throw; }
        }
        private static async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() != null) { } }
        public async Task StopAsync() {
            var owned = process; process = null;
            if (owned == null) return;
            try {
                owned.StandardInput.Close();
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
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        }
    }
}
