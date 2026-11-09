#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Protobot.Collaboration;
using UnityEngine;

namespace Protobot {
    // Explicit development-player automation. It has no listener or release-build execution path.
    internal static class CollaborationPlayerProbe {
        [Serializable] private sealed class Report {
            public bool passed;
            public string hash, transport, error;
            public long revision;
            public int parts;
        }
        public static async Task RunAsync(CollaborationSession session) {
            string output = CollaborationArguments.Get("--collab-test-results");
            if (string.IsNullOrEmpty(output)) return;
            try {
                if (CollaborationArguments.Get("--collab-probe") == "ui" || CollaborationArguments.Get("--collab-probe") == "ui-child") {
                    await session.RunUIPlayerProbeAsync(output, CollaborationArguments.Get("--collab-probe") == "ui-child"); return;
                }
                if (CollaborationArguments.Get("--collab-probe") == "host") {
                    await session.StartProbeHostAsync();
                    Require(session.ProbeConnected, "In-app host failed.");
                    Write(output + ".ready", new Ready { url = session.ProbeEndpoint, room = "team" });
                }
                await Until(() => session.ProbeConnected);
                LocalPreferences.SetString("collaborationProbeName", CollaborationArguments.Get("--collab-name"));
                var part = PartsManager.GeneratePart("CCHL-1x2-5", Vector3.right * (CollaborationArguments.Get("--collab-probe") == "host" ? 0 : 10), Quaternion.identity);
                CollaborationSession.PlaceCatalog(part);
                await Until(() => session.ProbeDocument?.revision == 2 && !CollaborationSession.HasPending && PartsManager.FindLoadedObjects().Count == 2);
                var document = session.ProbeDocument;
                var scene = PartsManager.FindLoadedObjects().ToDictionary(p => p.GetComponent<SavedObject>().ConnectionId);
                foreach (var accepted in document.parts) {
                    Require(scene.TryGetValue(accepted.id, out var body), "Missing materialized stable ID.");
                    Require(body.GetComponent<SavedObject>().id == accepted.catalogId, "Wrong generated catalog part.");
                    var captured = CollaborationScene.Capture(body);
                    Require(captured.position.Zip(accepted.position, (a, b) => Math.Abs(a - b)).All(d => d < .0001), "Wrong scene pose.");
                    Require(body.GetComponentsInChildren<Collider>().Any(c => c.enabled), "Missing generated colliders.");
                }
                Require(CollaborationSession.SaveSnapshot().parts.Length == 2, "Wrong accepted save snapshot.");
                Write(output, new Report { passed = true, hash = DocumentModel.Hash(document), transport = session.ProbeTransport, revision = document.revision, parts = document.parts.Length });
            }
            catch (Exception error) { Write(output, new Report { error = error.Message }); Debug.LogException(error); }
        }
        private static async Task Until(Func<bool> condition) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition()) { if (watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("Built-player collaboration probe timed out."); await Task.Delay(25); }
        }
        private static void Write<T>(string path, T value) {
            path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path + ".tmp", WireJson.Encode(value));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
        }
        private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    }
}
#endif
