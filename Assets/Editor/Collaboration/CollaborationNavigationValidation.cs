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
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationNavigationValidation {
        private const string Key = "Protobot.CollaborationNavigationSmoke";
        private static int frames, assertions;
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
            try { await Check(); Debug.Log("Collaboration navigation passed: " + assertions + " assertions; actual top-bar pointer, responsive layout/render, role pages, private Close/Escape, owner session and live participant counts."); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            CollaborationSession.EnableDevelopment(); var session = Object.FindFirstObjectByType<CollaborationSession>();
            var title = (RectTransform)Object.FindFirstObjectByType<Protobot.UI.BuildNameText>().transform;
            var badge = Get<RectTransform>(session, "uiBadge");
            string root = Path.Combine(LocalPreferences.ProfileDirectory, "navigation-render"); Directory.CreateDirectory(root);
            // Use temporary fixed Game View sizes, restoring the editor's selection
            // and removing only the custom sizes created by this scenario.
            var editor = typeof(EditorWindow).Assembly;
            var sizesType = editor.GetType("UnityEditor.GameViewSizes");
            var sizes = sizesType.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var groupType = editor.GetType("UnityEditor.GameViewSizeGroupType");
            var group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
            var view = EditorWindow.GetWindow(editor.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
            var selected = view.GetType().GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int oldSelected = (int)selected.GetValue(view);
            var sizeType = editor.GetType("UnityEditor.GameViewSize"); var sizeKind = editor.GetType("UnityEditor.GameViewSizeType");
            int builtins = (int)group.GetType().GetMethod("GetBuiltinCount").Invoke(group, null);
            for (int i = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null) - 1; i >= builtins; i--) {
                var entry = group.GetType().GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                if ((string)entry.GetType().GetProperty("baseText").GetValue(entry) == "Collaboration navigation validation")
                    group.GetType().GetMethod("RemoveCustomSize").Invoke(group, new object[] { i });
            }
            int added = 0, originalCustomCount = (int)group.GetType().GetMethod("GetCustomCount").Invoke(group, null);
            try {
                foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(960, 640), new Vector2Int(800, 600) }) {
                    int index = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
                    var entry = Activator.CreateInstance(sizeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { Enum.Parse(sizeKind, "FixedResolution"), size.x, size.y, "Collaboration navigation validation" }, null);
                    group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { entry }); added++;
                    selected.SetValue(view, index); view.Repaint(); EditorApplication.QueuePlayerLoopUpdate();
                    await Until(() => Screen.width == size.x && Screen.height == size.y); await Task.Delay(200); Canvas.ForceUpdateCanvases();
                    Invoke(session, "PositionCollaborationButton"); Invoke(session, "RefreshUI");
                    var b = Bounds(badge); var t = Bounds(title);
                    Require(b.xMin > t.xMax && Mathf.Abs(b.center.y - t.center.y) < 2 && b.xMax <= Screen.width && b.yMax <= Screen.height,
                        "Top-bar entry overlaps the filename or exceeds the window at " + size);
                    foreach (var control in title.parent.GetComponentsInChildren<Selectable>().Where(c => c.gameObject.activeInHierarchy)) {
                        var other = Bounds((RectTransform)control.transform);
                        Require(!b.Overlaps(other), "Top-bar entry covers existing control " + control.name + " at " + size);
                    }
                    Require(Get<Text>(session, "uiBadgeText").text == "Collaboration", "Offline entry label changed.");
                    await Click(session, "Collaboration"); Require(CollaborationSession.BlocksUIInput, "Top-bar pointer failed to open modal.");
                    Require(Get<GameObject>(session, "uiNavigation").activeInHierarchy && !Get<GameObject>(session, "uiModal").activeInHierarchy, "Everyday entry did not open the server popover.");
                    var popover = Bounds(Get<RectTransform>(session, "uiNavigationCard"));
                    Require(popover.xMin >= 0 && popover.xMax <= Screen.width && popover.yMin >= 0 && popover.yMax < Screen.height, "Popover exceeds window at " + size);
                    foreach (var control in Get<RectTransform>(session, "uiNavigationCard").GetComponentsInChildren<Selectable>()) {
                        var bounds = Bounds((RectTransform)control.transform);
                        Require(bounds.xMin >= popover.xMin && bounds.xMax <= popover.xMax && bounds.yMin >= popover.yMin && bounds.yMax <= popover.yMax, "Browser control escaped popover: " + control.name + " at " + size);
                    }
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) {
                        string image = Path.Combine(root, "browser-" + size.x + ".png"); ScreenCapture.CaptureScreenshot(image);
                        await Until(() => File.Exists(image) && new FileInfo(image).Length > 0);
                    }
                    await Click(session, "Advanced Collaboration");
                    await Click(session, "Owned Rooms Tab");
                    Require(Get<GameObject>(session, "uiOwnedRooms").activeInHierarchy && !Get<GameObject>(session, "uiCreateRoom").activeInHierarchy &&
                        !Get<InputField>(session, "uiPassword").gameObject.activeInHierarchy, "Owned rooms leaked creation/password controls.");
                    var card = Bounds(Get<RectTransform>(session, "uiCard"));
                    foreach (var tab in Get<Button[]>(session, "uiPageButtons")) {
                        var rect = Bounds((RectTransform)tab.transform);
                        Require(rect.xMin >= card.xMin && rect.xMax <= card.xMax && rect.yMin >= card.yMin && rect.yMax <= card.yMax,
                            "Role navigation escaped the modal at " + size);
                    }
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) {
                        string image = Path.Combine(root, "owned-" + size.x + ".png"); ScreenCapture.CaptureScreenshot(image);
                        await Until(() => File.Exists(image) && new FileInfo(image).Length > 0);
                    }
                    await Click(session, "Create First Owned Room"); Require(Get<GameObject>(session, "uiCreateRoom").activeInHierarchy, "Empty ownership page lacks a creation route.");
                    await Click(session, "Server Admin Tab"); Require(!Get<InputField>(session, "uiRoom").gameObject.activeInHierarchy &&
                        !Get<InputField>(session, "uiPassword").gameObject.activeInHierarchy, "Server admin exposes participant admission fields.");
                    await Click(session, "Admin Create Room"); Require(Get<Dropdown>(session, "uiCreationAuthority").value == 2, "Admin creation route selected wrong authority.");
                    Get<InputField>(session, "uiPassword").text = "private-room-input";
                    Get<InputField>(session, "uiCreationKey").text = "private-creation-input";
                    await Click(session, "Close");
                    Require(Get<string>(session, "password") == "" && Get<string>(session, "creationKey") == "" && !CollaborationSession.BlocksUIInput,
                        "Close retained room credentials or modal capture.");
                    await Click(session, "Collaboration"); await Click(session, "Advanced Collaboration"); await Click(session, "Join Tab"); Get<InputField>(session, "uiPassword").text = "private-room-input";
                    InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.Escape)); await Task.Delay(100);
                    InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); await Task.Delay(100);
                    Require(Get<string>(session, "password") == "" && !CollaborationSession.BlocksUIInput, "Escape retained private room input or modal capture.");
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) {
                        string image = Path.Combine(root, "topbar-" + size.x + ".png"); ScreenCapture.CaptureScreenshot(image);
                        await Until(() => File.Exists(image) && new FileInfo(image).Length > 0);
                    }
                    if (size.x == 1280) await CollaborationBrowserValidation.Check(session, root);
                    await Click(session, "Collaboration"); await Click(session, "Navigation Host Locally"); Invoke(session, "RefreshUI");
                    Require(Get<RectTransform>(session, "uiNavigationCard").gameObject.activeInHierarchy == (size.x >= 1100), "Compact-window fallback did not switch to a single panel.");
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) {
                        string image = Path.Combine(root, "host-panel-" + size.x + ".png"); ScreenCapture.CaptureScreenshot(image);
                        await Until(() => File.Exists(image) && new FileInfo(image).Length > 0);
                    }
                    await Click(session, "Detail Close");
                }
                SceneBuild.GenerateBuild(SceneBuild.DefaultBuild); PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
                await Click(session, "Collaboration"); await Click(session, "Navigation Host Locally"); Get<InputField>(session, "uiPort").text = "0";
                await Click(session, "Start Hosting"); await Until(() => CollaborationSession.IsShared && !Get<bool>(session, "busy"));
                var client = Get<CollaborationClient>(session, "client"); var host = Get<CollaborationHost>(session, "host");
                await Until(() => Get<Participant[]>(session, "participants").Length == 1); Invoke(session, "RefreshUI");
                Require(Get<Text>(session, "uiBadgeText").text == "Collaboration (1)", "Session entry omits owner count.");
                using (var peer = new CollaborationClient(host.Url, Get<string>(session, "room"), CollaborationScene.Catalog, CollaborationTransport.LongPolling)) {
                    await peer.JoinAsync("Counted viewer", viewer: true);
                    await Until(() => Get<Participant[]>(session, "participants").Length == 2); Invoke(session, "RefreshUI");
                    Require(Get<Text>(session, "uiBadgeText").text == "Collaboration (2)", "Session entry failed to count a joining participant.");
                    await peer.LeaveAsync();
                }
                await Until(() => Get<Participant[]>(session, "participants").Length == 1); Invoke(session, "RefreshUI");
                Require(Get<Text>(session, "uiBadgeText").text == "Collaboration (1)", "Session entry retained a departed participant.");
                await Click(session, "Leave"); await Click(session, "Cancel Leave"); Require(client.IsConnected, "Cancel ended the owner session.");
                await Click(session, "Leave"); await Click(session, "Confirm Leave"); await Get<Task>(session, "hostShutdown"); Invoke(session, "RefreshUI");
                Require(Get<Text>(session, "uiBadgeText").text == "Collaboration", "Leaving did not restore the default entry label.");
                Debug.Log("Navigation renders: " + root);
            }
            finally {
                selected.SetValue(view, oldSelected);
                while (added-- > 0) group.GetType().GetMethod("RemoveCustomSize").Invoke(group, new object[] { builtins + originalCustomCount + added });
            }
        }
        private static Rect Bounds(RectTransform rect) { var corners = new Vector3[4]; rect.GetWorldCorners(corners); return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y); }
        private static async Task Click(CollaborationSession session, string name) {
            Invoke(session, "RefreshUI"); await Task.Delay(80); Canvas.ForceUpdateCanvases();
            await CollaborationUIValidation.Click(Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>().Single(b => b.name == name).gameObject);
        }
        private static async Task Until(Func<bool> condition) { for (int i = 0; i < 1200; i++) { if (condition()) return; await Task.Delay(25); } throw new TimeoutException("Navigation validation timed out."); }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
    }
}
