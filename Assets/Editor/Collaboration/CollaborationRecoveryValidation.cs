using System;
using System.Diagnostics;
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
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationRecoveryValidation {
        private const string Key = "Protobot.CollaborationRecoverySmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try {
                await Check();
                Debug.Log("Collaboration recovery validation passed: durable in-app host, helper kill/restart, private preview exclusion, same-identity reconnect, persistent undo/redo, unchanged local files, deliberate recovered-room opening/Save As and participant cache recovery.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var part = PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            string id = part.GetComponent<SavedObject>().ConnectionId;
            PartsManager.GeneratePart("PLTE-3-3", Vector3.right * 20, Quaternion.Euler(12, 27, 9));
            CollaborationSession.EnableDevelopment();
            var session = Object.FindFirstObjectByType<CollaborationSession>();
            Set(session, "port", "0"); Set(session, "password", "recovery-test-password");
            await (Task)Invoke(session, "HostAsync");
            var client = Get<CollaborationClient>(session, "client");
            Require(client?.IsConnected == true && client.IsDurable, "In-app hosting did not create a durable room.");
            var info = Get<CollaborationRecovery.HostedRoom>(session, "hostedRoom");
            Require(File.Exists(Path.Combine(info.directory, "room.sqlite")) && File.Exists(CollaborationRecovery.OwnerSession(info)), "Host recovery files were not created.");
            string ownerId = client.Participant.id;
            var host = Get<CollaborationHost>(session, "host"); string endpoint = host.Url;
            string root = LocalPreferences.ProfileDirectory ?? Path.Combine(Path.GetTempPath(), "protobot-recovery-" + Guid.NewGuid().ToString("N"));
            string peerCache = Path.Combine(root, "peer recovery", "session.json");
            var catalog = CollaborationScene.Catalog;
            var peer = new CollaborationClient(endpoint, "team", catalog, CollaborationTransport.LongPolling, recoveryFile: peerCache);
            try {
                await peer.JoinAsync("Recovery peer", "recovery-test-password");
                string peerId = peer.Participant.id;
                var move = WireJson.Clone(client.Snapshot.parts.Single(p => p.id == id)); move.position[0] = 7;
                var lease = await client.ReserveAsync(new[] { id });
                Require((await client.SubmitAsync(client.CreateTransaction("SetPoses", new[] { move }, lease: lease.token))).accepted, "Initial accepted edit failed.");
                await Applied(session, peer, 1);
                var manager = Object.FindFirstObjectByType<BuildsManager>();
                string savedPath = Path.Combine(root, "saved-before-room-restart.pbb"); manager.buildPath = savedPath;
                Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Local file save failed.");
                var added = WireJson.Clone(move); added.id = Guid.NewGuid().ToString("N"); added.position[0] = 40;
                Require((await peer.SubmitAsync(peer.CreateTransaction("AddParts", new[] { added }))).accepted, "Peer edit before crash failed.");
                await Applied(session, peer, 2);
                var keyboard = Object.FindFirstObjectByType<KeyboardTransformTool>(); keyboard.enabled = false;
                var selection = Get<SelectionManager>(keyboard, "selectionManager");
                selection.SetCurrent(new ObjectSelection { gameObject = Body(id), individual = true }, true, true);
                Invoke(keyboard, "BeginSelection", false); await Until(() => Get<bool>(keyboard, "active"));
                Set(keyboard, "displayedOffset", Vector3.up * 3); Invoke(keyboard, "ApplyDisplayedTransform");
                string oldLease = Get<string>(session, "lease");
                var process = Get<Process>(host, "process"); process.Kill(); await Task.Run(() => process.WaitForExit());
                await Until(() => !client.IsConnected && !Get<bool>(keyboard, "active"));
                Require(Body(id).transform.position == Vector3.right * 7, "Host crash retained a private preview.");
                await (Task)Invoke(session, "ReconnectAsync");
                await peer.PrepareReconnectAsync(); await peer.JoinAsync("Recovery peer");
                await Applied(session, peer, 2);
                Require(client.Participant.id == ownerId && client.Participant.role == "owner" && peer.Participant.id == peerId && host.Recovered,
                    "Helper restart lost durable room identities.");
                Require(PartsManager.FindLoadedObjects().Count == 3 && Body(id).transform.position == Vector3.right * 7 && manager.buildPath == savedPath,
                    "Helper recovery changed accepted poses or the local file association.");
                try { await client.ReserveAsync(new[] { id }, oldLease); throw new InvalidOperationException("Expected old reservation to be fenced."); }
                catch (CollaborationException error) { Require(error.Code == "lease-expired", "Unexpected stale lease result."); }
                Protobot.StateSystems.StateSystem.instance.Undo(); await Applied(session, peer, 3);
                Require(Body(id).transform.position == Vector3.zero && PartsManager.FindLoadedObjects().Count == 3, "Recovered personal undo changed someone else's disjoint create.");
                Protobot.StateSystems.StateSystem.instance.Redo(); await Applied(session, peer, 4);
                Require(BuildsManager.ParsePath(savedPath).parts.Length == 2 && !manager.CurrentBuildIsSaved, "Server recovery overwrote or falsely saved a participant's local file.");
                Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown");
                Require(Get<CollaborationHost>(session, "host") == null && !host.IsRunning && !File.Exists(CollaborationRecovery.OwnerSession(info)),
                    "Stop hosting did not finish revoking membership before stopping the helper.");
                var recovered = CollaborationRecovery.List(out var unreadable).Single(r => r.directory == info.directory);
                Require(!unreadable && !CollaborationSession.IsShared, "Listing recovered rooms reopened a listener.");
                await (Task)Invoke(session, "RecoverHostAsync", recovered);
                client = Get<CollaborationClient>(session, "client");
                Require(client?.IsConnected == true && client.Participant.role == "owner" && client.Participant.id != ownerId && manager.buildPath == "" &&
                    PartsManager.FindLoadedObjects().Count == 3 && Body(id).transform.position == Vector3.right * 7,
                    "Explicit recovery did not reopen accepted content as a new local Save As document.");
                peer.Dispose();
                peer = new CollaborationClient(Get<CollaborationHost>(session, "host").Url, "team", catalog, CollaborationTransport.LongPolling, recoveryFile: peerCache);
                await peer.JoinAsync("Returned participant");
                Require(peer.Participant.id == peerId && peer.Snapshot.revision == 4, "A new participant client did not restore its cached identity.");
                await peer.LeaveAsync();
                Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown"); File.Delete(savedPath);
            }
            finally { peer.Dispose(); }
        }
        private static GameObject Body(string id) => PartsManager.FindLoadedObjects().Single(b => b.GetComponent<SavedObject>().ConnectionId == id);
        private static async Task Applied(CollaborationSession session, CollaborationClient peer, long revision) {
            await Until(() => Get<Document>(session, "committed").revision == revision && !CollaborationSession.HasPending && CollaborationSession.CanPreview);
            await peer.WaitForRevisionAsync(revision);
            Require(CollaborationSession.CommittedHash == DocumentModel.Hash(peer.Snapshot), "Recovered participants diverged.");
        }
        private static async Task Until(Func<bool> predicate) {
            var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Unity recovery scenario timed out."); await Task.Delay(20); }
        }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
