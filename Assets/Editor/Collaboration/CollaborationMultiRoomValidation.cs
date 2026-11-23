using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationMultiRoomValidation {
        private const string Key = "Protobot.MultiRoomSmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog(); EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }
        [InitializeOnLoadMethod] private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try { await Check(); Debug.Log("Collaboration multi-room validation passed: real dedicated helper, two rooms, Unity joins on both transports, independent remote edits/custom materialization, scoped caches, local saves and server restart."); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static string Quote(string text) {
            var value = new StringBuilder("\""); int slashes = 0;
            foreach (char c in text) {
                if (c == '\\') { slashes++; continue; }
                value.Append('\\', c == '\"' ? slashes * 2 + 1 : slashes); slashes = 0; value.Append(c);
            }
            return value.Append('\\', slashes * 2).Append('\"').ToString();
        }
        private static async Task Run(params string[] args) {
            using var process = Process.Start(new ProcessStartInfo(CollaborationHost.HelperPath, string.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            });
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
            await Task.Run(() => process.WaitForExit()); await output;
            Require(process.ExitCode == 0, "Multi-room CLI failed: " + await error);
        }
        private static async Task Check() {
            string root = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "multi-room-" + Guid.NewGuid().ToString("N"));
            string registry = Path.Combine(root, "server"); RecoveryFiles.CreateDirectory(root);
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            string model = Path.Combine(root, "mesh.obj"); File.WriteAllText(model, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            CustomModel.Create(CustomModelImporter.Read(model, 1), Vector3.right * 10, Quaternion.identity);
            var catalog = CollaborationScene.Catalog; var red = CollaborationScene.CaptureDocument(catalog);
            string customId = red.parts.Single(p => p.catalogId == "CUSTOM").id;
            var blue = GeometryTransfer.CloneDocument(red); blue.parts.Single(p => p.id == customId).position[0] = 25;
            foreach (var item in new[] { ("red", red), ("blue", blue) }) RecoveryFiles.Write(Path.Combine(root, item.Item1 + ".json"), new RoomExport { catalog = catalog, document = item.Item2 });
            await Run("server-init", "--data-dir", registry);
            Process server = null; Task<string> errors = null, output = null;
            async Task<Ready> Start(string listener) {
                server = Process.Start(new ProcessStartInfo(CollaborationHost.HelperPath, "serve --multi-room --config " + Quote(Path.Combine(registry, "server.json")) + " --listen " + listener) {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                });
                server.StandardInput.Close(); errors = server.StandardError.ReadToEndAsync(); var line = server.StandardOutput.ReadLineAsync();
                Require(await Task.WhenAny(line, Task.Delay(30000)) == line, "Multi-room helper readiness timed out.");
                string text = await line; Require(text != null, "Multi-room helper failed: " + (text == null ? await errors : ""));
                output = server.StandardOutput.ReadToEndAsync(); var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(text));
                Require(ready.multiRoom && ready.room == null && ready.version == CollaborationProtocol.Version, "Wrong dedicated readiness."); return ready;
            }
            async Task Stop() { if (server == null) return; if (!server.HasExited) server.Kill(); await Task.Run(() => server.WaitForExit()); await errors; if (output != null) await output; server.Dispose(); server = null; }
            try {
                var ready = await Start("http://127.0.0.1:0");
                foreach (string room in new[] { "red", "blue" }) await Run("room", "create", "--url", ready.url, "--admin-file", Path.Combine(registry, "admin.json"),
                    "--room", room, "--snapshot", Path.Combine(root, room + ".json"), "--owner-output", Path.Combine(root, room + "-owner.json"));
                var session = Object.FindFirstObjectByType<CollaborationSession>(); var manager = Object.FindFirstObjectByType<BuildsManager>();
                Set(session, "endpoint", ready.url); Set(session, "room", "red"); Set(session, "transport", CollaborationTransport.WebSocket);
                await (Task)Invoke(session, "JoinAsync"); Require(CollaborationSession.IsShared, "Unity red join failed.");
                var redClient = Get<CollaborationClient>(session, "client"); string redIdentity = redClient.Participant.id;
                Require(Body(customId).GetComponentInChildren<MeshFilter>().sharedMesh.vertexCount == 3 && Math.Abs(Body(customId).transform.position.x - 10) < .001, "Red custom geometry materialization failed.");
                using (var peer = new CollaborationClient(ready.url, "blue", catalog, CollaborationTransport.LongPolling)) {
                    await peer.JoinAsync("Independent blue editor"); var added = WireJson.Clone(peer.Snapshot.parts.First(p => p.catalogId != "CUSTOM")); added.id = Guid.NewGuid().ToString("N"); added.position[0] = 50;
                    Require((await peer.SubmitAsync(peer.CreateTransaction("AddParts", new[] { added }))).accepted, "Blue edit failed.");
                    await Task.Delay(100); Require(PartsManager.FindLoadedObjects().Count == 2 && redClient.Snapshot.revision == 0, "Blue event entered the red scene.");
                    string redSave = Path.Combine(root, "red.pbb"); manager.buildPath = redSave;
                    Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Red independent save failed.");
                    Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown");
                    Set(session, "room", "blue"); Set(session, "transport", CollaborationTransport.LongPolling);
                    await (Task)Invoke(session, "JoinAsync"); Require(CollaborationSession.IsShared && manager.buildPath == "", "Blue join did not use Save As.");
                    Require(PartsManager.FindLoadedObjects().Count == 3 && Math.Abs(Body(customId).transform.position.x - 25) < .001, "Unity selected wrong room/custom pose.");
                    string blueSave = Path.Combine(root, "blue.pbb"); manager.buildPath = blueSave;
                    Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Blue independent save failed.");
                    Require(BuildsManager.ParsePath(redSave).parts.Length == 2 && BuildsManager.ParsePath(blueSave).parts.Length == 3, "Room saves mixed accepted scenes.");
                    Require(CollaborationRecovery.SessionFile(ready.url, "red", catalog.hash) != CollaborationRecovery.SessionFile(ready.url, "blue", catalog.hash), "Recovery scopes collide.");
                    peer.Dispose();
                }
                var blueClient = Get<CollaborationClient>(session, "client"); string blueIdentity = blueClient.Participant.id;
                await Stop(); await Until(() => !blueClient.IsConnected);
                await Start(ready.url); await (Task)Invoke(session, "ReconnectAsync");
                Require(blueClient.IsConnected && blueClient.Participant.id == blueIdentity && Math.Abs(Body(customId).transform.position.x - 25) < .001, "Dedicated restart lost blue identity/materialization.");
                Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown");
                Set(session, "room", "red"); await (Task)Invoke(session, "JoinAsync");
                Require(Get<CollaborationClient>(session, "client").Participant.id != blueIdentity && Math.Abs(Body(customId).transform.position.x - 10) < .001, "Red rejoin selected blue cache or content.");
                Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown");
            }
            finally { await Stop(); }
        }
        private static GameObject Body(string id) => PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == id);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static async Task Until(Func<bool> predicate) { for (int i = 0; i < 500; i++) { if (predicate()) return; await Task.Delay(10); } throw new TimeoutException("Multi-room Unity condition timed out."); }
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
