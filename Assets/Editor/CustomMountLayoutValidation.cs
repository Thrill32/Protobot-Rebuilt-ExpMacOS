using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.StateSystems;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CustomMountLayoutValidation {
        private const string KeyName = "Protobot.CustomMountLayoutSmoke";
        private static int frames;
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(KeyName, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod] private static void Resume() {
            if (SessionState.GetBool(KeyName, false)) { frames = 0; EditorApplication.update += Tick; }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(KeyName, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(KeyName, false);
            try {
                await Check();
                Debug.Log("Custom mounting layouts passed: quarter-inch plane grids and axis/plane movement, free first point, snap bypass, face/model isolation, rotated models, modifier multi-selection, rigid group editing, rebound duplicate G/R binding, exact rotation, preview-safe saves/colliders, overlap reuse/conflict rejection, cancellation, grouped undo/redo/delete and binary persistence.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild); await NextFrame();
            var tool = Object.FindFirstObjectByType<CustomModelTool>();
            var camera = Get<Camera>(tool, "refCamera");
            var body = CustomModel.Create(PlaneMesh(), new Vector3(100, 100, 100), Quaternion.Euler(17, 29, 8));
            var model = body.GetComponent<CustomModel>();
            var bodyPose = new Pose(body.transform.position, body.transform.rotation);
            PivotCamera.Main.SetTransform(body.transform.position, Quaternion.LookRotation(-body.transform.forward, body.transform.up).eulerAngles, -8);
            Physics.SyncTransforms(); EventSystem.current.SetSelectedGameObject(null);
            tool.ToggleMounts(); await NextFrame(); Set(tool, "startFrame", -1);
            var editor = tool.MountEditor;
            Vector3 World(float x, float y) => body.transform.TransformPoint(new Vector3(x, y, 0));

            Click(World(.13f, -.18f));
            var first = editor.Selected;
            Near(first.Surface, World(.13f, -.18f), "The first hole was forced onto an arbitrary origin grid.");
            Click(World(.42f, .24f));
            var second = editor.Selected;
            Require(second != first, "Snapped placement did not add a new point.");
            Near(second.Surface, World(.38f, .32f), "A second hole did not follow the quarter-inch model-plane grid.");
            Near(second.transform.right, body.transform.right, "The plane layout ignored the rotated model's axes.");

            Keys(Key.LeftCtrl); Click(World(1.02f, -.34f));
            var free = editor.Selected;
            Near(free.Surface, World(1.02f, -.34f), "Snap Bypass did not allow arbitrary hole spacing.");
            Require(editor.SelectedPoints.Count == 1, "Bypassing new-hole placement unexpectedly extended the selection.");
            Keys();
            int count = model.Snapshot().mounts.Length;
            Click(World(2.9f, 2.99f));
            Require(model.Snapshot().mounts.Length == count, "A near-edge click snapped a new hole beyond the model bounds.");
            Keys(Key.LeftCtrl); Click(World(2.9f, 2.99f)); Keys();
            Require(model.Snapshot().mounts.Length == count + 1, "Snap Bypass could not place an edge hole on the actual surface.");
            Near(editor.Selected.Surface, World(2.9f, 2.99f), "Edge bypass retained the invalid grid point.");
            editor.Select(second); Pointer(second.Surface); var axisStart = second.Surface;
            editor.Begin(false); Set(editor, "axis", Vector3.right);
            Pointer(axisStart + Vector3.right * .31f); editor.Tick(false);
            Near(second.Surface, axisStart + Vector3.right * .25f, "Axis movement did not use quarter-inch mounting-point snapping.");
            editor.Finish(false);
            var pose = CustomMountGrid.PlacementPose(model, World(.9f, .9f), -body.transform.forward, true, out bool onGrid);
            Require(onGrid, "Viewing a coplanar face from the other side discarded its layout.");
            Near(pose.position, World(.88f, .82f), "Opposite normals changed grid spacing/origin.");
            var offPlane = World(.9f, .9f) + body.transform.forward * .2f;
            pose = CustomMountGrid.PlacementPose(model, offPlane, body.transform.forward, true, out onGrid);
            Require(!onGrid, "A parallel face at another depth inherited the wrong plane's grid."); Near(pose.position, offPlane, "A new face was moved to another plane.");
            pose = CustomMountGrid.PlacementPose(model, World(.9f, .9f), Quaternion.AngleAxis(5, body.transform.up) * body.transform.forward, true, out onGrid);
            Require(!onGrid, "A differently oriented surface was forced onto a flat-face grid.");

            var otherBody = CustomModel.Create(PlaneMesh(), body.transform.position + body.transform.right * 10, Quaternion.Euler(21, 4, 37));
            var other = otherBody.GetComponent<CustomModel>();
            var otherFace = otherBody.transform.position + otherBody.transform.up * .23f;
            pose = CustomMountGrid.PlacementPose(other, otherFace, otherBody.transform.forward, true, out onGrid);
            Require(!onGrid, "Another imported model inherited the first model's layout."); Near(pose.position, otherFace, "A model's first point did not remain free.");
            var otherPoint = other.AddMount(pose, .22f, .17f, false); otherPoint.RecordAdded();
            editor.Select(first); editor.Select(otherPoint, true);
            var mixedBefore = SceneBuild.ToBuildData(); var otherSurface = otherPoint.Surface;
            editor.Begin(false); editor.Apply(otherSurface + otherBody.transform.right * .5f, otherPoint.transform.rotation);
            Require(SceneBuild.ToBuildData().CompareData(mixedBefore), "A group preview leaked into one of its models' saves.");
            editor.Finish(false); Near(otherPoint.Surface, otherSurface, "Cancel did not restore points on the second model.");
            otherBody.SetActive(false); Object.Destroy(otherBody); await NextFrame();

            editor.Select(null); Click(first.Surface); Keys(Key.LeftCtrl); Click(second.Surface);
            Require(editor.SelectedPoints.Count == 2 && editor.SelectedPoints.Contains(first) && editor.SelectedPoints.Contains(second), "The Select Multiple modifier did not add a mounting ring.");
            Click(second.Surface); Require(editor.SelectedPoints.Count == 1 && editor.Selected == first, "Modifier-click did not toggle an existing selection member.");
            Click(second.Surface); Require(editor.SelectedPoints.Count == 2, "Could not restore multi-selection."); Keys();
            var originals = editor.SelectedPoints.ToArray();
            var originalSurfaces = originals.Select(p => p.Surface).ToArray();
            var originalData = originals.Select(p => p.Snapshot()).ToArray();
            var inputs = Object.FindObjectsByType<Protobot.InputEvents.InputEvent>(FindObjectsSortMode.None);
            var grab = inputs.Single(i => i.name == "Grab Selection");
            var duplicate = inputs.Single(i => i.name == "Duplicate");
            var delete = inputs.Single(i => i.name == "Delete Selection");

            var screw = PartsManager.GeneratePart("SCRW-1.00in", first.Surface, Quaternion.LookRotation(first.transform.forward)).GetComponent<Screw>();
            Physics.SyncTransforms(); screw.holeDetector.RefreshOverlaps();
            Require(screw.holeDetector.holes.Contains(first.GetComponent<HoleCollider>()), "The source mounting point did not attach to its test screw.");
            var beforeCopy = SceneBuild.ToBuildData(); int state = StateSystem.curIndex;
            Pointer(second.Surface); grab.Update(); Keys(Key.LeftAlt, Key.G); grab.Update();
            Require(editor.IsEditing && !KeyboardTransformTool.IsCapturingInput && editor.SelectedPoints.All(p => p.IsPreview), "Duplicate+G did not start point-only copies.");
            var copies = editor.SelectedPoints.ToArray();
            Require(copies.Length == 2 && copies.All(p => p.Owner == model) && originals.All(p => !copies.Contains(p)), "Multi-duplicate moved originals or cloned the whole model.");
            Require(copies.All(p => !p.GetComponent<Collider>().enabled && !p.GetComponent<HoleCollider>().enabled), "Temporary copies exposed attachment colliders.");
            screw.holeDetector.RefreshOverlaps();
            Require(copies.All(p => !screw.holeDetector.holes.Contains(p.GetComponent<HoleCollider>())), "Temporary copies stole fastener occupancy.");
            Require(SceneBuild.ToBuildData().CompareData(beforeCopy), "Uncommitted copies leaked into a save.");
            editor.Finish(true);
            Require(editor.IsEditing && StateSystem.curIndex == state, "Overlapping unmoved copies were committed as duplicate holes.");
            Keys(); Pointer(originalSurfaces[1] + body.transform.right * .31f + body.transform.up * .2f); editor.Tick(false);
            for (int i = 0; i < copies.Length; i++) Near(copies[i].Surface, originalSurfaces[i] + body.transform.right * .25f + body.transform.up * .25f, "A copied group lost quarter-inch movement snapping.");
            Keys(Key.LeftCtrl); Pointer(originalSurfaces[1] + body.transform.right * .31f + body.transform.up * .27f); editor.Tick(false);
            var copyOffset = body.transform.right * .31f + body.transform.up * .27f;
            for (int i = 0; i < copies.Length; i++) {
                Near(copies[i].Surface, originalSurfaces[i] + copyOffset, "Bypass movement distorted the copied pattern.");
                Near(originals[i].Surface, originalSurfaces[i], "Duplicating moved an original hole.");
                var data = copies[i].Snapshot();
                Require(data.diameter == originalData[i].diameter && data.depth == originalData[i].depth && data.threaded == originalData[i].threaded, "Copying lost hole settings.");
            }
            Set(editor, "startFrame", -1); Keys(Key.LeftCtrl, Key.Enter); editor.Tick(false); Keys();
            Require(!editor.IsEditing && copies.All(p => !p.IsPreview && p.GetComponent<Collider>().enabled) && StateSystem.curIndex == state + 1,
                "Committing copied points was not one undoable edit.");
            var copied = SceneBuild.ToBuildData(); Require(!copied.CompareData(beforeCopy), "Committed copies did not mark the build dirty.");
            StateSystem.instance.Undo(); Require(copies.All(p => !p.gameObject.activeSelf) && SceneBuild.ToBuildData().CompareData(beforeCopy), "One undo did not remove the complete copied pattern.");
            StateSystem.instance.Redo(); Require(copies.All(p => p.gameObject.activeSelf) && SceneBuild.ToBuildData().CompareData(copied), "Redo did not restore the complete copied pattern.");

            editor.Select(first); editor.Select(second, true); Pointer(second.Surface);
            Keys(); grab.Update(); Keys(Key.LeftAlt, Key.G); grab.Update();
            var canceled = editor.SelectedPoints.ToArray(); state = StateSystem.curIndex;
            editor.Apply(second.Surface + body.transform.up, second.transform.rotation);
            Keys(Key.Escape); Require(EscapeDeselect.TryHandleEscape(), "Escape did not cancel point duplication."); Keys();
            Require(!editor.IsEditing && editor.SelectedPoints.SequenceEqual(originals) && canceled.All(p => !p.gameObject.activeSelf) && StateSystem.curIndex == state,
                "Cancel left extra holes, changed selection or added undo history.");
            Require(SceneBuild.ToBuildData().CompareData(copied), "Cancel changed saved mounting points."); await NextFrame();

            var groupBefore = editor.SelectedPoints.Select(p => p.LocalPose).ToArray();
            var pivot = (first.Surface + second.Surface) / 2; var groupRotation = second.transform.rotation;
            editor.Begin(true); var rotation = Quaternion.AngleAxis(90, body.transform.forward);
            editor.Apply(pivot, rotation * groupRotation); editor.Finish(true);
            Near(first.Surface, pivot + rotation * (originalSurfaces[0] - pivot), "Group rotation did not retain the pattern around its center.");
            StateSystem.instance.Undo();
            for (int i = 0; i < originals.Length; i++) Near(originals[i].LocalPose.position, groupBefore[i].position, "Group rotation undo missed a member.");

            // The configured duplication binding must be polled, including when
            // its InputEvent has not updated before the grab/rotation input.
            var previousAction = duplicate.defaultAction; bool wasEnabled = previousAction.enabled;
            previousAction.Disable();
            var rebound = new InputAction("Duplicate test", InputActionType.Button, "<Keyboard>/b");
            duplicate.defaultAction = rebound; rebound.Enable();
            try {
                Keys(Key.LeftAlt); editor.Begin(false);
                Require(editor.SelectedPoints.SequenceEqual(originals) && editor.SelectedPoints.All(p => !p.IsPreview), "Point duplication ignored the rebound modifier."); editor.Finish(false);
                Keys(Key.B); editor.Begin(true);
                Require(editor.SelectedPoints.Count == 2 && editor.SelectedPoints.All(p => p.IsPreview), "The rebound duplication hold did not work with rotation.");
                var rotatedCopies = editor.SelectedPoints.ToArray();
                Set(editor, "axis", Vector3.forward);
                var numeric = Get<TransformNumericInput>(editor, "numeric"); numeric.Cycle(false);
                foreach (char c in "90") InputSystem.QueueTextEvent(Keyboard.current, c); UpdateInput();
                Invoke(editor, "SubmitNumeric");
                Require(!editor.IsEditing && rotatedCopies.All(p => !p.IsPreview), "Rotating a copied pattern could not be committed.");
                var exactRotation = Quaternion.AngleAxis(90, Vector3.forward);
                for (int i = 0; i < rotatedCopies.Length; i++)
                    Near(rotatedCopies[i].Surface, pivot + exactRotation * (originalSurfaces[i] - pivot), "Rotated copies lost their pattern.");
            }
            finally { Keys(); duplicate.defaultAction = previousAction; rebound.Dispose(); if (wasEnabled) previousAction.Enable(); }

            editor.Select(first); editor.Select(second, true);
            var beforeExtend = SceneBuild.ToBuildData(); state = StateSystem.curIndex;
            Keys(Key.LeftAlt); editor.Begin(false); Keys();
            editor.Apply(second.Surface + second.Surface - first.Surface, second.transform.rotation); editor.Finish(true);
            Require(!editor.IsEditing && editor.SelectedPoints.Count == 1 && StateSystem.curIndex == state + 1,
                "Extending an adjacent pattern did not reuse its matching existing hole.");
            Near(editor.Selected.Surface, originalSurfaces[1] * 2 - originalSurfaces[0], "Pattern extension placed its new hole incorrectly.");
            StateSystem.instance.Undo(); Require(SceneBuild.ToBuildData().CompareData(beforeExtend), "Undo of a partly overlapping copy changed existing holes.");
            StateSystem.instance.Redo();

            var different = model.AddMount(new Pose(World(-1.2f, -1.5f), first.transform.rotation), .3f, .25f, false); different.RecordAdded();
            editor.Select(first); editor.Select(second, true); state = StateSystem.curIndex;
            Keys(Key.LeftAlt); editor.Begin(false); Keys();
            editor.Apply(second.Surface + different.Surface - first.Surface, second.transform.rotation); editor.Finish(true);
            Require(editor.IsEditing && StateSystem.curIndex == state && !different.Threaded && different.Diameter == .3f,
                "A conflicting existing hole's settings were silently overwritten.");
            editor.Finish(false); await NextFrame();

            editor.Select(copies[0]); editor.Select(copies[1], true); var beforeDelete = SceneBuild.ToBuildData();
            Keys(); delete.Update(); Keys(Key.Delete); delete.Update(); Keys();
            Require(copies.All(p => !p.gameObject.activeSelf) && originals.All(p => p.gameObject.activeSelf) && body.activeSelf,
                "Deleting selected holes changed the model or unselected holes.");
            StateSystem.instance.Undo(); Require(copies.All(p => p.gameObject.activeSelf) && SceneBuild.ToBuildData().CompareData(beforeDelete), "Group Delete required more than one undo.");
            Near(body.transform.position, bodyPose.position, "Point edits moved the imported model."); Near(body.transform.rotation, bodyPose.rotation, "Point edits rotated the imported model.");
            tool.ToggleMounts();

            var saved = SceneBuild.ToBuildData(); using var memory = new MemoryStream(); var formatter = new BinaryFormatter();
            formatter.Serialize(memory, saved); memory.Position = 0;
            SceneBuild.GenerateBuild((BuildData)formatter.Deserialize(memory));
            Require(saved.CompareData(SceneBuild.ToBuildData()), "Copied point layouts changed through binary save/load.");
            var loaded = Object.FindFirstObjectByType<CustomModel>();
            var loadedFirst = loaded.GetComponentsInChildren<CustomMountPoint>().First();
            var loadedRotation = loaded.transform.rotation;
            loaded.transform.SetPositionAndRotation(new Vector3(-70, 25, 30), Quaternion.Euler(43, 13, 71) * loadedRotation);
            pose = CustomMountGrid.PlacementPose(loaded, loadedFirst.Surface + loadedFirst.transform.right * .76f + loadedFirst.transform.up * .2f,
                loadedFirst.transform.forward, true, out onGrid);
            Require(onGrid, "Moving/reloading a model discarded its plane layout."); Near(pose.position, loadedFirst.Surface + loadedFirst.transform.right * .75f + loadedFirst.transform.up * .25f, "A moved model's layout stayed in world coordinates.");

            void Pointer(Vector3 world) => InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(world));
            void Click(Vector3 world) {
                var position = (Vector2)camera.WorldToScreenPoint(world);
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); UpdateInput();
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position, buttons = 1 }); UpdateInput();
                Invoke(tool, "UpdateMountPlacement");
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); UpdateInput();
            }
        }
        private static CustomModelData PlaneMesh() => new() {
            name = "Mount grid plate", vertices = new float[] { -3,-3,0, 3,-3,0, 3,3,0, -3,3,0 }, triangles = new[] { 0,1,2, 0,2,3 }
        };
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static void Keys(params Key[] keys) { InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys)); UpdateInput(); }
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static object Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args);
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .001f, message + $" ({a:F6} vs {b:F6})");
        private static void Near(Quaternion a, Quaternion b, string message) => Require(Quaternion.Angle(a, b) < .02f, message);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
