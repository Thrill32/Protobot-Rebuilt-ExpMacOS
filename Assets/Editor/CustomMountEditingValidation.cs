using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CustomMountEditingValidation {
        private const string SmokeKey = "Protobot.CustomMountEditingSmoke";
        private static int frames;
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(SmokeKey, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Custom mounting-point editing passed: M-mode ring/empty-center selection, point-only G movement over pre-made holes, surface-plane/axis movement, rotation, exact input, save-safe preview, cancellation, keyboard undo/redo with and without selected points, history guards during previews/text entry, fastener refresh, independent clones, deletion, occlusion and binary save/load.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            var tool = Object.FindFirstObjectByType<CustomModelTool>();
            var selection = Get<SelectionManager>(tool, "selectionManager");
            var camera = Get<Camera>(tool, "refCamera");
            var body = CustomModel.Create(FrameMesh(), new Vector3(100, 100, 100), Quaternion.Euler(17, 29, 8));
            var model = body.GetComponent<CustomModel>();
            var bodyPose = new Pose(body.transform.position, body.transform.rotation);
            var point = model.AddMount(body.transform.TransformPoint(new Vector3(-.8f, 0, 0)), body.transform.forward, .2f, .1f, true);
            point.RecordAdded(); var original = point.LocalPose;
            Require(point.GetComponentsInChildren<MeshRenderer>().All(r => r.forceRenderingOff), "New mounting rings were visible outside M mode.");
            Require(point.GetComponent<Collider>().enabled && point.GetComponent<HoleCollider>().isActiveAndEnabled &&
                !body.GetComponent<MeshRenderer>().forceRenderingOff, "Hiding rings disabled a hole or its model.");
            PivotCamera.Main.SetTransform(body.transform.position, Quaternion.LookRotation(-body.transform.forward, body.transform.up).eulerAngles, -8);
            Physics.SyncTransforms();
            var centerRay = new Ray(body.transform.position + body.transform.forward * 3, -body.transform.forward);
            Require(!Physics.Raycast(centerRay, 6, LayerMask.GetMask("Default")), "Fixture did not have a real empty hole.");
            EventSystem.current.SetSelectedGameObject(null);
            tool.ToggleMounts(); await NextFrame();
            Require(point.GetComponentsInChildren<MeshRenderer>().All(r => !r.forceRenderingOff), "Entering M did not reveal existing mounting rings.");
            Set(tool, "startFrame", -1);
            var editor = tool.MountEditor;
            var inputs = Object.FindObjectsByType<Protobot.InputEvents.InputEvent>(FindObjectsSortMode.None);
            var undo = inputs.Single(i => i.name == "Undo");
            var redo = inputs.Single(i => i.name == "Redo");
            History(undo, Key.Z);
            Require(!point.gameObject.activeSelf && CustomModelTool.IsEditingMounts, "Undo shortcut did not remove a new point while staying in M mode with no selection.");
            History(redo, Key.Y);
            Require(point.gameObject.activeSelf && CustomModelTool.IsEditingMounts, "Redo shortcut did not restore a new point in M mode.");
            Click(point.Surface);
            Require(editor.Selected == point && model.GetComponentsInChildren<CustomMountPoint>().Length == 1,
                "Clicking an existing ring did not select it or created another point.");
            int state = StateSystem.curIndex; var before = SceneBuild.ToBuildData();
            var grab = Object.FindObjectsByType<Protobot.InputEvents.InputEvent>(FindObjectsSortMode.None).Single(i => i.name == "Grab Selection");
            Keys(); grab.Update(); Keys(Key.G); grab.Update();
            Require(editor.IsEditing && !Protobot.Tools.KeyboardTransformTool.IsCapturingInput, "G did not start point-only editing.");
            Keys(Key.LeftCtrl); grab.Update();
            Pointer(body.transform.position);
            editor.Tick(false);
            Near(point.Surface, body.transform.position, "Planar G movement could not reach the empty hole center.");
            Near(body.transform.position, bodyPose.position, "Moving a point moved its model.");
            Near(body.transform.rotation, bodyPose.rotation, "Moving a point rotated its model.");
            Require(SceneBuild.ToBuildData().CompareData(before), "A temporary point preview leaked into saved geometry.");
            Set(editor, "startFrame", -1); Keys(Key.LeftCtrl, Key.Enter); editor.Tick(false); Keys();
            Require(!editor.IsEditing, "Enter did not commit mounting-point movement.");
            Require(StateSystem.curIndex == state + 1 && !SceneBuild.ToBuildData().CompareData(before), "Point movement was not one undoable saved edit.");
            Near(point.Snapshot().x, 0, "Moved local X did not save.");
            Require(point.transform.parent == body.transform && point.gameObject.layer == HoleCollider.HOLE_COLLISIONS_LAYER, "Point movement broke ownership/layer.");
            Require(CustomMountPoint.Pick(centerRay) == point, "A point over a real hole is not pickable at its empty center.");
            editor.Select(null); Click(body.transform.position);
            Require(editor.Selected == point && model.GetComponentsInChildren<CustomMountPoint>().Length == 1, "M-mode center picking over the void failed.");
            var moved = point.LocalPose;
            History(undo, Key.Z); Near(point.LocalPose.position, original.position, "Undo shortcut did not restore local point position.");
            History(redo, Key.Y); Near(point.LocalPose.position, moved.position, "Redo shortcut did not restore point position.");

            var screwGen = PartsManager.GetPartType("SCRW").GetComponent<PartGenerator>(); PartParameterEditing.Load(screwGen, "SCRW-1.00in");
            var screwBody = screwGen.Generate(point.Surface, Quaternion.LookRotation(point.transform.forward));
            var screw = screwBody.GetComponent<Screw>(); Physics.SyncTransforms(); screw.holeDetector.RefreshOverlaps();
            Require(screw.holeDetector.holes.Contains(point.GetComponent<HoleCollider>()), "Moved attachment could not receive a screw.");
            editor.Begin(false); editor.Apply(point.Surface + point.transform.right, point.transform.rotation); editor.Finish(true);
            Require(!screw.holeDetector.holes.Contains(point.GetComponent<HoleCollider>()), "Moving an attachment left stale fastener occupancy.");
            History(undo, Key.Z); Require(screw.holeDetector.holes.Contains(point.GetComponent<HoleCollider>()), "Undo shortcut did not reconnect the restored attachment.");
            screwBody.SetActive(false); Object.Destroy(screwBody);

            state = StateSystem.curIndex; var canceled = point.LocalPose;
            editor.Begin(false); editor.Apply(point.Surface + point.transform.up * .3f, point.transform.rotation);
            History(undo, Key.Z); History(redo, Key.Y);
            Require(StateSystem.curIndex == state && editor.IsEditing, "History shortcuts changed committed history during a pending point preview.");
            Keys(Key.Escape); Require(EscapeDeselect.TryHandleEscape(), "Escape did not cancel the point edit."); Keys();
            Near(point.LocalPose.position, canceled.position, "Cancel did not restore local pose.");
            Require(StateSystem.curIndex == state, "Cancel added undo history.");
            await NextFrame(); // Escape owns its entire frame; later shortcuts need a new one.

            Pointer(point.Surface); editor.Begin(false); Keys(Key.LeftCtrl);
            var anchor = point.Surface; var mountRotation = point.transform.rotation;
            var planeStart = Get<Vector3>(editor, "planeStart");
            var edge = new Vector2(camera.pixelRect.xMax - 1, camera.pixelRect.center.y);
            var offset = (Vector3)Invoke(editor, "PlanePoint", edge) - planeStart;
            var expected = anchor + mountRotation * Vector3.right * Vector3.Dot(offset, mountRotation * Vector3.right)
                + mountRotation * Vector3.up * Vector3.Dot(offset, mountRotation * Vector3.up);
            Set(editor, "axis", Vector3.right); InputState.Change(Mouse.current.position, edge); editor.Tick(false);
            Require(Vector2.Distance(Mouse.current.position.ReadValue(), edge) > 10, "Axis point movement did not wrap the cursor.");
            Set(editor, "axis", Vector3.zero); editor.Tick(false);
            Near(point.Surface, expected, "Returning to plane movement after a cursor wrap lost its original displacement.");
            var pausedPose = point.LocalPose; editor.Tick(true); Pointer(anchor); editor.Tick(false);
            Near(point.LocalPose.position, pausedPose.position, "Resuming point movement after camera/UI pause jumped.");
            editor.Finish(false); Keys();

            var beforeRotation = SceneBuild.ToBuildData();
            editor.Begin(true);
            var surface = point.Surface;
            var rotated = Quaternion.AngleAxis(37, body.transform.up) * point.transform.rotation;
            editor.Apply(surface, rotated); editor.Finish(true);
            Near(point.Surface, surface, "Rotation moved the selected mounting face.");
            Require(!SceneBuild.ToBuildData().CompareData(beforeRotation), "Changing only a mounting-point rotation did not mark the build dirty.");
            var rotation = point.LocalPose.rotation;
            History(undo, Key.Z); Near(point.LocalPose.rotation, canceled.rotation, "Undo rotation shortcut failed.");
            History(redo, Key.Y); Near(point.LocalPose.rotation, rotation, "Redo rotation shortcut failed.");

            editor.Begin(false); Set(editor, "axis", Vector3.right);
            var numeric = Get<Protobot.Tools.TransformNumericInput>(editor, "numeric"); numeric.Cycle(false);
            state = StateSystem.curIndex;
            History(undo, Key.Z); History(redo, Key.Y);
            Require(editor.IsTyping && StateSystem.curIndex == state, "History shortcuts escaped numeric entry or changed history while typing.");
            foreach (char c in ".125") InputSystem.QueueTextEvent(Keyboard.current, c); UpdateInput();
            var previousSurface = point.Surface;
            Invoke(editor, "SubmitNumeric"); Near(point.Surface, previousSurface + Vector3.right * .125f, "Exact point offset failed.");

            var savedPose = point.LocalPose;
            tool.ToggleMounts();
            Require(point.GetComponentsInChildren<MeshRenderer>().All(r => r.forceRenderingOff), "Leaving M did not hide mounting rings.");
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>(); var clone = duplicator.CreateCopies(new[] { body }).Single();
            var clonedPoint = clone.GetComponentInChildren<CustomMountPoint>();
            Require(clonedPoint.GetComponentsInChildren<MeshRenderer>().All(r => r.forceRenderingOff), "Cloning outside M revealed mounting rings.");
            Require(clonedPoint.Owner.gameObject == clone, "Cloned point retained its source owner.");
            Near(clonedPoint.LocalPose.position, savedPose.position, "Cloning lost the edited position.");
            clonedPoint.ApplyLocalPose(new Pose(savedPose.position + Vector3.right, savedPose.rotation));
            Near(point.LocalPose.position, savedPose.position, "Editing a cloned point changed its source.");
            ObjectDuplicator.DiscardCopies(new[] { clone }.ToList());
            await NextFrame();

            // A foreground body must still obscure the analytic mounting disk.
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.transform.position = point.Surface + point.transform.forward; block.transform.localScale = Vector3.one * .3f;
            Physics.SyncTransforms();
            Require(CustomMountPoint.Pick(new Ray(point.Surface + point.transform.forward * 3, -point.transform.forward)) == null, "Point picker selected through foreground geometry.");
            Object.DestroyImmediate(block);

            var data = SceneBuild.ToBuildData();
            using var memory = new MemoryStream(); new BinaryFormatter().Serialize(memory, data); memory.Position = 0;
            SceneBuild.GenerateBuild((BuildData)new BinaryFormatter().Deserialize(memory));
            var loaded = Object.FindFirstObjectByType<CustomMountPoint>();
            Require(loaded.GetComponentsInChildren<MeshRenderer>().All(r => r.forceRenderingOff), "Loading outside M revealed mounting rings.");
            Near(loaded.LocalPose.position, savedPose.position, "Build load lost the edited local position.");
            Near(loaded.LocalPose.rotation, savedPose.rotation, "Build load lost the edited orientation.");
            Require(loaded.Owner != null && loaded.GetComponent<HoleCollider>().holeData.part == loaded.Owner.gameObject, "Reload broke mounting-point ownership.");
            Require(SceneBuild.ToBuildData().CompareData(data), "Reloaded point edits made an unchanged build look dirty.");

            tool.ToggleMounts(); await NextFrame(); tool.MountEditor.Select(loaded);
            var delete = Object.FindObjectsByType<Protobot.InputEvents.InputEvent>(FindObjectsSortMode.None).Single(i => i.name == "Delete Selection");
            Keys(); delete.Update(); Keys(Key.Delete); delete.Update();
            Require(!loaded.gameObject.activeSelf && loaded.Owner.gameObject.activeSelf, "Deleting a selected point removed its model or failed.");
            tool.MountEditor.Tick(false);
            Require(tool.MountEditor.Selected == null, "Deletion retained an inactive point selection.");
            History(undo, Key.Z); Require(loaded.gameObject.activeSelf, "Undo point deletion shortcut failed without a selection.");
            Require(loaded.GetComponentsInChildren<MeshRenderer>().All(r => !r.forceRenderingOff), "Undoing deletion in M hid the restored mounting rings.");
            History(redo, Key.Y); Require(!loaded.gameObject.activeSelf, "Redo point deletion shortcut failed without a selection.");
            History(undo, Key.Z);
            EventSystem.current.SetSelectedGameObject(Get<UnityEngine.UI.InputField>(tool, "depthField").gameObject);
            state = StateSystem.curIndex; History(redo, Key.Y);
            Require(loaded.gameObject.activeSelf && StateSystem.curIndex == state, "History shortcuts changed the build while a hole dimension field was focused.");
            EventSystem.current.SetSelectedGameObject(null);
            Keys(); tool.ToggleMounts();
            Require(loaded.GetComponentsInChildren<MeshRenderer>().All(r => r.forceRenderingOff) && loaded.GetComponent<Collider>().enabled,
                "Leaving M hid attachment colliders or left rings visible.");

            void History(Protobot.InputEvents.InputEvent input, Key key) {
                Keys(); input.Update(); Keys(Key.LeftCtrl, key); input.Update(); Keys(); input.Update();
            }
            void Pointer(Vector3 world) => InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(world));
            void Click(Vector3 world) {
                var position = (Vector2)camera.WorldToScreenPoint(world);
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); UpdateInput();
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position, buttons = 1 }); UpdateInput();
                Invoke(tool, "UpdateMountPlacement");
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); UpdateInput();
            }
        }

        private static CustomModelData FrameMesh() {
            var outer = new[] { new Vector3(-2,-2,0), new Vector3(2,-2,0), new Vector3(2,2,0), new Vector3(-2,2,0) };
            var inner = new[] { new Vector3(-.4f,-.4f,0), new Vector3(.4f,-.4f,0), new Vector3(.4f,.4f,0), new Vector3(-.4f,.4f,0) };
            var vertices = outer.Concat(inner).SelectMany(v => new[] { v.x, v.y, 0f }).ToArray();
            var indices = Enumerable.Range(0,4).SelectMany(i => { int next = (i+1)%4; return new[] { i,next,next+4,i,next+4,i+4 }; }).ToArray();
            return new CustomModelData { name = "Pre-drilled plate", vertices = vertices, triangles = indices };
        }
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static void Keys(params Key[] keys) { InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys)); UpdateInput(); }
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static object Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj,args);
        private static void Near(float a, float b, string message) => Require(Mathf.Abs(a-b) < .0001f,message);
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a,b) < .0001f,message + $" ({a:F6} vs {b:F6}, distance={Vector3.Distance(a,b):G9})");
        private static void Near(Quaternion a, Quaternion b, string message) => Require(Quaternion.Angle(a,b) < .02f,message);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
