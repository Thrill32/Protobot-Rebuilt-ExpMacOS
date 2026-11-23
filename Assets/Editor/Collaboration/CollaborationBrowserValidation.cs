using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    internal static class CollaborationBrowserValidation {
        internal static async Task Check(CollaborationSession session, string root) {
            Require(CollaborationInvite.ValidateOrigin("cad.example.org") == "https://cad.example.org" &&
                CollaborationInvite.ValidateOrigin("cad.example.org:8443") == "https://cad.example.org:8443" &&
                CollaborationInvite.ValidateOrigin("http://127.0.0.1:8787") == "http://127.0.0.1:8787", "Origin normalization changed schemes or ports.");
            var builds = Object.FindFirstObjectByType<BuildsManager>();
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild); PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            builds.buildPath = Path.Combine(root, "browser-local.pbb");
            Require(await (Task<bool>)Invoke(builds, "SaveAsync", false), "Browser fixture save failed.");
            byte[] local = File.ReadAllBytes(builds.buildPath); string localPath = builds.buildPath;
            string registry = Path.Combine(root, "public-server");
            await Run("server-init", "--data-dir", registry, "--creation-policy", "open", "--directory-policy", "public");
            using var process = Process.Start(new ProcessStartInfo(CollaborationHost.HelperPath, "serve --multi-room --config " + Quote(Path.Combine(registry, "server.json")) + " --listen http://127.0.0.1:0") {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            });
            var errors = process.StandardError.ReadToEndAsync();
            try {
                var line = process.StandardOutput.ReadLineAsync(); Require(await Task.WhenAny(line, Task.Delay(30000)) == line, "Browser server readiness timed out.");
                var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(await line)); var drain = process.StandardOutput.ReadToEndAsync();
                var catalog = CollaborationScene.Catalog;
                var content = new RoomExport { catalog = catalog, document = new Document { generation = "browser-fixture", catalogHash = catalog.hash } };
                using (var creation = new RemoteRoomCreation(ready.url, network: NetworkPolicy.Direct)) {
                    for (int i = 0; i < 8; i++) await creation.CreateAsync(content, "browser-" + i, "browser-secret", null, false, Path.Combine(root, "fixture-owners"), CancellationToken.None);
                }
                await Click(session, "Collaboration");
                Get<InputField>(session, "uiServerHost").text = ready.url;
                await Click(session, "Browse Server"); await Until(() => Get<CancellationTokenSource>(session, "directoryRequest") == null);
                Invoke(session, "RefreshUI");
                Require(Get<RoomDirectoryEntry[]>(session, "directoryRooms").Length == 8 && LocalPreferences.GetString("Collaboration.LastServer") == ready.url, "Directory or last server was not retained.");
                Require(builds.buildPath == localPath && builds.CurrentBuildIsSaved && local.SequenceEqual(File.ReadAllBytes(localPath)), "Browsing changed local project/save state.");
                var scroll = Get<ScrollRect>(session, "uiDirectoryScroll");
                Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition = 1;
                var center = RectTransformUtility.WorldToScreenPoint(null, scroll.viewport.TransformPoint(scroll.viewport.rect.center));
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center, scroll = new Vector2(0, -120) }); await Task.Delay(100);
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center }); await Task.Delay(100);
                Require(scroll.verticalNormalizedPosition < .99f, "Physical wheel did not scroll room list.");
                await Click(session, "Directory Room browser-7");
                Require(Get<GameObject>(session, "uiNavigationJoin").activeInHierarchy && Get<string>(session, "room") == "browser-7", "Room selection did not open joining panel.");
                var password = Get<InputField>(session, "uiNavigationPassword"); await CollaborationUIValidation.Click(password.gameObject); password.text = "browser-secret";
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.Tab)); await Task.Delay(100);
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); await Task.Delay(100);
                Require(EventSystem.current.currentSelectedGameObject != null && EventSystem.current.currentSelectedGameObject != password.gameObject && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(Get<GameObject>(session, "uiRoot").transform), "Tab failed to move within collaboration UI.");
                await Capture(root, "selected-room");
                await Outside(session);
                Require(!CollaborationSession.BlocksUIInput && Get<string>(session, "password") == "", "Outside click leaked input capture or password.");
                await Click(session, "Collaboration"); await Click(session, "Directory Room browser-7"); password.text = "browser-secret";
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.Escape)); await Task.Delay(100);
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); await Task.Delay(100);
                Require(!CollaborationSession.BlocksUIInput && Get<string>(session, "password") == "", "Everyday Escape leaked password/focus.");
                await Click(session, "Collaboration"); await Click(session, "Directory Room browser-7"); password.text = "browser-secret";
                PartsManager.GeneratePart("CCHL-1x2-5", Vector3.right * 7, Quaternion.identity);
                await Until(() => !builds.CurrentBuildIsSaved);
                await Click(session, "Navigation Join");
                var prompt = Get<Protobot.UI.UnsavedChangesUI>(builds, "unsavedChangesMenu");
                await Until(() => prompt.gameObject.activeSelf);
                Require(!CollaborationSession.IsShared, "Join bypassed unsaved changes.");
                await CollaborationUIValidation.Click(Get<Button>(prompt, "discardButton").gameObject);
                await Until(() => CollaborationSession.IsShared && !Get<bool>(session, "busy"));
                var client = Get<CollaborationClient>(session, "client"); string id = client.Participant.id;
                Require(client.IsConnected && builds.buildPath == "" && local.SequenceEqual(File.ReadAllBytes(localPath)), "Join changed saved bytes or skipped Save As.");
                using var peer = new CollaborationClient(ready.url, "browser-7", catalog, CollaborationTransport.LongPolling);
                await peer.JoinAsync("Independent viewer", "browser-secret", viewer: true);
                await Until(() => Get<Participant[]>(session, "participants").Length == 2);
                Require(peer.Participant.role == "viewer" && client.Participant.role == "editor", "Two-player roles changed.");
                // Simulate application exit, which retains membership rather than explicit Leave.
                client.Dispose(); Set(session, "client", null); Invoke(session, "ClosePanel");
                await Click(session, "Collaboration"); await Click(session, "Directory Room browser-7"); Invoke(session, "RefreshUI");
                Require(!password.gameObject.activeInHierarchy && Get<Button>(session, "uiForgetAccess").gameObject.activeInHierarchy, "Saved access did not replace password entry.");
                await Click(session, "Navigation Join");
                await Until(() => CollaborationSession.IsShared || Get<bool>(session, "busy") || prompt.gameObject.activeSelf);
                if (prompt.gameObject.activeSelf) await CollaborationUIValidation.Click(Get<Button>(prompt, "discardButton").gameObject);
                await Until(() => CollaborationSession.IsShared && !Get<bool>(session, "busy"));
                client = Get<CollaborationClient>(session, "client"); Require(client.Participant.id == id, "Saved access created a new membership.");
                client.Dispose(); Set(session, "client", null); Invoke(session, "ClosePanel");
                await Click(session, "Collaboration"); await Click(session, "Directory Room browser-7"); await Click(session, "Forget Saved Access"); Invoke(session, "RefreshUI");
                Require(password.gameObject.activeInHierarchy && !File.Exists(CollaborationRecovery.SessionFile(ready.url, "browser-7", catalog.hash)), "Forget did not clear scoped access.");
                Require(!File.ReadAllText(Path.Combine(LocalPreferences.ProfileDirectory, "preferences.json")).Contains("browser-secret"), "Room password leaked into preferences.");
                await Click(session, "Navigation Back"); await Click(session, "Navigation Create Room");
                Require(Get<GameObject>(session, "uiCreateRoom").activeInHierarchy && Get<Dropdown>(session, "uiCreateEditorReplacement").value == 0, "Creation panel/default No missing.");
                await Capture(root, "create-panel");
                await Click(session, "Navigation Back"); await Click(session, "Navigation Server Admin");
                Require(Get<GameObject>(session, "uiServerAdmin").activeInHierarchy, "Adjacent admin form missing.");
                await Capture(root, "admin-panel"); await Outside(session);
                // Older/dedicated helpers have no public directory; direct ID must remain usable.
                var seed = content.CreateRoom("private-direct", "private-password", new string('b', 64));
                using var privateHost = new CollaborationHost(); await privateHost.StartAsync(seed, 0, false);
                await Click(session, "Collaboration"); await Click(session, "Add Server"); Get<InputField>(session, "uiServerHost").text = privateHost.Url;
                await Click(session, "Browse Server"); await Until(() => Get<CancellationTokenSource>(session, "directoryRequest") == null); Invoke(session, "RefreshUI");
                Require(Get<Text>(session, "uiDirectoryNotice").text.Contains("no public directory"), "No-directory feedback lost direct joining guidance.");
                await Click(session, "Join By ID"); Get<InputField>(session, "uiNavigationRoom").text = seed.room; password.text = "private-password";
                await Click(session, "Navigation Join");
                await Until(() => CollaborationSession.IsShared || Get<bool>(session, "busy") || prompt.gameObject.activeSelf);
                if (prompt.gameObject.activeSelf) await CollaborationUIValidation.Click(Get<Button>(prompt, "discardButton").gameObject);
                await Until(() => CollaborationSession.IsShared && !Get<bool>(session, "busy"));
                await Click(session, "Leave"); await Click(session, "Confirm Leave"); await Get<Task>(session, "hostShutdown"); Invoke(session, "ClosePanel");
                await privateHost.StopAsync();
                UnityEngine.Debug.Log("Everyday browser validation passed: real directory, wheel, selection, Tab/Escape/outside-click, unsaved prompt, two-player joining, protected saved access/Forget, creation/admin panels and no-directory direct joining. Renders: " + root);
            } finally { if (!process.HasExited) { process.Kill(); await Task.Run(() => process.WaitForExit()); } await errors; }
        }
        private static async Task Outside(CollaborationSession session) {
            var position = new Vector2(4, 40);
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); await Task.Delay(60);
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }.WithButton(MouseButton.Left)); await Task.Delay(60);
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); await Task.Delay(80);
        }
        private static async Task Capture(string root, string name) {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            string path = Path.Combine(root, name + ".png"); ScreenCapture.CaptureScreenshot(path); await Until(() => File.Exists(path) && new FileInfo(path).Length > 0);
        }
        private static async Task Click(CollaborationSession session, string name) { Invoke(session, "RefreshUI"); await Task.Delay(100); await CollaborationUIValidation.Click(Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>().Single(b => b.name == name).gameObject); }
        private static async Task Until(Func<bool> condition) { for (int i = 0; i < 1600; i++) { if (condition()) return; await Task.Delay(25); } throw new TimeoutException("Browser validation timed out."); }
        private static async Task Run(params string[] args) {
            using var child = Process.Start(new ProcessStartInfo(CollaborationHost.HelperPath, string.Join(" ", args.Select(Quote))) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync(); await Task.Run(() => child.WaitForExit()); await stdout; await stderr;
            Require(child.ExitCode == 0, "Browser helper command failed.");
        }
        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
