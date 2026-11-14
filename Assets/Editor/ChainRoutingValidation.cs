using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DG.Tweening;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.StateSystems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using InputEvent = Protobot.InputEvents.InputEvent;

namespace ProtobotEditorTools {
    public static class ChainRoutingValidation {
        private const string SessionKey = "Protobot.ChainRoutingSmoke";
        private static int frames;
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SessionKey, true); EditorApplication.EnterPlaymode();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(SessionKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        [InitializeOnLoadMethod] private static void Resume() {
            if (SessionState.GetBool(SessionKey, false)) { frames = 0; EditorApplication.update += Tick; }
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SessionKey, false);
            try {
                await Check();
                Debug.Log("Chain routing smoke passed: Shift+C and route buttons, preview/save isolation, invalid route guards, cancellation, signed simulation, stable identity, route undo/redo, P/clipboard copies, deletion recovery, binary/JSON load and malformed-route rejection.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Press(InputEvent input, params Key[] keys) {
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); UpdateInput(); input.Update();
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys)); UpdateInput(); input.Update();
        }
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        private static void Complete() { foreach (var saved in Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None)) saved.transform.DOComplete(); }
        private static async Task Check() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            GameObject.Find("Splash Screen")?.SetActive(false);
            bool renderProbe = Environment.GetCommandLineArgs().Contains("-chainRoutingRenderProbe");
            EditorWindow game = null;
            if (renderProbe) {
                game = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); game.Show(); game.Focus();
                await Task.Delay(100);
            }
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            var manager = Get<SelectionManager>(tool, "selectionManager");
            var parts = ChainContourValidation.AdvancedFixture(); var route = ChainContourValidation.Route(parts);
            manager.SetCurrent(new MultiSelection(null, parts.Select(p => p.gameObject).Reverse().ToList()) { individual = true }, true, true);
            var input = Get<InputEvent>(tool, "chainInput");
            Press(input, Key.LeftShift, Key.C);
            Require(tool.RouteEditor != null && SimulationTool.CapturesSelection, "Shift+C did not open the route editor");
            Require(ChainLoop.SaveAll().Length == 0, "An unconfirmed route entered the build");
            var editor = tool.RouteEditor;
            Require(!editor.IsValid, "An all-outer route through the inner idler was marked valid");
            int idler = Array.IndexOf(editor.Data.members, route.members[1]);
            editor.ToggleWrap(idler); Require(editor.IsValid, "Choosing the inner wrap did not produce a valid route");
            editor.Reverse(); Require(editor.IsValid, "Reversing the route in the editor changed validity");
            Require(ChainLoop.SaveAll().Length == 0 && SceneBuild.ToBuildData().chains.Length == 0, "Route preview leaked into persistence");
            if (renderProbe) {
                PivotCamera.Main.SetTransform(new Vector3(0, 1.5f, 0), new Vector3(0, 0, 0), -12);
                PivotCamera.Main.transform.DOComplete(); PivotCamera.Main.camera.transform.DOComplete();
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); UpdateInput(); input.Update();
                Canvas.ForceUpdateCanvases();
                string path = "/tmp/protobot-chain-routing-editor.png"; File.Delete(path);
                for (int i = 0; i < 30; i++) { game.Repaint(); EditorApplication.QueuePlayerLoopUpdate(); await Task.Delay(20); }
                Require(tool.RouteEditor != null, "Route editor lost focus during the render probe");
                ScreenCapture.CaptureScreenshot(path);
                for (int i = 0; i < 150 && !File.Exists(path); i++) { game.Repaint(); EditorApplication.QueuePlayerLoopUpdate(); await Task.Delay(20); }
                Require(File.Exists(path), "Route editor screenshot was not captured");
            }
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.Escape)); UpdateInput();
            EscapeDeselect.TryHandleEscape();
            Require(tool.RouteEditor == null && ChainLoop.SaveAll().Length == 0 && manager.current != null, "Escape retained a route/preview or cleared selection");
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); UpdateInput(); input.Update();
            await Task.Delay(50);
            tool.EditChainRoute(); editor = tool.RouteEditor;
            Require(editor != null, "Route editor did not reopen");
            idler = Array.IndexOf(editor.Data.members, route.members[1]); editor.ToggleWrap(idler);
            var apply = Object.FindObjectsByType<Button>(FindObjectsSortMode.None).Single(b => b.name == "Apply" && b.transform.root.name == "Chain Routing");
            Require(apply.interactable, "Apply is disabled for a valid route"); apply.onClick.Invoke();
            var loop = Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).Single(); string identity = loop.ConnectionId;
            Require(loop.Data.wraps.Contains(1) && tool.RouteEditor == null, "Apply did not save and close the route editor");
            StateSystem.instance.Undo(); Complete(); Require(!loop.gameObject.activeSelf, "Undo did not remove the new route");
            StateSystem.instance.Redo(); Complete(); Require(loop.gameObject.activeSelf && loop.ConnectionId == identity, "Redo changed route identity");
            // Replacing a legacy route is one undo step and retains the entity ID.
            // A Shift-containing rebound Chain chord must keep its existing quick-chain behavior.
            await Task.Delay(50);
            input.defaultAction.ApplyBindingOverride(0, "<Keyboard>/leftShift");
            manager.SetCurrent(new MultiSelection(null, new List<GameObject> { parts[0].gameObject, parts[2].gameObject }) { individual = true }, true, true);
            Press(input, Key.LeftShift);
            var pair = ChainLoop.Find(new[] { route.members[0], route.members[2] });
            Require(pair != null && tool.RouteEditor == null, "A rebound Chain action containing Shift lost quick-chain behavior");
            input.defaultAction.RemoveBindingOverride(0);
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); UpdateInput(); input.Update();
            string pairId = pair.ConnectionId;
            var explicitPair = pair.Data; explicitPair.routeVersion = 1; explicitPair.wraps = new[] { 0, 0 };
            Require(ChainLoop.SetRoute(explicitPair, out _), "Could not edit an existing legacy route");
            Require(pair.ConnectionId == pairId && pair.Data.routeVersion == 1, "Editing a route replaced its identity");
            StateSystem.instance.Undo(); Complete(); Require(pair.Data.routeVersion == 0, "Undo did not restore legacy routing");
            StateSystem.instance.Redo(); Complete(); Require(pair.Data.routeVersion == 1 && pair.ConnectionId == pairId, "Redo lost route/identity");
            pair.gameObject.SetActive(false);
            loop.RefreshVisual(); var line = loop.GetComponent<LineRenderer>();
            var model = MechanismSimulation.Create(parts[0].gameObject); model.Evaluate(1); loop.RefreshVisual();
            Require(!model.HasConflict && line.sharedMaterial.mainTextureOffset.x != 0, "Inner chain did not animate"); model.Restore();
            var generator = PartsManager.GetPartType("SPKT").GetComponent<PartGenerator>(); PartParameterEditing.Load(generator, "SPKT-Normal-15T");
            var replacement = PartParameterEditing.Replace(parts[1].gameObject, generator);
            parts[1].gameObject.SetActive(false);
            Require(loop.ResolveParts().Contains(replacement.GetComponent<SimulationPart>()) && loop.TryContour(out _, out _), "Regeneration broke an inner endpoint or its route");
            replacement.SetActive(false); Object.Destroy(replacement); parts[1].gameObject.SetActive(true);
            Require(loop.TryContour(out _, out _), "Restoring the original inner endpoint broke routing");
            var snapshot = SceneBuild.ToBuildData(); Require(snapshot.chains.Length == 1, "Snapshot omitted the explicit chain");
            snapshot.placementAnchor = PlacementAnchorData.From(new Pose(Vector3.zero, Quaternion.identity), false);
            var json = BuildTransfer.FromClipboard(BuildTransfer.ToClipboard(snapshot)); BuildTransfer.ValidateChains(json);
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
            var directCopies = duplicator.CreateCopies(parts.Select(p => p.gameObject));
            var directChain = Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).Single(c => c.ConnectionId != identity);
            Require(directChain.Data.wraps.SequenceEqual(loop.Data.wraps) && directChain.ConnectionId != identity, "Alt/axis copies lost explicit route metadata or identity");
            ObjectDuplicator.DiscardCopies(directCopies);
            Require(Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).Single() == loop, "Discarding copies leaked a chain or removed the original");
            var preview = new GameObject("Route copy preview");
            using (var placement = SelectionPlacementData.FromBuild(json, preview.transform, duplicator, manager, true)) {
                var copies = placement.Generate(new Vector3(20, 0, 0), Quaternion.Euler(25, 35, 15));
                var copied = placement.GeneratedChains.Single().GetComponent<ChainLoop>();
                Require(copied.ConnectionId != identity && copied.Data.routeVersion == 1 && copied.Data.wraps.SequenceEqual(snapshot.chains.Single().wraps) &&
                    copied.TryContour(out _, out _), "Clipboard/P copy lost its route or retained chain identity");
                Require(!copied.MemberIds.Intersect(route.members).Any(), "Copied chain still refers to source sprockets");
                ObjectDuplicator.DiscardCopies(copies);
                foreach (var chain in placement.GeneratedChains) { chain.SetActive(false); Object.Destroy(chain); }
            }
            Object.Destroy(preview);
            parts[1].gameObject.SetActive(false); loop.RefreshVisual();
            Require(!line.enabled && ChainLoop.SaveAll().Length == 0, "Deleting an inner sprocket left a dangling chain");
            parts[1].gameObject.SetActive(true); loop.RefreshVisual(); Require(line.enabled, "Restoring an inner sprocket did not restore the route");
            manager.ClearCurrent(false, true, true);
            using var stream = new MemoryStream(); var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
            formatter.Serialize(stream, snapshot); stream.Position = 0; var restored = (BuildData)formatter.Deserialize(stream);
            SceneBuild.GenerateBuild(restored);
            var loaded = Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).Single();
            Require(loaded.ConnectionId == identity && loaded.TryContour(out _, out _), "Binary load lost explicit routing or stable identity");
            SceneBuild.GenerateBuild(JsonUtility.FromJson<BuildData>(JsonUtility.ToJson(snapshot)));
            loaded = Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).Single();
            Require(loaded.ConnectionId == identity && loaded.TryContour(out _, out _), "JSON load lost explicit routing or identity");
            var bad = JsonUtility.FromJson<BuildData>(JsonUtility.ToJson(snapshot)); bad.chains[0].wraps[0] = 12;
            bool rejected = false; try { BuildTransfer.ValidateChains(bad); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Malformed route was accepted for transfer");
            rejected = false; try { SceneBuild.GenerateBuild(bad); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && ChainLoop.Find(snapshot.chains[0].members)?.ConnectionId == identity, "Malformed file load replaced the current chain");
            bad = JsonUtility.FromJson<BuildData>(JsonUtility.ToJson(snapshot)); bad.chains[0].wraps = new int[4];
            rejected = false; try { SceneBuild.GenerateBuild(bad); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && ChainLoop.Find(snapshot.chains[0].members)?.ConnectionId == identity, "Physically invalid file route replaced the current build");
        }
    }
}
