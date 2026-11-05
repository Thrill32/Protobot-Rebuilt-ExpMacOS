using System;
using System.Globalization;
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
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CustomMountDepthValidation {
        private const string SmokeKey = "Protobot.CustomMountDepthSmoke";
        private static int frames;
        public static void Smoke() {
            CustomModelValidation.Validate();
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod] private static void Resume() {
            if (SessionState.GetBool(SmokeKey, false)) { frames = 0; EditorApplication.update += Tick; }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
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
                Debug.Log("Custom mounting depth passed: real B/rebound input, relative multi-model mouse depth, limits, snapping/bypass, arbitrary-axis cursor wrapping, upward toolbar/exact-screen-edge/outside-screen wrapping through the real controller, UI click guards, slash relative/absolute entry, invalid-input/pointer recovery, B/N and rebound calibration, confirmation-frame capture, cancellation, grouped undo/redo, collider/ring geometry, fastener refresh and preview-safe binary/JSON persistence.");
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
            var otherBody = CustomModel.Create(PlaneMesh(), body.transform.position + Vector3.right * 3, Quaternion.Euler(35, 8, 40));
            var other = otherBody.GetComponent<CustomModel>();
            var first = model.AddMount(body.transform.position, body.transform.forward, .169f, .046f, true);
            var last = other.AddMount(otherBody.transform.position, otherBody.transform.forward, .23f, .17f, false);
            var points = new[] { first, last };
            first.RecordAdded(); last.RecordAdded();
            var poses = points.Select(p => p.LocalPose).ToArray();
            var surfaces = points.Select(p => p.Surface).ToArray();
            var rotations = points.Select(p => p.transform.rotation).ToArray();
            var depths = points.Select(p => p.Depth).ToArray();
            PivotCamera.Main.SetTransform(body.transform.position, Quaternion.LookRotation(-last.transform.forward, last.transform.up).eulerAngles, -8);
            EventSystem.current.SetSelectedGameObject(null); Physics.SyncTransforms();
            tool.ToggleMounts(); await NextFrame();
            var editor = tool.MountEditor;
            editor.Select(first); editor.Select(last, true);
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None);
            var depth = inputs.Single(i => i.name == "Change Mounting Depth");
            var calibrate = inputs.Single(i => i.name == "Recalibrate Mounting Grid");
            var box = inputs.Single(i => i.name == "Box Select");
            var baseline = SceneBuild.ToBuildData();
            int state = StateSystem.curIndex;
            Pointer((Vector2)camera.WorldToScreenPoint(last.Surface));
            Keys(); depth.Update(); Keys(Key.B); depth.Update(); Keys();
            Require(editor.IsEditing && !KeyboardTransformTool.IsCapturingInput && !CustomModelTool.AllowsInput(box), "B did not capture hole-only depth editing ahead of box/part transforms.");
            var start = (Vector2)MouseInput.Position;
            float perPixel = Vector3.Distance(camera.ScreenToWorldPoint(new Vector3(0, 0, camera.WorldToScreenPoint(last.Surface).z)),
                camera.ScreenToWorldPoint(new Vector3(0, 1, camera.WorldToScreenPoint(last.Surface).z)));
            Pointer(start + Vector2.up * (.134f / perPixel)); editor.Tick(false);
            Near(first.Depth, depths[0] + .13f, "Depth mouse snapping did not use hundredths of an inch.");
            Near(last.Depth, depths[1] + .13f, "Mouse depth replaced unequal initial depths.");
            Keys(Key.LeftCtrl); Pointer(start + Vector2.up * (.134f / perPixel)); editor.Tick(false); Keys();
            Near(first.Depth, depths[0] + .134f, "Snap Bypass did not retain fine depth control.");
            CheckGeometry();
            Require(SceneBuild.ToBuildData().CompareData(baseline), "A depth/center preview leaked into a saved build.");
            Keys(Key.LeftCtrl); Pointer(start - Vector2.up * (1f / perPixel)); editor.Tick(false); Keys();
            Require(first.Depth > 0 && last.Depth > 0, "Mouse depth allowed an empty bore.");
            Near(last.Depth - first.Depth, depths[1] - depths[0], "Clamping one bore changed relative depths.");
            Keys(Key.LeftCtrl); Pointer(start + Vector2.up * (200f / perPixel)); editor.Tick(false); Keys();
            Require(points.All(p => p.Depth <= 100), "Mouse depth exceeded the dimension limit.");
            Near(last.Depth - first.Depth, depths[1] - depths[0], "Maximum-depth clamping changed relative depths.");
            editor.Finish(false);
            Require(StateSystem.curIndex == state && SceneBuild.ToBuildData().CompareData(baseline), "Cancel changed history or saved dimensions.");
            CheckOriginal();

            CheckControllerWrapping(tool, camera, points);
            CheckOriginal();

            // Slash and Enter pass through the real numeric editor, with no snapping.
            editor.BeginDepth(); Slash(); Text(".012345"); Keys(Key.Enter); editor.Tick(false); Keys();
            Require(!editor.IsEditing && StateSystem.curIndex == state + 1, "Relative numeric depth was not one committed edit.");
            Near(first.Depth, depths[0] + .012345f, "Relative exact depth was snapped or applied from the preview.");
            Near(last.Depth, depths[1] + .012345f, "Relative exact entry missed a selected model.");
            CheckGeometry();
            StateSystem.instance.Undo(); CheckOriginal();
            StateSystem.instance.Redo(); Near(last.Depth, depths[1] + .012345f, "Depth redo lost its target.");
            StateSystem.instance.Undo();

            editor.BeginDepth(); Slash(); Slash();
            Require(editor.IsTyping && !CustomModelTool.AllowsInput(calibrate), "Numeric depth did not capture calibration shortcuts.");
            Text(".123456"); Keys(Key.NumpadEnter); editor.Tick(false); Keys();
            Require(!editor.IsEditing, "Absolute numeric depth did not confirm.");
            foreach (var point in points) Near(point.Depth, .123456f, "Absolute depth did not set the requested common depth.");
            CheckGeometry(); StateSystem.instance.Undo(); CheckOriginal();

            editor.BeginDepth(); editor.ApplyDepth(.02f); Slash(); Text("-1"); Keys(Key.Enter); editor.Tick(false); Keys();
            Require(editor.IsEditing && !editor.IsTyping, "Invalid depth confirmed or retained numeric capture.");
            Near(first.Depth, depths[0] + .02f, "Invalid numeric depth destroyed the preview.");
            Keys(Key.LeftCtrl); editor.Tick(false); Keys(); Near(first.Depth, depths[0] + .02f, "Returning from numeric entry jumped at a stationary pointer.");
            Require(!editor.ApplyDepth(float.NaN) && !editor.ApplyDepth(0, true) && !editor.ApplyDepth(101, true), "Invalid dimensions were accepted.");
            Slash(); Slash(); Slash(); Keys(Key.LeftCtrl); editor.Tick(false); Keys();
            Near(first.Depth, depths[0] + .02f, "Slash cycling lost the depth preview.");
            Require(editor.CancelLevel() && !editor.IsEditing && editor.SelectedPoints.Count == 2, "Escape's edit tier did not retain hole selection.");
            CheckOriginal();

            // N commits the preview and samples the last selection without changing the grid or type.
            int sibling = last.transform.GetSiblingIndex();
            editor.BeginDepth(); editor.ApplyDepth(.07f);
            Keys(); calibrate.Update(); Keys(Key.N); calibrate.Update(); Keys();
            Require(!editor.IsEditing && editor.Tick(false), "B/N did not end editing and capture its completion frame.");
            Near(float.Parse(Get<InputField>(tool, "depthField").text, CultureInfo.InvariantCulture), depths[1] + .07f, "N sampled stale or first-selected depth.");
            Near(float.Parse(Get<InputField>(tool, "diameterField").text, CultureInfo.InvariantCulture), last.Diameter, "N did not copy the last-selected diameter.");
            Require(last.transform.GetSiblingIndex() == sibling && Get<bool>(tool, "threaded"), "Depth calibration changed grid order or the new-hole type.");
            CheckGeometry();
            StateSystem.instance.Undo(); CheckOriginal(); StateSystem.instance.Redo();
            var committed = SceneBuild.ToBuildData();

            // Rebinding either command must use InputEvent rather than physical B/N.
            var oldDepth = depth.defaultAction; oldDepth.Disable();
            var oldCalibration = calibrate.defaultAction; oldCalibration.Disable();
            using (var reboundDepth = new InputAction("Depth test", InputActionType.Button, "<Keyboard>/f7"))
            using (var reboundCalibration = new InputAction("Depth calibration test", InputActionType.Button, "<Keyboard>/f8")) {
                depth.defaultAction = reboundDepth; calibrate.defaultAction = reboundCalibration;
                reboundDepth.Enable(); reboundCalibration.Enable();
                try {
                    Keys(); depth.Update(); calibrate.Update(); Keys(Key.B); depth.Update();
                    Require(!editor.IsEditing, "Rebound depth still reacted to B.");
                    Keys(); depth.Update(); Keys(Key.F7); depth.Update(); Keys();
                    Require(editor.IsEditing, "Rebound depth command did not start.");
                    editor.ApplyDepth(.01f); Keys(Key.N); calibrate.Update();
                    Require(editor.IsEditing, "Rebound calibration still reacted to N.");
                    Keys(); calibrate.Update(); Keys(Key.F8); calibrate.Update(); Keys();
                    Require(!editor.IsEditing, "Rebound calibration did not finish the depth edit.");
                    StateSystem.instance.Undo(); Require(SceneBuild.ToBuildData().CompareData(committed), "Rebound depth undo missed a model.");
                }
                finally { Keys(); depth.defaultAction = oldDepth; calibrate.defaultAction = oldCalibration; oldDepth.Enable(); oldCalibration.Enable(); }
            }

            // Fine depth updates must refresh the actual attachment metadata and rings.
            var screwBody = PartsManager.GeneratePart("SCRW-1.00in", first.Surface, Quaternion.LookRotation(first.transform.forward));
            var detector = screwBody.GetComponent<Screw>().holeDetector;
            Physics.SyncTransforms(); detector.RefreshOverlaps();
            Require(detector.holes.Contains(first.GetComponent<HoleCollider>()), "Depth-edited bore no longer accepts a screw.");
            editor.Select(first); editor.BeginDepth(); editor.ApplyDepth(.005f); editor.Finish(true);
            Require(detector.holes.Contains(first.GetComponent<HoleCollider>()), "Committing depth lost fastener occupancy.");
            StateSystem.instance.Undo(); Require(detector.holes.Contains(first.GetComponent<HoleCollider>()), "Depth undo did not refresh fastener occupancy.");
            screwBody.SetActive(false); Object.Destroy(screwBody);

            CheckWrapping(camera, last.Surface);
            tool.ToggleMounts();
            var saved = SceneBuild.ToBuildData();
            Require(saved.CompareData(JsonUtility.FromJson<BuildData>(JsonUtility.ToJson(saved))), "Depth-edited mounts lost JSON persistence.");
            using var memory = new MemoryStream(); var formatter = new BinaryFormatter();
            formatter.Serialize(memory, saved); memory.Position = 0;
            SceneBuild.GenerateBuild((BuildData)formatter.Deserialize(memory));
            Require(saved.CompareData(SceneBuild.ToBuildData()), "Depth-edited mounts lost binary persistence.");

            void Slash() { Keys(); Keys(Key.Slash); editor.Tick(false); Keys(); }
            void CheckOriginal() {
                for (int i = 0; i < points.Length; i++) {
                    Near(points[i].Depth, depths[i], "Cancel/undo did not restore a hole's own depth.");
                    Near(points[i].LocalPose.position, poses[i].position, "Cancel/undo did not restore the hole center.");
                }
                CheckGeometry();
            }
            void CheckGeometry() {
                for (int i = 0; i < points.Length; i++) {
                    var point = points[i];
                    Near(point.Surface, surfaces[i], "Depth editing moved a front face.");
                    Require(Quaternion.Angle(point.transform.rotation, rotations[i]) < .01f, "Depth editing rotated a hole.");
                    Near(point.GetComponent<HoleCollider>().holeData.depth, point.Depth, "Attachment depth metadata is stale.");
                    Near(point.transform.localScale.z, point.Depth + .004f, "Physical collider depth is stale.");
                    var rings = point.GetComponentsInChildren<MeshRenderer>();
                    Require(rings.Length == 2, "Depth editing duplicated face rings.");
                    foreach (var ring in rings)
                        Near(Mathf.Abs(Vector3.Dot(ring.transform.position - point.transform.position, point.transform.forward)), point.Depth * .5f + .003f, "A ring did not track its mating face.");
                }
            }
        }
        private static void CheckControllerWrapping(CustomModelTool tool, Camera camera, CustomMountPoint[] points) {
            var editor = tool.MountEditor;
            var updateMountEditing = typeof(CustomModelTool).GetMethod("UpdateMountEditing", BindingFlags.Instance | BindingFlags.NonPublic);
            // Batch GameViews have no OS focus; inject it at the controller boundary.
            void UpdateEditing(bool focused = true) => updateMountEditing.Invoke(tool, new object[] { focused });
            var module = EventSystem.current.currentInputModule;
            try {
                // Exercise the controller's pause calculation and UI hit testing,
                // rather than bypassing them with editor.Tick(false).
                Keys(Key.LeftCtrl); Pointer(camera.pixelRect.center); module.Process();
                editor.BeginDepth();
                typeof(CustomMountEditor).GetField("startFrame", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(editor, -1);
                float difference = points[1].Depth - points[0].Depth;
                foreach (float top in new[] { Screen.height - 2f, Screen.height, Screen.height + 3f }) {
                    float previous = points[0].Depth;
                    Pointer(new Vector2(camera.pixelRect.center.x, top)); module.Process();
                    if (top < Screen.height) Require(MouseInput.overUI, "The pointer missed Main's top toolbar.");
                    else Require(!MouseInput.withinScreen, "The boundary fixture did not reach the exact/outside screen edge.");
                    UpdateEditing();
                    var wrapped = (Vector2)MouseInput.Position;
                    Require(wrapped.y < Screen.height * .5f, $"Upward depth movement stopped at the toolbar/screen boundary instead of wrapping (y={top}).");
                    Require(points[0].Depth > previous, "Upward wrapping stopped increasing depth.");
                    Near(points[1].Depth - points[0].Depth, difference, "Upward wrapping changed relative selected depths.");
                    float beforeAcknowledgement = points[0].Depth;
                    Pointer(wrapped); module.Process(); UpdateEditing();
                    Near(points[0].Depth, beforeAcknowledgement, "An upward warp acknowledgement jumped the depth.");
                    Pointer(wrapped + Vector2.up * 20); module.Process(); UpdateEditing();
                    Require(points[0].Depth > beforeAcknowledgement, "Depth did not continue after an upward wrap.");
                }

                float beforePause = points[0].Depth;
                Pointer(camera.pixelRect.center); module.Process(); UpdateEditing(false);
                Near(points[0].Depth, beforePause, "An unfocused editor continued changing depth.");
                Pointer(camera.pixelRect.center + Vector2.up * 30); module.Process(); UpdateEditing();
                Near(points[0].Depth, beforePause, "Resuming a paused controller jumped the depth.");

                var overToolbar = new Vector2(camera.pixelRect.center.x, Screen.height - 2);
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = overToolbar, buttons = 1 }); UpdateInput();
                module.Process(); Require(MouseInput.overUI, "The click fixture missed Main's toolbar."); UpdateEditing();
                Require(editor.IsEditing, "A UI click confirmed the depth gesture.");
                Pointer((Vector2)MouseInput.Position); // Acknowledge the wrap before revisiting the toolbar.
                Pointer(overToolbar); Keys(Key.LeftCtrl, Key.Enter); module.Process(); UpdateEditing(); Keys();
                Require(!editor.IsEditing, "Enter over the toolbar did not confirm depth.");
                StateSystem.instance.Undo();
            }
            finally {
                editor.Finish(false); Keys();
                module.Process();
            }
        }
        private static void CheckWrapping(Camera camera, Vector3 origin) {
            var gesture = new TransformGesture();
            var center = camera.pixelRect.center;
            foreach (bool orthographic in new[] { false, true }) {
                camera.orthographic = orthographic;
                foreach (var axis in new[] { new Vector3(.3f, .8f, -.2f).normalized, -camera.transform.forward }) {
                    gesture.Begin(camera, origin, center);
                    var before = new Vector2(camera.pixelRect.xMax - 2, center.y);
                    Require(TransformCursor.TryWrap(before, camera.pixelRect, out var after), "Cursor fixture did not reach a viewport edge.");
                    for (int i = 0; i < 20; i++) {
                        var target = gesture.Position(before, axis, false);
                        var sensitivity = gesture.Position(before + Vector2.one * 10, axis, false) - target;
                        gesture.RebasePointer(before, after, axis);
                        Near(gesture.Position(after, axis, false), target, "Oblique depth axis jumped across a cursor wrap.");
                        Near(gesture.Position(after + Vector2.one * 10, axis, false) - target, sensitivity, "Depth sensitivity changed after a cursor wrap.");
                    }
                }
            }
            camera.orthographic = false;
        }
        private static CustomModelData PlaneMesh() => new() {
            name = "Depth editing fixture", vertices = new float[] { -2,-2,0, 2,-2,0, 2,2,0, -2,2,0 }, triangles = new[] { 0,1,2, 0,2,3 }
        };
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static void Keys(params Key[] keys) { InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys)); UpdateInput(); }
        private static void Pointer(Vector2 position) { InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); UpdateInput(); }
        private static void Text(string text) { foreach (char character in text) InputSystem.QueueTextEvent(Keyboard.current, character); UpdateInput(); }
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Near(float a, float b, string message) => Require(Mathf.Abs(a - b) < .00015f, message + $" ({a} vs {b})");
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .0003f, message + $" ({a} vs {b})");
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
