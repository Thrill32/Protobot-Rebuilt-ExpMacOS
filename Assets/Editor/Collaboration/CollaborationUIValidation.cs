using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using SFB;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationUIValidation {
        private const string Key = "Protobot.CollaborationUISmoke";
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
                Debug.Log("Collaboration UI validation passed: modal input, Host, participants/viewers/save state, credential-free CLI export and accepted snapshot isolation, sustained Unity request diagnostics/cancellation, reconnect and deliberate leave/recovery controls.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            PartsManager.GeneratePart("CCHL-1x2-5", Vector3.right * 3, Quaternion.identity);
            CollaborationSession.EnableDevelopment();
            var session = Object.FindFirstObjectByType<CollaborationSession>();
            Invoke(session, "OpenPanel", 1); await Task.Delay(80); Canvas.ForceUpdateCanvases();
            var modal = Get<GameObject>(session, "uiModal");
            Require(CollaborationSession.BlocksUIInput && modal.activeSelf, "Collaboration did not capture editor tools.");
            // Real UI module must keep processing pointer and text while scene tools are blocked.
            var name = Get<InputField>(session, "uiName"); await Click(name.gameObject);
            Require(EventSystem.current.currentSelectedGameObject == name.gameObject, "Modal UI input module discarded field focus.");
            Require(name.navigation.mode == Navigation.Mode.Explicit && name.navigation.selectOnDown.transform.IsChildOf(modal.transform), "Keyboard navigation can leave the modal.");
            name.text = "UI tester";
            Require(name.isFocused, "The clicked field was selected without activating text entry.");
            // Legacy uGUI fields consume OS IMGUI key events; synthetic InputSystem
            // text events do not populate Event.PopEvent in a headless editor.
            name.ProcessEvent(new Event { type = EventType.KeyDown, character = '!' }); await Task.Delay(60);
            Require(name.text.Contains("!"), "The modal input field did not receive text input.");
            var pointer = new PointerEventData(EventSystem.current) { position = Vector2.one * 3 };
            var hits = new System.Collections.Generic.List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
            Require(hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(modal.transform), "Modal backdrop let scene UI receive a click.");
            GUIUtility.systemCopyBuffer = CollaborationInvite.Format("http://127.0.0.1:8787", "ui-room");
            Invoke(session, "PasteInvite");
            Require(Get<InputField>(session, "uiEndpoint").text == "http://127.0.0.1:8787" && Get<InputField>(session, "uiRoom").text == "ui-room", "Invite paste did not populate fields.");
            foreach (string address in new[] { "https://user:secret@example.org", "https://example.org/?password=secret", "https://example.org/rooms/a", "file:///tmp/a", "https://example.org/#token" }) {
                try { CollaborationInvite.ValidateOrigin(address); throw new InvalidOperationException("Unsafe invite origin accepted."); }
                catch (ArgumentException) { }
            }
            string ipv6 = CollaborationInvite.Format("http://[::1]:8787/", "ipv6-room");
            CollaborationInvite.Parse(ipv6.Replace("\n", "\r\n"), out var origin, out var room);
            Require(origin == "http://[::1]:8787" && room == "ipv6-room" && !ipv6.Contains("password"), "Invite IPv6/CRLF round trip failed.");
            Get<InputField>(session, "uiPort").text = "0";
            Invoke(session, "SelectUITab", 0); Canvas.ForceUpdateCanvases();
            await Click(Button(session, "Start Hosting").gameObject);
            Require(Get<bool>(session, "busy") || CollaborationSession.IsShared, "Host pointer click did not invoke its action: " + Get<string>(session, "status"));
            await Until(() => CollaborationSession.IsShared && !Get<bool>(session, "busy"));
            Invoke(session, "RefreshUI");
            var client = Get<CollaborationClient>(session, "client");
            Require(client.IsConnected && client.Participant.role == "owner", "Host button did not start the owned server.");
            Require(Get<Text>(session, "uiConnection").text.Contains("Connected") && Get<Text>(session, "uiMembers").text.Contains("(you)") && Get<Text>(session, "uiSaveStatus").text.Contains("Unsaved"), "Session status did not reflect actual state.");
            Button(session, "Copy Invite").onClick.Invoke(); CollaborationInvite.Parse(GUIUtility.systemCopyBuffer, out origin, out room);
            Require(origin == Get<CollaborationHost>(session, "host").Url && room == "ui-room", "Copied invite did not use the bound port and current room.");
            await CheckExport(session, origin, room);
            using (var peer = new CollaborationClient(origin, room, CollaborationScene.Catalog, CollaborationTransport.LongPolling)) {
                await peer.JoinAsync("Read-only peer", viewer: true);
                await Until(() => Get<Participant[]>(session, "participants").Length == 2); Invoke(session, "RefreshUI");
                Require(Get<Text>(session, "uiMembers").text.Contains("Read-only peer  -  viewer"), "Viewer role was missing from the participant UI.");
                var part = new Part { id = Guid.NewGuid().ToString("N"), catalogId = "CCHL-1x2-5" };
                try { await peer.SubmitAsync(peer.CreateTransaction("AddParts", new[] { part })); throw new InvalidOperationException("Viewer edit was allowed."); }
                catch (CollaborationException error) { Require(error.Code == "viewer-read-only", "Unexpected viewer rejection."); }
                await peer.LeaveAsync();
            }
            client.Suspend("UI test disconnect"); await Task.Delay(60); Invoke(session, "RefreshUI");
            Require(Get<Button>(session, "uiReconnect").gameObject.activeSelf, "Reconnect action did not appear.");
            Button(session, "Reconnect").onClick.Invoke(); await Until(() => client.IsConnected && !Get<bool>(session, "busy"));
            Button(session, "Leave").onClick.Invoke();
            Require(CollaborationSession.IsShared && Get<GameObject>(session, "uiLeaveConfirmation").activeSelf, "Leave did not wait for the in-app choice.");
            Button(session, "Cancel Leave").onClick.Invoke(); Require(CollaborationSession.IsShared, "Cancel leave detached the room.");
            Button(session, "Leave").onClick.Invoke(); Button(session, "Confirm Leave").onClick.Invoke();
            await Get<Task>(session, "hostShutdown"); Invoke(session, "RefreshUI");
            Require(!CollaborationSession.IsShared && CollaborationRecovery.List(out _).Length > 0, "Leave did not preserve hosted recovery data.");
            Require(CollaborationSession.CancelPanel() && !CollaborationSession.BlocksUIInput, "Closing the panel retained tool input capture.");
            Require(!Get<GameObject>(session, "uiModal").activeSelf, "Closed modal still intercepts pointer hits.");
            await CheckConnectionTest(session);
        }
        private static async Task CheckConnectionTest(CollaborationSession session) {
            var catalog = CollaborationScene.Catalog;
            var server = new CollaborationHost();
            try {
                await server.StartAsync(new Bootstrap { room = "ui-room", catalog = catalog, document = new Document { catalogHash = catalog.hash } }, 0, false);
                Invoke(session, "OpenPanel", 1);
                Get<InputField>(session, "uiEndpoint").text = server.Url;
                Get<Dropdown>(session, "uiTransport").value = (int)CollaborationTransport.LongPolling;
                var test = (Task)Invoke(session, "TestServerAsync"); await Until(() => !Get<bool>(session, "busy")); await test;
                Require(!CollaborationSession.IsShared && Get<Text>(session, "uiTestReport").text.Contains("requests-idle-events: passed"), "The Unity connection test failed or opened a shared document.");
                var canceled = (Task)Invoke(session, "TestServerAsync"); await Task.Delay(200); Invoke(session, "ClosePanel");
                await Until(() => !Get<bool>(session, "busy")); await canceled;
                Require(Get<string>(session, "status") == "Connection test canceled." && !CollaborationSession.IsShared, "Closing the diagnostic window did not cancel it cleanly.");
            }
            finally { await server.StopAsync(); }
        }
        private static async Task CheckExport(CollaborationSession session, string origin, string room) {
            var wrapper = typeof(StandaloneFileBrowser).GetField("_platformWrapper", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = wrapper.GetValue(null); var fake = new ExportDialog(); wrapper.SetValue(null, fake);
            string path = Path.Combine(LocalPreferences.ProfileDirectory, "CLI accepted snapshot.json");
            var builds = Object.FindFirstObjectByType<BuildsManager>(); string localPath = builds.buildPath; bool wasSaved = builds.CurrentBuildIsSaved;
            try {
                var export = (Task)Invoke(session, "ExportRoomAsync"); await Until(() => fake.Complete != null);
                using var remote = new CollaborationClient(origin, room, CollaborationScene.Catalog, CollaborationTransport.LongPolling);
                await remote.JoinAsync("Export timing peer");
                var added = WireJson.Clone(remote.Snapshot.parts[0]); added.id = Guid.NewGuid().ToString("N"); added.position[0] = 30;
                Require((await remote.SubmitAsync(remote.CreateTransaction("AddParts", new[] { added }))).accepted, "Remote edit during export failed.");
                await Until(() => Get<Document>(session, "committed").parts.Length == 2);
                fake.Complete(path); await export;
                var content = RecoveryFiles.Read<RoomExport>(path); content.Validate();
                Require(content.document.parts.Length == 1 && content.document.parts[0].position[0] == 3 && content.document.revision == 0,
                    "CLI export included later remote edits instead of its captured accepted snapshot.");
                Require(builds.buildPath == localPath && builds.CurrentBuildIsSaved == wasSaved && !File.ReadAllText(path).Contains("resumeToken") && !File.ReadAllText(path).Contains("password"),
                    "CLI export changed local save state or leaked session credentials.");
                await remote.LeaveAsync();
                var canceled = (Task)Invoke(session, "ExportRoomAsync"); await Until(() => fake.Complete != null); fake.Complete(""); await canceled;
                Require(!Get<bool>(session, "busy") && builds.buildPath == localPath, "Canceling export left the session busy or changed its local file.");
            }
            finally { wrapper.SetValue(null, previous); if (File.Exists(path)) File.Delete(path); }
        }
        private sealed class ExportDialog : IStandaloneFileBrowser {
            public Action<string> Complete;
            public void SaveFilePanelAsync(string title, string directory, string name, ExtensionFilter[] extensions, Action<string> callback) => Complete = value => { Complete = null; callback(value); };
            public string[] OpenFilePanel(string title, string directory, ExtensionFilter[] extensions, bool multiselect) => throw new NotSupportedException();
            public void OpenFilePanelAsync(string title, string directory, ExtensionFilter[] extensions, bool multiselect, Action<string[]> callback) => throw new NotSupportedException();
            public string SaveFilePanel(string title, string directory, string name, ExtensionFilter[] extensions) => throw new NotSupportedException();
            public string[] OpenFolderPanel(string title, string directory, bool multiselect) => throw new NotSupportedException();
            public void OpenFolderPanelAsync(string title, string directory, bool multiselect, Action<string[]> callback) => throw new NotSupportedException();
        }
        private static Button Button(CollaborationSession session, string name) => Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>(true).Single(b => b.name == name);
        private static async Task Click(GameObject target) {
            // Scroll the target into view before feeding actual pointer state to Main's input module.
            var scroll = target.GetComponentInParent<ScrollRect>();
            Canvas.ForceUpdateCanvases();
            if (scroll != null) {
                var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, target.transform);
                float top = scroll.viewport.rect.yMax - bounds.max.y;
                scroll.content.anchoredPosition += Vector2.up * top; Canvas.ForceUpdateCanvases();
                await Task.Delay(60); Canvas.ForceUpdateCanvases();
            }
            var rect = (RectTransform)target.transform; var center = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
            var hits = new System.Collections.Generic.List<RaycastResult>(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = center }, hits);
            Require(hits.Count > 0 && (hits[0].gameObject == target || hits[0].gameObject.transform.IsChildOf(target.transform)), "Pointer target " + target.name + " at " + center + " is covered by " + (hits.Count == 0 ? "nothing" : hits[0].gameObject.name));
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center }); await Task.Delay(60);
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center }.WithButton(MouseButton.Left)); await Task.Delay(60);
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center }); await Task.Delay(60);
        }
        private static async Task Until(Func<bool> condition) { var timeout = DateTime.UtcNow.AddSeconds(30); while (!condition()) { if (DateTime.UtcNow > timeout) throw new TimeoutException("Collaboration UI scenario timed out."); await Task.Delay(25); } }
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
