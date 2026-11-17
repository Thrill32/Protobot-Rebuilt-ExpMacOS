using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationShutdownValidation {
        private const string Key = "Protobot.ShutdownSmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog(); EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod] private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>(); if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try {
                await Check();
                Debug.Log("Collaboration shutdown validation passed: three-transport host-stop feedback, private preview rollback, saved bytes/path/marker and membership retained, actual pointer reconnect/leave/stop-confirm/cancel, durable personal history and independent helper isolation.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            CollaborationSession.EnableDevelopment(); var session = Object.FindFirstObjectByType<CollaborationSession>();
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            string root = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "shutdown-check"); RecoveryFiles.CreateDirectory(root);
            var catalog = CollaborationScene.Catalog;
            foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
                SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
                var part = PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity); string id = part.GetComponent<SavedObject>().ConnectionId;
                string path = Path.Combine(root, "before-" + transport + ".pbb"); manager.buildPath = path;
                Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Initial save failed.");
                var seed = new Bootstrap { room = "shutdown-" + transport.ToString().ToLowerInvariant(), catalog = catalog,
                    ownerCredential = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), document = CollaborationScene.CaptureDocument(catalog) };
                var info = CollaborationRecovery.Create(seed, 0);
                using var authority = new CollaborationHost(); await authority.StartAsync(seed, 0, false, info.directory);
                try {
                    Set(session, "endpoint", authority.Url); Set(session, "room", seed.room); Set(session, "transport", transport);
                    Invoke(session, "OpenPanel", 1); Get<Dropdown>(session, "uiNetworkRoute").value = 1;
                    await Click(session, "Join Room"); await Until(() => !Get<bool>(session, "busy"));
                    var client = Get<CollaborationClient>(session, "client"); Require(client?.IsConnected == true, "Pointer Join failed.");
                    await Until(() => client.ActiveTransport != null); string identity = client.Participant.id;
                    var pose = WireJson.Clone(client.Snapshot.parts.Single()); pose.position[0] = 7;
                    var lease = await client.ReserveAsync(new[] { id });
                    Require((await client.SubmitAsync(client.CreateTransaction("SetPoses", new[] { pose }, lease: lease.token))).accepted, "Accepted pose failed.");
                    await Until(() => Get<Document>(session, "committed").revision == 1); await client.ReleaseAsync(lease.token);
                    path = Path.Combine(root, "accepted-" + transport + ".pbb"); manager.buildPath = path;
                    Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Accepted save failed."); byte[] saved = File.ReadAllBytes(path);
                    var keyboard = Object.FindFirstObjectByType<KeyboardTransformTool>(); keyboard.enabled = false;
                    var selection = Get<SelectionManager>(keyboard, "selectionManager");
                    selection.SetCurrent(new ObjectSelection { gameObject = Body(id), individual = true }, true, true);
                    Invoke(session, "ClosePanel"); Invoke(keyboard, "BeginSelection", false); await Until(() => Get<bool>(keyboard, "active"));
                    Set(keyboard, "displayedOffset", Vector3.up * 3); Invoke(keyboard, "ApplyDisplayedTransform");
                    int port = authority.BoundPort; await authority.StopAsync();
                    await Until(() => !client.IsConnected && Get<string>(session, "status").Contains("host stopped") && !Get<bool>(keyboard, "active"));
                    Require(Body(id).transform.position == Vector3.right * 7 && manager.buildPath == path && manager.CurrentBuildIsSaved && File.ReadAllBytes(path).SequenceEqual(saved),
                        "Deliberate shutdown changed accepted scene or local saved bytes/path/marker.");
                    Require(client.Participant.id == identity && !client.IsMembershipRevoked && client.PendingCount == 0 &&
                        client.CaptureDiagnostics().events.Any(e => e.code == "server-stopping"), "Shutdown lost recoverable identity or fixed diagnostic reason.");
                    Invoke(session, "OpenPanel", 1); Invoke(session, "RefreshUI");
                    Require(Get<Text>(session, "uiStatus").text.Contains("host stopped") && Get<Button>(session, "uiReconnect").interactable, "Shutdown feedback/reconnect control is unavailable.");
                    await authority.StartAsync(null, port, false, info.directory);
                    await Click(session, "Reconnect"); await Until(() => !Get<bool>(session, "busy"));
                    Require(client.IsConnected && client.Participant.id == identity && manager.buildPath == path && manager.CurrentBuildIsSaved,
                        "Pointer reconnect changed identity or local save marker.");
                    Require((await client.SubmitAsync(client.CreateTransaction("Undo", Array.Empty<Part>()))).accepted, "Personal history did not survive graceful stop.");
                    await Until(() => Get<Document>(session, "committed").revision == 2);
                    Require(Body(id).transform.position == Vector3.zero && !manager.CurrentBuildIsSaved && File.ReadAllBytes(path).SequenceEqual(saved), "Recovered undo overwrote local file or failed to update scene.");
                    await Click(session, "Leave"); await Click(session, "Cancel Leave"); Require(client.IsConnected, "Leave cancellation disconnected the participant.");
                    await Click(session, "Leave"); await Click(session, "Confirm Leave"); await Get<Task>(session, "hostShutdown");
                    Require(!CollaborationSession.IsShared && authority.IsRunning && manager.buildPath == path && File.ReadAllBytes(path).SequenceEqual(saved), "Participant Leave stopped an independent helper or changed local file.");
                    keyboard.enabled = true;
                }
                finally { if (CollaborationSession.IsShared) { Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown"); } await authority.StopAsync(); }
            }
            // Exercise the actual owned-host confirmation with a second process alive.
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild); PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            var unrelatedSeed = new Bootstrap { room = "independent", catalog = catalog, document = CollaborationScene.CaptureDocument(catalog) };
            using var independent = new CollaborationHost(); await independent.StartAsync(unrelatedSeed, 0, false);
            try {
                Set(session, "room", "owned-stop"); Set(session, "port", "0"); Invoke(session, "OpenPanel", 0);
                Get<Dropdown>(session, "uiTransport").value = (int)CollaborationTransport.LongPolling;
                await Click(session, "Start Hosting"); await Until(() => !Get<bool>(session, "busy"));
                var client = Get<CollaborationClient>(session, "client"); var host = Get<CollaborationHost>(session, "host");
                Require(client?.IsConnected == true && host?.IsRunning == true, "Pointer owned Host failed.");
                using var peer = new CollaborationClient(host.Url, "owned-stop", catalog, CollaborationTransport.ShortPolling);
                string reason = null; peer.Disconnected += value => reason = value; await peer.JoinAsync("Stop observer");
                await Click(session, "Leave"); await Click(session, "Cancel Leave"); Require(host.IsRunning && client.IsConnected, "Stop cancellation stopped hosting.");
                await Click(session, "Leave"); await Click(session, "Confirm Leave"); await Get<Task>(session, "hostShutdown");
                await Until(() => reason == "server-stopping");
                Require(!host.IsRunning && independent.IsRunning && !CollaborationSession.IsShared && !peer.IsMembershipRevoked,
                    "Owned Stop affected an independent process or revoked a participant.");
            }
            finally { if (CollaborationSession.IsShared) { Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown"); } await independent.StopAsync(); }
        }
        private static GameObject Body(string id) => PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == id);
        private static async Task Click(CollaborationSession session, string name) { Invoke(session, "RefreshUI"); await CollaborationUIValidation.Click(Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>(true).Single(b => b.name == name).gameObject); }
        private static async Task Until(Func<bool> condition) { for (int i = 0; i < 1200; i++) { if (condition()) return; await Task.Delay(25); } throw new TimeoutException("Shutdown UI condition timed out."); }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
