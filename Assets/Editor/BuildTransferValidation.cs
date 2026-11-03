using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using DG.Tweening;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.StateSystems;
using Protobot.Tools;
using SFB;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class BuildTransferValidation {
        private const string SmokeKey = "Protobot.BuildTransferSmoke";
        private static int frames;

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var tool = Object.FindFirstObjectByType<BuildTransferTool>();
            Require(tool != null, "Missing transfer controller in Main.");
            var serialized = new SerializedObject(tool);
            foreach (var field in new[] { "buildsManager", "selectionManager", "placement", "duplicator", "copyInput", "pasteInput", "exportInput", "importInput" })
                Require(serialized.FindProperty(field).objectReferenceValue != null, "Missing transfer reference: " + field);
            foreach (var name in new[] { "Save Selected Parts As Button", "Import Build Button", "Copy Selection Button", "Paste Selection Button" })
                Require(Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(b => b.name == name), "Missing menu item: " + name);
            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var binding in BuildTransferSetup.Bindings)
                Require(rows.Any(row => (new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue as InputEvent)?.name == binding.name), "Missing transfer Preferences row.");
            Require(rows.Where(row => BuildTransferSetup.Bindings.Any(binding => binding.name == (new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue as InputEvent)?.name))
                .All(row => row.transform.parent.name == "Files Prefs Menu"), "Transfer rows must use their own Preferences category.");
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(SmokeKey, false)) { frames = 0; EditorApplication.update += Tick; } }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Build transfer validation passed: selection-only always-Save-As, unchanged current project/path/save state, deferred async panels and cancellation/failure cleanup, cross-project Command-C/V and text-field guards, independent one-shot placement/selection, undo/redo, fresh identities, complete-chain remapping, custom model/mount/color/state persistence and legacy project import.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static async Task Check() {
            var tool = Object.FindFirstObjectByType<BuildTransferTool>();
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            var selection = Get<SelectionManager>(tool, "selectionManager");
            var placement = Get<Placement>(tool, "placement");
            var originalClipboard = GUIUtility.systemCopyBuffer;
            var wrapper = typeof(StandaloneFileBrowser).GetField("_platformWrapper", BindingFlags.NonPublic | BindingFlags.Static);
            var originalWrapper = wrapper.GetValue(null);
            var dialogs = new DeferredDialogs(); wrapper.SetValue(null, dialogs);
            var directory = Path.Combine(Path.GetTempPath(), "Protobot transfer " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var prefKeys = new[] { "selectionFileDirectory", "buildFileDirectory", "recentBuilds" };
            var prefs = prefKeys.Select(key => (key, present: PlayerPrefs.HasKey(key), value: PlayerPrefs.GetString(key))).ToArray();
            int saves = 0, loads = 0;
            manager.OnSaveBuild.AddListener(_ => saves++); manager.OnLoadBuild.AddListener(_ => loads++);
            try {
                EventSystem.current.SetSelectedGameObject(null);
                var center = new Vector3(300, 300, 300);
                var structural = PartsManager.GeneratePart("CCHL-1x2-5", center, Quaternion.Euler(13, 29, 17));
                structural.GetComponent<Renderer>().material.color = Color.red;
                structural.GetComponent<SavedObject>().state = "transfer-state";
                var custom = CustomModel.Create(CustomModelImporter.Parse(Encoding.UTF8.GetBytes("v -1 -1 0\nv 1 -1 0\nv 0 1 0\nf 1 2 3"), ".obj", "Portable model", 1), center + Vector3.up * 4, Quaternion.Euler(19, 37, 23));
                custom.GetComponent<Renderer>().material.color = Color.green;
                custom.GetComponent<CustomModel>().AddMount(custom.transform.position, custom.transform.forward, .169f, .1f, true);
                var sprocketA = PartsManager.GeneratePart("SPKT-Normal-10T", center + Vector3.right * 6, Quaternion.identity);
                var sprocketB = PartsManager.GeneratePart("SPKT-Normal-40T", center + Vector3.right * 10, Quaternion.identity);
                Require(ChainLoop.Create(new[] { sprocketA.GetComponent<SimulationPart>(), sprocketB.GetComponent<SimulationPart>() }) != null, "Could not create source chain.");
                var excluded = PartsManager.GeneratePart("PLTE-3-3", center + Vector3.down * 5, Quaternion.identity);
                var selected = new[] { structural, custom, sprocketA, sprocketB };
                selection.SetCurrent(new MultiSelection(null, selected.ToList()) { individual = true }, true);
                var captured = BuildTransfer.Capture(selection.current);
                Require(captured.parts.Length == 4 && captured.chains.Length == 1 && captured.parts.Count(p => p.IsCustomModel) == 1, "Selection snapshot omitted parts/custom geometry/chains.");
                var partial = SceneBuild.ToBuildData(new[] { structural, sprocketA });
                Require(partial.chains.Length == 0, "Partial selection exported a dangling chain.");
                var sourceIds = captured.parts.Select(p => p.connectionId).ToHashSet();
                // Ignore malformed/incomplete old chain records rather than leaking unrecorded placed geometry.
                var ignoredChains = captured.chains;
                captured.chains = captured.chains.Concat(new[] { new ChainData { members = new string[] { null, "missing" } } }).ToArray();
                using (var preview = SelectionPlacementData.FromBuild(captured, placement.transform, Get<ObjectDuplicator>(tool, "duplicator"), selection)) {
                    var temporary = preview.Generate(new Vector3(800, 800, 800), Quaternion.identity);
                    Require(preview.GeneratedChains.Count == 1, "Invalid chain metadata prevented otherwise valid placement.");
                    foreach (var chain in preview.GeneratedChains) Object.DestroyImmediate(chain);
                    ObjectDuplicator.DiscardCopies(temporary);
                }
                captured.chains = ignoredChains;
                var projectPath = Path.Combine(directory, "Current Project.pbb");
                var savedProject = SceneBuild.ToBuildData();
                Write(projectPath, savedProject);
                var projectBytes = File.ReadAllBytes(projectPath);
                manager.buildPath = projectPath; Set(manager, "savedBuildData", savedProject);
                excluded.transform.position += Vector3.right;
                Require((bool)Invoke(manager, "HasUnsavedChanges"), "Fixture must begin with an unsaved edit.");
                string recent = PlayerPrefs.GetString("recentBuilds");

                var exportTask = tool.SaveSelectedAsAsync();
                Require(dialogs.SaveCalls == 1 && dialogs.SaveTitle == "Save Selected Parts As" && NativeFileDialogs.IsOpen, "Selection export did not use Save As.");
                manager.Save(); tool.SaveSelectedAs();
                Require(dialogs.SaveCalls == 1, "Selection export allowed an overlapping save panel.");
                // The chosen set is frozen before the dialog, even if scene/selection changes while it is pending.
                selection.SetCurrent(new ObjectSelection { gameObject = excluded, individual = true }, true);
                var selectionPath = Path.Combine(directory, "Sélècted Parts.PBB");
                await Task.Run(() => dialogs.SaveComplete(selectionPath));
                Require(await exportTask, "Selection Save As failed.");
                var exported = BuildsManager.ParsePath(selectionPath);
                Require(exported.parts.Length == 4 && exported.chains.Length == 1, "Export saved the whole project or the later selection.");
                Require(manager.buildPath == projectPath && ReferenceEquals(Get<BuildData>(manager, "savedBuildData"), savedProject) && saves == 0 && loads == 0,
                    "Selection export replaced the current path or marked the project saved.");
                Require((bool)Invoke(manager, "HasUnsavedChanges") && File.ReadAllBytes(projectPath).SequenceEqual(projectBytes) && PlayerPrefs.GetString("recentBuilds") == recent,
                    "Selection export wrote the current project or changed its dirty/recent state.");
                selection.SetCurrent(new MultiSelection(null, selected.ToList()) { individual = true }, true);
                var canceled = tool.SaveSelectedAsAsync(); Require(dialogs.SaveCalls == 2, "A second selection export reused its old path.");
                dialogs.SaveComplete(""); Require(!await canceled && !manager.IsFileOperationInProgress, "Canceled export changed state or retained the gate.");
                var failed = tool.SaveSelectedAsAsync(); dialogs.SaveComplete(Path.Combine(directory, "Missing", "Bad.pbb"));
                Require(!await failed && manager.buildPath == projectPath && saves == 0, "Failed export changed the current project.");

                // Finish copying after changing projects, proving the clipboard owns portable data rather than scene references.
                var copy = tool.CopySelectedAsync();
                SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
                Require(await copy && GUIUtility.systemCopyBuffer.StartsWith(BuildTransfer.ClipboardPrefix), "Copy did not survive a project switch.");
                Require(PartsManager.FindLoadedObjects().Count == 0, "Project switch failed to clear source geometry.");
                var clipboard = GUIUtility.systemCopyBuffer;
                var destination = PartsManager.GeneratePart("PLTE-3-3", new Vector3(-30, 0, 0), Quaternion.identity);
                selection.SetCurrent(new ObjectSelection { gameObject = destination, individual = true }, true);
                manager.buildPath = Path.Combine(directory, "Destination.pbb");
                var destinationPose = new Pose(destination.transform.position, destination.transform.rotation);
                Require(await tool.PasteAsync(), "Cross-project paste failed.");
                Require(placement.CurrentSelectionPlacement?.PartCount == 4 && !placement.CurrentSelectionPlacement.RepeatPlacement && PartsManager.FindLoadedObjects().Count == 1,
                    "Paste did not produce a single placeable preview without live temporary parts.");
                var previewGeometry = placement.GetComponent<MeshFilter>().sharedMesh;
                Require(previewGeometry != null && previewGeometry.vertexCount > 0 && previewGeometry.bounds.center.magnitude < 20 && previewGeometry.bounds.extents.magnitude > 2,
                    "Imported preview geometry was not transformed into the captured selection frame.");
                placement.StopPlacing(); await NextFrame();
                Require(PartsManager.FindLoadedObjects().Count == 1 && GUIUtility.systemCopyBuffer == clipboard, "Cancel paste left parts behind or consumed the clipboard.");
                Require(await tool.PasteAsync(), "Repeated paste could not reuse the clipboard.");
                var pose = new Pose(new Vector3(20, 30, 40), Quaternion.Euler(23, 37, 19));
                Invoke(placement, "ApplyPose", pose);
                int beforeState = StateSystem.curIndex;
                Invoke(placement, "Place");
                var pasted = PartsManager.FindLoadedObjects().Where(part => part != destination).ToArray();
                Require(pasted.Length == 4 && !placement.placing && StateSystem.curIndex == beforeState + 1, "Paste did not commit as one edit or stop after one placement.");
                Require(selection.current is MultiSelection selectedPaste && selectedPaste.objs.ToHashSet().SetEquals(pasted), "Placed imported parts did not remain selected.");
                Require(pasted.All(part => !sourceIds.Contains(part.GetComponent<SavedObject>().ConnectionId)), "Pasted parts reused source identities.");
                Require(pasted.Single(p => p.GetComponent<CustomModel>() != null).GetComponentInChildren<CustomMountPoint>() != null, "Paste lost custom mounting points.");
                Require(pasted.Single(p => p.GetComponent<SavedObject>().id.StartsWith("CCHL")).GetComponent<SavedObject>().state == "transfer-state", "Paste lost saved part state.");
                var chains = ChainLoop.SaveAll(); var pastedIds = pasted.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToHashSet();
                Require(chains.Length == 1 && chains[0].members.All(pastedIds.Contains), "Pasted chain joined the wrong parts.");
                Near(destination.transform.position, destinationPose.position, "Paste moved destination geometry.");
                StateSystem.instance.Undo();
                Require(pasted.All(part => !part.activeSelf) && selection.current?.gameObject == destination && ChainLoop.SaveAll().Length == 0, "Undo failed to remove paste and restore selection.");
                StateSystem.instance.Redo();
                Require(pasted.All(part => part.activeSelf) && BuildTransfer.SelectedBodies(selection.current).ToHashSet().SetEquals(pasted) && ChainLoop.SaveAll().Length == 1,
                    "Redo failed to restore pasted geometry, chains and selection.");
                await NextFrame();
                var destinationSnapshot = SceneBuild.ToBuildData();
                var destinationPath = manager.buildPath;
                Write(destinationPath, destinationSnapshot);
                var importTask = tool.ImportBuildAsync();
                Require(dialogs.OpenCalls == 1 && dialogs.OpenTitle == "Import Build", "Import did not open a build-file picker.");
                dialogs.OpenComplete(new[] { selectionPath }); Require(await importTask, "Selection-file import failed.");
                Require(manager.buildPath == destinationPath && PartsManager.FindLoadedObjects().Count == 5 && placement.placing, "Import replaced the current project or created parts before placement.");
                Invoke(placement, "ApplyPose", new Pose(new Vector3(70, 30, 40), Quaternion.identity)); Invoke(placement, "Place");
                Require(PartsManager.FindLoadedObjects().Count == 9 && BuildTransfer.SelectedBodies(selection.current).Count == 4 && ChainLoop.SaveAll().Length == 2,
                    "Import did not add/select the full set and its independent chain.");
                await NextFrame();
                // Old full-project files without selection anchor metadata are equally importable.
                Require(await tool.ImportBuildAsync(destinationPath) && placement.CurrentSelectionPlacement.PartCount == 5 && !placement.CurrentSelectionPlacement.HasHole,
                    "A regular project without selection anchor metadata was not importable.");
                placement.StopPlacing(); await NextFrame();
                var cancelImport = tool.ImportBuildAsync(); dialogs.OpenComplete(Array.Empty<string>());
                Require(!await cancelImport && PartsManager.FindLoadedObjects().Count == 9 && !placement.placing, "Canceled import changed the current scene.");
                string broken = Path.Combine(directory, "Broken.pbb"); File.WriteAllText(broken, "not a build");
                Require(!await tool.ImportBuildAsync(broken) && !manager.IsFileOperationInProgress && !BuildTransferTool.IsBusy, "Failed import retained a busy gate.");
                GUIUtility.systemCopyBuffer = "ordinary text";
                Require(!await tool.PasteAsync() && PartsManager.FindLoadedObjects().Count == 9, "Pasting ordinary text created parts.");
                GUIUtility.systemCopyBuffer = BuildTransfer.ClipboardPrefix + "{broken";
                Require(!await tool.PasteAsync() && !placement.placing, "Malformed copied data changed the project.");
                GUIUtility.systemCopyBuffer = clipboard;

                // Clipboard commands leave focused text inputs to Unity's ordinary copy/paste handling.
                var inputField = Object.FindObjectsByType<InputField>(FindObjectsInactive.Include, FindObjectsSortMode.None).First();
                EventSystem.current.SetSelectedGameObject(inputField.gameObject);
                Require(!await tool.CopySelectedAsync() && !await tool.PasteAsync() && GUIUtility.systemCopyBuffer == clipboard, "World copy/paste intercepted a text field.");
                EventSystem.current.SetSelectedGameObject(null);
                selection.ClearCurrent(false, true, true);
                CheckCommandKeys(tool);
                await Until(() => !BuildTransferTool.IsBusy);

                var finalData = SceneBuild.ToBuildData();
                using var stream = new MemoryStream(); new BinaryFormatter().Serialize(stream, finalData); stream.Position = 0;
                var restored = (BuildData)new BinaryFormatter().Deserialize(stream);
                SceneBuild.GenerateBuild(restored);
                Require(SceneBuild.ToBuildData().CompareData(restored) && ChainLoop.SaveAll().Length == 2 && Object.FindObjectsByType<CustomModel>(FindObjectsSortMode.None).Length == 2,
                    "Transferred parts/chains/custom models did not persist after saving and loading the destination.");
                Require(dialogs.SyncCalls == 0, "Transfer file operations used a synchronous dialog.");
                if (Environment.GetCommandLineArgs().Contains("-buildTransferRenderProbe")) {
                    GameObject.Find("Splash Screen")?.SetActive(false); GameObject.Find("Add Parts Menu")?.SetActive(false);
                    foreach (var prompt in Object.FindObjectsByType<QuickPrompt>(FindObjectsSortMode.None)) prompt.gameObject.SetActive(false);
                    var rects = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    var file = rects.Single(rect => rect.name == "File Menu").gameObject;
                    var edit = rects.Single(rect => rect.name == "Edit Menu").gameObject;
                    file.SetActive(true); await Capture("file"); file.SetActive(false);
                    edit.SetActive(true); await Capture("edit"); edit.SetActive(false);
                    var canvas = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(canvas => canvas.name == "Preferences Canvas");
                    canvas.gameObject.SetActive(true); canvas.enabled = true;
                    rects.Single(rect => rect != null && rect.name == "Preferences").gameObject.SetActive(true);
                    rects.Single(rect => rect != null && rect.name == "Files Toggle").GetComponent<Toggle>().isOn = true;
                    await Capture("preferences"); canvas.enabled = false;
                    EventSystem.current.SetSelectedGameObject(null);
                    GUIUtility.systemCopyBuffer = clipboard;
                    Require(await tool.PasteAsync(), "Render probe could not start paste.");
                    PivotCamera.Main.SetTransform(new Vector3(40, 30, 40), new Vector3(20, 20, 0), -55);
                    // A virtual camera gesture holds the real Placement preview still in a batch GameView.
                    var orbit = Get<InputEvent>(placement.GetComponent<KeyboardTransformTool>(), "orbitInput");
                    var originalAction = orbit.defaultAction; bool rebindEnabled = orbit.rebindAction.action.enabled;
                    var pad = InputSystem.AddDevice<Gamepad>();
                    var held = new InputAction("Render hold", InputActionType.Button, pad.buttonSouth.path); held.Enable();
                    try {
                        orbit.defaultAction = held; orbit.rebindAction.action.Disable();
                        InputState.Change(pad, new GamepadState().WithButton(GamepadButton.South));
                        Invoke(placement, "ApplyPose", new Pose(new Vector3(40, 30, 40), Quaternion.identity));
                        placement.transform.DOComplete();
                        await Capture("preview");
                        var renderer = placement.GetComponent<MeshRenderer>();
                        var mesh = placement.GetComponent<MeshFilter>().sharedMesh;
                        Require(renderer.isVisible && renderer.enabled && !renderer.forceRenderingOff && mesh.GetIndexCount(0) > 0,
                            "The imported set's placement preview is not visible.");
                    }
                    finally { orbit.defaultAction = originalAction; if (rebindEnabled) orbit.rebindAction.action.Enable(); held.Dispose(); InputSystem.RemoveDevice(pad); }
                    placement.StopPlacing();
                }
            }
            finally {
                if (placement.placing) placement.StopPlacing();
                wrapper.SetValue(null, originalWrapper); GUIUtility.systemCopyBuffer = originalClipboard;
                foreach (var pref in prefs) { if (pref.present) PlayerPrefs.SetString(pref.key, pref.value); else PlayerPrefs.DeleteKey(pref.key); }
                Keys(); Directory.Delete(directory, true);
            }
        }

        private static void CheckCommandKeys(BuildTransferTool tool) {
            var copy = Get<InputEvent>(tool, "copyInput");
            var paste = Get<InputEvent>(tool, "pasteInput");
            var chain = Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None).Single(i => i.name == "Simulation Chain Selected Sprockets");
            var events = new[] { copy, paste, chain };
            var enabled = events.Select(e => (e.defaultAction.enabled, e.rebindAction.action.enabled)).ToArray();
            int copied = 0, chained = 0;
            Action onCopy = () => copied++, onChain = () => chained++;
            copy.performed += onCopy; chain.performed += onChain;
            try {
                foreach (var e in events) { e.rebindAction.action.Disable(); e.defaultAction.Enable(); }
                foreach (var modifier in new[] { Key.LeftMeta, Key.RightMeta }) {
                    Keys(); foreach (var e in events) e.Update();
                    Keys(modifier); Require(!copy.IsCurrentlyPressed && !paste.IsCurrentlyPressed, "Command alone triggered clipboard actions.");
                    Keys(modifier, Key.C); Require(copy.IsCurrentlyPressed && !paste.IsCurrentlyPressed, "Command-C did not require the complete chord.");
                    chain.Update(); copy.Update();
                    Require(chained == 0, "Command-C also ran the chain action.");
                    Keys(Key.C); chain.Update(); Require(chained == 0, "Releasing Command fired a held C action.");
                    Keys(); foreach (var e in events) e.Update();
                    Keys(modifier, Key.V); Require(paste.IsCurrentlyPressed && !copy.IsCurrentlyPressed, "Command-V did not require the complete chord.");
                }
                Require(copied == 2, "Both Command keys must support copy.");
                Keys(); foreach (var e in events) e.Update();
                Keys(Key.C); chain.Update(); Require(chained == 1, "Plain C no longer reaches chain creation.");
                var ui = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None).First();
                Require(!string.IsNullOrWhiteSpace(ui.GetBindingDisplayString(copy.defaultAction)), "Composite clipboard bindings are blank in Preferences.");
            }
            finally {
                copy.performed -= onCopy; chain.performed -= onChain; Keys();
                for (int i = 0; i < events.Length; i++) {
                    events[i].Update(); if (!enabled[i].Item1) events[i].defaultAction.Disable(); if (enabled[i].Item2) events[i].rebindAction.action.Enable();
                }
            }
        }
        private static void Keys(params Key[] keys) {
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys));
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null)
                .Invoke(null, new object[] { InputUpdateType.Dynamic });
        }
        private static void Write(string path, BuildData data) { using var file = File.Create(path); new BinaryFormatter().Serialize(file, data); }
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static async Task Capture(string mode) {
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            view.Show(); view.position = new Rect(60, 60, 1280, 840); view.Focus();
            for (int i = 0; i < 5; i++) { view.Repaint(); EditorApplication.QueuePlayerLoopUpdate(); await NextFrame(); }
            string path = "/tmp/protobot-transfer-" + mode + ".png";
            File.Delete(path); ScreenCapture.CaptureScreenshot(path);
            double deadline = EditorApplication.timeSinceStartup + 15;
            while (!File.Exists(path)) {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Transfer UI did not render.");
                view.Repaint(); EditorApplication.QueuePlayerLoopUpdate(); await NextFrame();
            }
        }
        private static async Task Until(Func<bool> condition) { double end = EditorApplication.timeSinceStartup + 10; while (!condition()) { if (EditorApplication.timeSinceStartup > end) throw new TimeoutException(); await Task.Yield(); } }
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        private static object Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args);
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .001f, message);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private sealed class DeferredDialogs : IStandaloneFileBrowser {
            public Action<string[]> OpenComplete; public Action<string> SaveComplete;
            public int OpenCalls, SaveCalls, SyncCalls; public string SaveTitle, OpenTitle;
            public void OpenFilePanelAsync(string title, string directory, ExtensionFilter[] extensions, bool multi, Action<string[]> callback) { OpenCalls++; OpenTitle = title; OpenComplete = callback; }
            public void SaveFilePanelAsync(string title, string directory, string name, ExtensionFilter[] extensions, Action<string> callback) { SaveCalls++; SaveTitle = title; SaveComplete = callback; }
            public string[] OpenFilePanel(string title, string directory, ExtensionFilter[] extensions, bool multi) { SyncCalls++; throw new Exception("Sync panel used."); }
            public string SaveFilePanel(string title, string directory, string name, ExtensionFilter[] extensions) { SyncCalls++; throw new Exception("Sync panel used."); }
            public string[] OpenFolderPanel(string title, string directory, bool multi) => throw new NotSupportedException();
            public void OpenFolderPanelAsync(string title, string directory, bool multi, Action<string[]> callback) => throw new NotSupportedException();
        }
    }
}
