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
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationValidation {
        private const string Key = "Protobot.CollaborationSmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try {
                await Check();
                Debug.Log("Collaboration validation passed: actual managed helper launch, Unity shared-client WebSocket/HTTPS-request transports, join/remote materialization, reservation/ghost/cancel, exact G/R confirmation, preview-safe snapshots, remote delete, path isolation and helper shutdown.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var a = PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            var b = PartsManager.GeneratePart("PLTE-3-3", Vector3.right * 10, Quaternion.Euler(17, 31, 12));
            b.GetComponent<SavedObject>().state = "collaboration-fixture";
            PartColors.ApplyPaint(b, new Color(.2f, .3f, .4f));
            string paintedId = b.GetComponent<SavedObject>().ConnectionId;
            var catalog = CollaborationScene.Catalog;
            var initial = CollaborationScene.CaptureDocument(catalog);
            Require(initial.parts.Length == 2, "Host capture must include exactly two saved parts.");
            var savedPath = "collaboration-local-path.pbb";
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            manager.buildPath = savedPath;
            Set(manager, "savedBuildData", SceneBuild.ToBuildData());
            CollaborationSession.EnableDevelopment();
            var session = Object.FindFirstObjectByType<CollaborationSession>();
            Set(session, "port", "0"); Set(session, "displayName", "Hosting editor");
            // The scene uses exactly the same private helper lifecycle as players.
            await (Task)Invoke(session, "HostAsync");
            Require(CollaborationSession.IsShared && manager.buildPath == savedPath, "Hosting must retain local file association.");
            Require(manager.CurrentBuildIsSaved, "Hosting changed the current local file's saved marker.");
            var client = Get<CollaborationClient>(session, "client");
            Require(client != null && client.IsConnected, "Hosting editor did not connect.");
            var url = Get<string>(session, "endpoint");
            using var bob = new CollaborationClient(url, "team", catalog, CollaborationTransport.LongPolling);
            await bob.JoinAsync("Second editor");
            var added = WireJson.Clone(initial.parts[0]); added.id = Guid.NewGuid().ToString("N"); added.position[0] = 30;
            Require((await bob.SubmitAsync(bob.CreateTransaction("AddParts", new[] { added }))).accepted, "Remote add rejected.");
            await Until(() => PartsManager.FindLoadedObjects().Count == 3);
            Require(manager.buildPath == savedPath && CollaborationSession.SaveSnapshot().parts.Length == 3, "Remote events changed local path or omitted committed parts.");
            Require(!manager.CurrentBuildIsSaved, "Remote create did not dirty the host's existing local file marker.");
            var id = added.id;
            var body = PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == id);
            var ghostLease = await bob.ReserveAsync(new[] { id });
            var ghostPart = WireJson.Clone(added); ghostPart.position[0] += 5;
            await bob.PreviewAsync(new Preview { lease = ghostLease.token, sequence = 1, parts = new[] { ghostPart } });
            await Until(() => Get<System.Collections.Generic.Dictionary<string, GameObject>>(session, "ghosts").Count == 1);
            Require(PartsManager.FindLoadedObjects().Count == 3 && body.transform.position.x == 30 &&
                CollaborationSession.SaveSnapshot().parts.Single(p => p.connectionId == id).xPos == 30, "Remote ghost changed colliders/saved bodies or accepted snapshot.");
            await bob.ReleaseAsync(ghostLease.token);
            await Until(() => Get<System.Collections.Generic.Dictionary<string, GameObject>>(session, "ghosts").Count == 0);
            var selection = Object.FindFirstObjectByType<SelectionManager>();
            // Locate the same primary selection used by the keyboard transform controller.
            var keyboard = Object.FindFirstObjectByType<KeyboardTransformTool>();
            // Batch Mode has no focused game window. Drive the real commit/cancel methods explicitly.
            keyboard.enabled = false;
            selection = Get<SelectionManager>(keyboard, "selectionManager");
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            var baseline = body.transform.position;
            Set(keyboard, "displayedOffset", Vector3.up * 2);
            Invoke(keyboard, "ApplyDisplayedTransform");
            Require(Vector3.Distance(body.transform.position, baseline + Vector3.up * 2) < .001f, "G preview did not use actual Unity roots.");
            var savedPreview = CollaborationSession.SaveSnapshot().parts.Single(p => p.connectionId == id);
            Require(Math.Abs(savedPreview.yPos - baseline.y) < .001, "Save included uncommitted transform preview.");
            Invoke(keyboard, "Finish", false);
            Require(Vector3.Distance(body.transform.position, baseline) < .001f, "Escape/cancel did not restore pose.");
            await Task.Delay(150);
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            Invoke(keyboard, "SetTarget", Vector3.up * 3, Quaternion.identity);
            Invoke(keyboard, "CompleteConfirmation");
            Require(await CollaborationSession.WaitForSaveAsync(), "Save barrier did not resolve this participant's accepted edit.");
            Require((await WaitPart(bob, id, 3)).position[1] == 3, "G confirmation did not reach second client.");
            var acceptedSave = CollaborationSession.SaveSnapshot();
            Require(acceptedSave.parts.Single(p => p.connectionId == id).yPos == 3, "Accepted pose omitted from save snapshot.");
            savedPath = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "accepted-collaboration-" + Guid.NewGuid().ToString("N") + ".pbb");
            manager.buildPath = savedPath;
            Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Accepted local save failed.");
            Require(BuildsManager.ParsePath(savedPath).parts.Single(p => p.connectionId == id).yPos == 3 &&
                !(bool)Invoke(manager, "HasUnsavedChanges"), "Local file/marker omitted accepted revision.");
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            Invoke(keyboard, "SetTarget", Vector3.up, Quaternion.identity);
            client.Suspend("Development test: disconnect immediately before confirmation.");
            Invoke(keyboard, "CompleteConfirmation");
            Require(body.transform.position.y == 3, "Late confirmation after disconnect did not restore the accepted pose.");
            await (Task)Invoke(session, "ReconnectAsync");
            Require(client.IsConnected && manager.buildPath == savedPath, "Reconnect lost membership or local file association.");
            // Verify render/material mapping independently of the accepted network hash.
            var painted = PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == paintedId);
            Require(painted.GetComponent<SavedObject>().state == "collaboration-fixture" && painted.GetComponent<SavedObject>().hasPaint &&
                Math.Abs(PartColors.GetColor(painted).g - .3f) < .001f, "Materialization lost paint/state.");
            var reservation = await bob.ReserveAsync(new[] { id });
            Require((await bob.SubmitAsync(bob.CreateTransaction("DeleteParts", Array.Empty<Part>(), new[] { id }, lease: reservation.token))).accepted, "Remote delete rejected.");
            await Until(() => PartsManager.FindLoadedObjects().Count == 2);
            Require(selection.current == null, "Remote deletion left invalid selection.");
            Require(manager.buildPath == savedPath, "A participant changed the host's local save path.");
            Require((bool)Invoke(manager, "HasUnsavedChanges"), "A newer remote edit did not leave the saved participant dirty.");
            Require(BuildsManager.ParsePath(savedPath).parts.Length == 3, "Remote edit modified an already saved local file.");
            File.Delete(savedPath);
            // Check Unity's WebSocket runtime separately from the forced request client.
            await Until(() => client.ActiveTransport == "websocket");
            await bob.LeaveAsync();
            var owned = Get<CollaborationHost>(session, "host");
            Invoke(session, "Detach");
            await owned.StopAsync();
            Require(!CollaborationSession.IsShared && PartsManager.FindLoadedObjects().Count == 2, "Leave must preserve last accepted local document.");
            // Join path uses normal file-save handling and clears path only for newly joined content.
            await CheckJoinAsync(catalog, initial, session, manager);
        }
        private static async Task CheckJoinAsync(CatalogManifest catalog, Document initial, CollaborationSession session, BuildsManager manager) {
            using var host = new CollaborationHost();
            try {
                await host.StartAsync(new Bootstrap { catalog = catalog, document = initial }, 0, false);
                Set(session, "endpoint", host.Url); Set(session, "transport", CollaborationTransport.LongPolling);
                await (Task)Invoke(session, "JoinAsync");
                Require(CollaborationSession.IsShared && manager.buildPath == "", "Newly joined document must default to Save As.");
                Require(PartsManager.FindLoadedObjects().Count == initial.parts.Length, "Join did not materialize complete snapshot.");
                Invoke(session, "Detach");
            }
            finally { await host.StopAsync(); }
        }
        private static async Task<Part> WaitPart(CollaborationClient client, string id, double y) {
            await Until(() => client.Snapshot.parts.Single(p => p.id == id).position[1] == y);
            return client.Snapshot.parts.Single(p => p.id == id);
        }
        private static async Task Until(Func<bool> predicate) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate()) { if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("Unity collaboration scenario timed out."); await Task.Delay(20); }
        }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
