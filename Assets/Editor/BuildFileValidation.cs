using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Builds;
using Protobot;
using Protobot.UI;
using SFB;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    /// <summary>Exercise deferred native callbacks without displaying OS panels.</summary>
    public static class BuildFileValidation {
        private const string SmokeKey = "Protobot.BuildFileSmoke";
        private static int frames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0;
            EditorApplication.update += Tick;
        }

        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Build file validation passed: startup recent-file history without disk probes, async-only dialogs, Unity-thread callback dispatch, overlapping requests, save/open/recent round trips, Unicode and uppercase paths, shared read-only access, stream cleanup, cancellation, failure recovery, and save-before-load ordering.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static async Task Check() {
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            Require(manager != null, "Desktop startup did not enable the Main scene build manager.");
            var wrapperField = typeof(StandaloneFileBrowser).GetField("_platformWrapper", BindingFlags.NonPublic | BindingFlags.Static);
            var previousWrapper = wrapperField.GetValue(null);
            var dialogs = new DeferredDialogs();
            wrapperField.SetValue(null, dialogs);
            var directory = Path.Combine(Path.GetTempPath(), "Protobot file validation " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            bool hadDirectory = PlayerPrefs.HasKey("buildFileDirectory");
            var previousDirectory = PlayerPrefs.GetString("buildFileDirectory");
            bool hadRecent = PlayerPrefs.HasKey("recentBuilds");
            var previousRecent = PlayerPrefs.GetString("recentBuilds");
            int unityThread = Thread.CurrentThread.ManagedThreadId;
            int saves = 0, loads = 0;
            GameObject concurrentEdit = null;
            manager.OnSaveBuild.AddListener(_ => {
                Require(Thread.CurrentThread.ManagedThreadId == unityThread, "Save UI ran outside Unity's thread.");
                saves++;
            });
            manager.OnLoadBuild.AddListener(_ => {
                Require(Thread.CurrentThread.ManagedThreadId == unityThread, "Load UI ran outside Unity's thread.");
                loads++;
            });
            try {
                var recentList = Object.FindFirstObjectByType<BuildsListUI>(FindObjectsInactive.Include);
                Require(recentList != null, "Missing recent-build UI.");
                var unavailable = new[] { "/Volumes/Offline server/Robot.pbb", "/Users/Offline/Library/CloudStorage/Robot.pbb" };
                string history = unavailable[0] + "\n\n" + unavailable[1] + "\n" + unavailable[0] + "\n ";
                PlayerPrefs.SetString("recentBuilds", history);
                var recent = recentList.GetPaths();
                Require(recent.Count == 2 && recent[0] == unavailable[0] && recent[1] == unavailable[1],
                    "Recent-file UI probed/pruned unavailable paths instead of displaying saved history.");
                recentList.AddPath("");
                Require(PlayerPrefs.GetString("recentBuilds") == history, "UI refresh changed unavailable-file history.");
                if (hadRecent) PlayerPrefs.SetString("recentBuilds", previousRecent);
                else PlayerPrefs.DeleteKey("recentBuilds");

                // A callback on Cocoa's event stack must not execute Unity work inline.
                var callbackTask = NativeFileDialogs.OpenFileAsync("Test", "", "pbb");
                dialogs.OpenComplete(Array.Empty<string>());
                Require(!callbackTask.IsCompleted && NativeFileDialogs.IsOpen, "Native callback resumed Unity inline.");
                await callbackTask;
                Require(!NativeFileDialogs.IsOpen, "Completed panel retained its gate.");

                manager.OpenBuild();
                int openCalls = dialogs.OpenCalls;
                manager.OpenBuild();
                manager.SaveAs();
                Require(dialogs.OpenCalls == openCalls && dialogs.SaveCalls == 0, "Repeated clicks opened overlapping panels.");
                Require(!await manager.OpenPathAsync("ignored.pbb"), "Recent file bypassed a pending panel.");
                await Task.Run(() => dialogs.OpenComplete(null));
                await Idle(manager);
                Require(loads == 0 && manager.buildPath == "", "Open cancellation changed the build.");

                var savedPath = Path.Combine(directory, "Röbot 测试.PBB");
                manager.SaveAs();
                Require(manager.IsFileOperationInProgress && dialogs.SaveName == "Untitled", "Save As did not await its panel.");
                dialogs.SaveComplete(savedPath);
                Require(saves == 0 && manager.buildPath == "", "Save state changed before panel unwinding and disk write.");
                await Idle(manager);
                Require(saves == 1 && manager.buildPath == savedPath, "Save As did not commit its file and snapshot.");
                using (var reader = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Require(BuildsManager.ParsePath(savedPath)?.camera != null, "Build reads requested write/exclusive access.");
                Require(BuildsManager.PathToFileName(savedPath) == "Röbot 测试.PBB" &&
                    BuildsManager.PathToFileName(@"C:\Builds\Robot.pbb") == "Robot.pbb", "Cross-platform file names are wrong.");
                int saveDialogs = dialogs.SaveCalls;
                manager.Save();
                await Idle(manager);
                Require(saves == 2 && dialogs.SaveCalls == saveDialogs, "Save to an existing path reopened a panel.");

                var targetPath = Path.Combine(directory, "Target.pbb");
                var target = SceneBuild.DefaultBuild;
                target.camera.xPos = 3;
                using (var file = File.Create(targetPath)) new BinaryFormatter().Serialize(file, target);
                manager.OpenBuild();
                Require(dialogs.OpenDirectory == directory, "Open did not start in the saved build's directory.");
                dialogs.OpenComplete(new[] { targetPath });
                await Idle(manager);
                Require(loads == 1 && manager.buildPath == targetPath, "Open panel did not use the normal load path.");
                Require(await manager.OpenPathAsync(savedPath) && loads == 2 && manager.buildPath == savedPath,
                    "Recent-file path did not load through the same reader.");

                manager.SaveAs();
                dialogs.SaveComplete("");
                await Idle(manager);
                Require(manager.buildPath == savedPath && saves == 2, "Canceling Save As replaced the current save path.");

                var malformed = Path.Combine(directory, "Broken.pbb");
                File.WriteAllText(malformed, "not a build");
                try {
                    BuildsManager.ParsePath(malformed);
                    throw new Exception("Malformed build was accepted.");
                }
                catch (SerializationException) { }
                using (File.Open(malformed, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Require(!await manager.OpenPathAsync(Path.Combine(directory, "missing.pbb")) && !manager.IsFileOperationInProgress,
                    "A missing file did not release the operation gate.");
                Require(manager.buildPath == savedPath && loads == 2, "Failed load replaced the current scene/path.");

                manager.buildPath = "";
                Set(manager, "attemptData", target);
                Set(manager, "attemptPath", targetPath);
                manager.SaveAndLoadAttempt();
                dialogs.SaveComplete("");
                await Idle(manager);
                Require(loads == 2 && manager.buildPath == "", "Canceling Save Before Load discarded the current scene.");
                manager.SaveAndQuit();
                dialogs.SaveComplete(null);
                await Idle(manager);
                Require(!(bool)Get(manager, "forceQuit"), "Canceling Save Before Quit still quit.");

                // Simulate an edit between the snapshot/write and continuation.
                UnityEngine.Events.UnityAction<BuildData> editAfterSnapshot = _ => {
                    concurrentEdit = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    concurrentEdit.AddComponent<SavedObject>().id = "Error";
                };
                manager.OnSaveBuild.AddListener(editAfterSnapshot);
                manager.SaveAndLoadAttempt();
                dialogs.SaveComplete(Path.Combine(directory, "Edited during save.pbb"));
                var unsavedMenu = (UnsavedChangesUI)Get(manager, "unsavedChangesMenu");
                await Until(() => unsavedMenu.gameObject.activeSelf);
                Require(loads == 2 && concurrentEdit != null, "Save-before-load discarded an edit made after its snapshot.");
                manager.OnSaveBuild.RemoveListener(editAfterSnapshot);
                Object.DestroyImmediate(concurrentEdit);
                unsavedMenu.gameObject.SetActive(false);
                manager.buildPath = "";

                int savesBeforeLoad = saves;
                var saveBeforeLoad = Path.Combine(directory, "Before loading.pbb");
                manager.OnLoadBuild.AddListener(_ => {
                    Require(saves == savesBeforeLoad + 1 && File.Exists(saveBeforeLoad), "Load ran before the save finished.");
                });
                manager.SaveAndLoadAttempt();
                dialogs.SaveComplete(saveBeforeLoad);
                await Until(() => loads == 3);
                Require(manager.buildPath == targetPath, "Successful save-before-load did not finish the pending load.");
                Require(dialogs.SyncCalls == 0, "File UI called a synchronous panel.");
            }
            finally {
                wrapperField.SetValue(null, previousWrapper);
                if (concurrentEdit != null) Object.DestroyImmediate(concurrentEdit);
                if (hadDirectory) PlayerPrefs.SetString("buildFileDirectory", previousDirectory);
                else PlayerPrefs.DeleteKey("buildFileDirectory");
                if (hadRecent) PlayerPrefs.SetString("recentBuilds", previousRecent);
                else PlayerPrefs.DeleteKey("recentBuilds");
                Directory.Delete(directory, true);
            }
        }

        private static Task Idle(BuildsManager manager) => Until(() => !manager.IsFileOperationInProgress);
        private static async Task Until(Func<bool> condition) {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do {
                await Task.Delay(10);
                if (DateTime.UtcNow > deadline) throw new TimeoutException("File operation did not finish.");
            } while (!condition());
        }

        private static object Get(object obj, string field) => obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class DeferredDialogs : IStandaloneFileBrowser {
            public Action<string[]> OpenComplete;
            public Action<string> SaveComplete;
            public int OpenCalls, SaveCalls, SyncCalls;
            public string OpenDirectory, SaveName;

            public void OpenFilePanelAsync(string title, string directory, ExtensionFilter[] extensions, bool multiselect, Action<string[]> cb) {
                OpenCalls++; OpenDirectory = directory; OpenComplete = cb;
            }
            public void SaveFilePanelAsync(string title, string directory, string name, ExtensionFilter[] extensions, Action<string> cb) {
                SaveCalls++; SaveName = name; SaveComplete = cb;
            }
            public string[] OpenFilePanel(string title, string directory, ExtensionFilter[] extensions, bool multiselect) { SyncCalls++; throw new Exception("Sync open called."); }
            public string SaveFilePanel(string title, string directory, string name, ExtensionFilter[] extensions) { SyncCalls++; throw new Exception("Sync save called."); }
            public string[] OpenFolderPanel(string title, string directory, bool multiselect) => throw new NotSupportedException();
            public void OpenFolderPanelAsync(string title, string directory, bool multiselect, Action<string[]> cb) => throw new NotSupportedException();
        }
    }
}
