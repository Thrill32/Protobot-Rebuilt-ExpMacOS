using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class GrabHoleSnappingValidation {
        private const string SmokeKey = "Protobot.GrabHoleSmoke";
        private static int frames;
        private static readonly List<GameObject> temporary = new();
        private static readonly List<InputEvent> inputs = new();

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0;
            EditorApplication.update += Tick;
        }

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

        private static void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(SmokeKey, false);
            try {
                Check();
                Debug.Log("Grab hole snapping smoke passed: source/target face previews, opposite source selection, camera navigation, XYZ priority, H/grid priority and rebinding, rigid face alignment, opposite faces, H-to-free continuity, original XYZ/numeric anchors, selected-hole groups, undo/redo, and fastener grouping/individual selection.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void Check() {
            var tool = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var manager = Get<SelectionManager>(tool, "selectionManager");
            foreach (var field in new[] { "holeSnapInput", "oppositeHoleInput", "mouseCast", "holePreview", "orbitInput", "panInput", "noclipInput" })
                Require(new SerializedObject(tool).FindProperty(field).objectReferenceValue != null, "Missing reference: " + field);
            var binding = Get<InputEvent>(tool, "holeSnapInput");
            Require(binding.defaultAction.bindings[0].path == "<Keyboard>/h", "Hole mode must default to H.");
            Require(Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(row =>
                new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue == binding), "Missing H Preferences row.");

            var previousStates = StateSystem.states;
            int previousIndex = StateSystem.curIndex;
            var previousSelection = manager.current;
            var previousCamera = Get<Camera>(tool, "refCamera");
            var previousCast = Get<MouseCast>(tool, "mouseCast");
            var previousOpposite = Get<InputEvent>(tool, "oppositeHoleInput");
            var camera = New("Grab validation camera").AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 10;
            camera.pixelRect = new Rect(0, 0, 800, 600);
            var center = new Vector3(1000, -1000, 1000);
            camera.transform.position = center + Vector3.back * 20;
            Set(tool, "refCamera", camera);
            var cast = camera.gameObject.AddComponent<MouseCast>();
            // Scene component caching runs at startup; this probe is created afterwards.
            Set(cast, "camera", camera);
            Set(tool, "mouseCast", cast);
            Set(tool, "oppositeHoleInput", Input("Opposite test", "<Keyboard>/space"));
            var first = Body("First grabbed body", center + new Vector3(-3, 0, 0));
            var second = Body("Second grabbed body", center + new Vector3(-1, 2, 0));
            var source = Hole(first, new Vector3(.2f, 0, 0));
            var targetBody = Body("Target body", center + new Vector3(4, 1, 0));
            var target = Hole(targetBody, Vector3.zero);
            var multi = new MultiSelection(null, new List<GameObject> { first, second });
            temporary.Add(multi.gameObject);
            multi.Select();
            manager.current = multi;
            var startFirst = first.transform.position;
            var startSecond = second.transform.position;
            var relative = second.transform.position - first.transform.position;
            try {
                Keys();
                Invoke(tool, "BeginSelection", false);
                var originalAnchor = Get<Vector3>(tool, "grabOrigin");
                CheckPriority(tool);
                Keys();
                Invoke(tool, "Finish", false);
                Invoke(tool, "BeginSelection", false);
                Invoke(tool, "ToggleHoleSnap");
                Require(Get<bool>(tool, "holeSnapping") && !Get<bool>(tool, "hasSourceHole"), "Multi-selection must allow source picking.");
                CheckCameraNavigation(tool, camera, source);
                CheckSourcePreview(tool, camera, source, target);
                Invoke(tool, "ChangeAxis", Vector3.right);
                Require(!Get<HoleFace>(tool, "holePreview").gameObject.activeSelf, "Axis exit left a hole preview visible.");
                Require(!Get<bool>(tool, "holeSnapping") && Get<Vector3>(tool, "axis") == Vector3.right, "X must leave source picking.");
                Near(first.transform.position, startFirst, "Exiting source picking moved a part.");
                Invoke(tool, "ToggleHoleSnap");
                Point(camera, target.transform.position);
                Require(!(bool)Invoke(tool, "PickSourceHole"), "Picked a source outside the selection.");
                Point(camera, source.transform.position);
                Require((bool)Invoke(tool, "PickSourceHole"), "Could not pick the source hole.");
                Near(first.transform.position, startFirst, "Rebasing the source pivot moved a part.");
                Snap(tool, camera, target, false);
                Near(source.transform.position - source.transform.forward * .1f,
                    target.transform.position - target.transform.forward * .1f, "Source face missed the target face.");
                Require(Vector3.Dot(-source.transform.forward, -target.transform.forward) < -.999f, "Source face did not face the target.");
                Near(Quaternion.Inverse(first.transform.rotation) * (second.transform.position - first.transform.position), relative, "Rigid spacing changed.");
                Require(first.transform.parent == multi.pivot.transform && second.transform.parent == multi.pivot.transform,
                    "Hole snapping changed MultiPivot parenting.");
                Snap(tool, camera, target, false); // Moving geometry now covers the target in the ray.
                Require(Get<bool>(tool, "hasHoleTarget"), "Moving colliders blocked the target hole.");
                Snap(tool, camera, target, true);
                Near(source.transform.position - source.transform.forward * .1f,
                    target.transform.position + target.transform.forward * .1f, "Opposite target face failed.");

                Keys();
                var snappedPosition = first.transform.position;
                var snappedRotation = first.transform.rotation;
                Invoke(tool, "ToggleHoleSnap");
                Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                Complete(tool);
                Near(first.transform.position, snappedPosition, "H-to-free jumped.");
                Near(first.transform.rotation, snappedRotation, "H-to-free lost orientation.");
                Invoke(tool, "ToggleHoleSnap");
                Invoke(tool, "ChangeAxis", Vector3.up);
                Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                Complete(tool);
                Require(Mathf.Abs(multi.pivot.transform.position.x - originalAnchor.x) < .003f &&
                    Mathf.Abs(multi.pivot.transform.position.z - originalAnchor.z) < .003f,
                    "H-to-Y must use the original grab anchor for the other coordinates.");
                snappedPosition = first.transform.position;
                var mouse = (Vector2)MouseInput.Position + Vector2.up * 30;
                InputState.Change(Mouse.current.position, mouse, InputUpdateType.Dynamic);
                Invoke(tool, "Apply", mouse);
                Complete(tool);
                var delta = first.transform.position - snappedPosition;
                Require(delta.y > .1f && Mathf.Abs(delta.x) < .002f && Mathf.Abs(delta.z) < .002f, "Y did not constrain movement after H.");
                Near(first.transform.rotation, snappedRotation, "Axis movement discarded hole alignment.");
                Invoke(tool, "Finish", false);
                Near(first.transform.position, startFirst, "Cancel failed to restore first part.");
                Near(second.transform.position, startSecond, "Cancel failed to restore second part.");

                // A selected hole should immediately become the anchor for its whole group.
                multi.gameObject.tag = "Group";
                var marker = New("Selected source face");
                marker.AddComponent<MeshFilter>();
                var face = marker.AddComponent<HoleFace>();
                face.Set(source.holeData, -Vector3.forward);
                manager.current = new HoleSelection { gameObject = marker, holeData = source.holeData, holeFace = face, faceDirection = -Vector3.forward };
                Invoke(tool, "BeginSelection", false);
                Require(Get<bool>(tool, "hasSourceHole") && Get<List<GameObject>>(tool, "bodies").Count == 2,
                    "Selected hole did not capture its whole group.");
                Invoke(tool, "ToggleHoleSnap");
                Snap(tool, camera, target, false);
                StateSystem.states = new List<State> { new(new List<IElement>()) };
                StateSystem.curIndex = 0;
                Invoke(tool, "Finish", true);
                Require(StateSystem.states.Count == 2 && !Get<bool>(tool, "active"), "Hole snap must commit as one undo step.");
                var finalPosition = first.transform.position;
                var finalRotation = first.transform.rotation;
                StateSystem.instance.Undo();
                first.transform.DOComplete(); second.transform.DOComplete();
                Near(first.transform.position, startFirst, "Undo did not restore first part.");
                Near(second.transform.position, startSecond, "Undo did not restore second part.");
                StateSystem.instance.Redo();
                first.transform.DOComplete(); second.transform.DOComplete();
                Near(first.transform.position, finalPosition, "Redo lost snapped position.");
                Near(first.transform.rotation, finalRotation, "Redo lost snapped orientation.");

                Invoke(tool, "BeginSelection", false);
                Invoke(tool, "ToggleHoleSnap");
                Snap(tool, camera, target, false);
                Invoke(tool, "ChangeAxis", Vector3.up);
                var numericAnchor = Get<Vector3>(tool, "grabOrigin");
                var numericRotation = first.transform.rotation;
                var numeric = Get<TransformNumericInput>(tool, "numericInput");
                numeric.Cycle(false);
                typeof(TransformNumericInput).GetProperty("Text").SetValue(numeric, ".25");
                Require((bool)Invoke(tool, "SubmitNumeric"), "Numeric entry after H was rejected.");
                Complete(tool);
                face.Refresh();
                Near(face.position, numericAnchor + Vector3.up * .25f, "Numeric entry after H lost the original grab origin.");
                Near(first.transform.rotation, numericRotation, "Numeric entry after H lost alignment.");
                FastenerGroupingValidation.Check(tool, manager);
            }
            finally {
                Invoke(tool, "Finish", false);
                Set(tool, "refCamera", previousCamera);
                Set(tool, "mouseCast", previousCast);
                Set(tool, "oppositeHoleInput", previousOpposite);
                manager.current = previousSelection;
                StateSystem.states = previousStates;
                StateSystem.curIndex = previousIndex;
                foreach (var input in inputs) { input.defaultAction.Dispose(); input.rebindAction.action.Dispose(); }
                foreach (var obj in temporary.Where(obj => obj != null)) Object.DestroyImmediate(obj);
                inputs.Clear(); temporary.Clear();
            }
        }

        // Batch Mode has no focused application window; exercise the same modal
        // input path after LateUpdate's focus/lifecycle guard.
        private static void HandleInput(KeyboardTransformTool tool) => Invoke(tool, "HandleInput", Keyboard.current, Mouse.current);

        private static void CheckSourcePreview(KeyboardTransformTool tool, Camera camera, HoleCollider source, HoleCollider target) {
            var preview = Get<HoleFace>(tool, "holePreview");
            Point(camera, target.transform.position);
            HandleInput(tool);
            Require(!preview.gameObject.activeSelf, "Source preview highlighted a hole outside the selection.");
            Point(camera, source.transform.position);
            Keys();
            HandleInput(tool);
            Require(preview.gameObject.activeSelf && preview.hole == source.holeData, "Source hover did not highlight the picked hole.");
            Near(preview.position, source.transform.position - Vector3.forward * .1f, "Source hover highlighted the wrong face.");
            Keys(Key.Space);
            HandleInput(tool);
            Near(preview.position, source.transform.position + Vector3.forward * .1f, "Space did not highlight the opposite source face.");
            Require(Vector3.Dot(preview.direction, Vector3.forward) > .999f, "Opposite source preview has the wrong direction.");
            Keys();
            HandleInput(tool);
            Near(preview.position, source.transform.position - Vector3.forward * .1f, "Releasing Space did not restore the hovered source face.");
            Keys(Key.Space);
            Button(MouseButton.Left, true);
            HandleInput(tool);
            Button(MouseButton.Left, false);
            Require(Get<bool>(tool, "hasSourceHole") && !preview.gameObject.activeSelf, "Source click did not commit and clear its hover preview.");
            Near(Get<Vector3>(tool, "sourceHoleOrigin"), source.transform.position + Vector3.forward * .1f, "Source click ignored the held opposite-face modifier.");
            Snap(tool, camera, target, false);
            Near(source.transform.position + source.transform.forward * .1f,
                target.transform.position - target.transform.forward * .1f, "Opposite source face did not meet the target face.");
            Invoke(tool, "Finish", false);
            Require(!preview.gameObject.activeSelf, "Cancel left the hole preview visible.");
            Keys();
            Invoke(tool, "BeginSelection", false);
            Invoke(tool, "ToggleHoleSnap");
            Point(camera, source.transform.position);
            HandleInput(tool);
            Require(preview.gameObject.activeSelf, "Source preview did not return after cancellation.");
        }

        private static void CheckCameraNavigation(KeyboardTransformTool tool, Camera camera, HoleCollider source) {
            var obj = New("Grab camera input probe");
            obj.SetActive(false);
            var cameraInput = obj.AddComponent<MousePivotCameraInput>();
            Set(cameraInput, "orbitInput", Get<InputEvent>(tool, "orbitInput"));
            Set(cameraInput, "panInput", Get<InputEvent>(tool, "panInput"));
            int orbits = 0, pans = 0, zooms = 0;
            cameraInput.updateOrbit += _ => orbits++;
            cameraInput.updatePan += _ => pans++;
            cameraInput.updateZoom += _ => zooms++;
            var noclip = Object.FindFirstObjectByType<NoclipCameraInput>();
            var start = source.transform.position;
            Require(KeyboardTransformTool.AllowsCameraNavigation && KeyboardTransformTool.AllowsInput(Get<InputEvent>(tool, "noclipInput")),
                "H mode blocked camera/noclip navigation.");
            if (Application.isFocused)
                Require((bool)Invoke(noclip, "CanCaptureInput"), "H mode blocked noclip activation.");
            try {
                Point(camera, source.transform.position);
                InputState.Change(Mouse.current.delta, new Vector2(10, 5), InputUpdateType.Dynamic);
                Button(MouseButton.Right, true);
                Get<InputEvent>(tool, "orbitInput").Update();
                Invoke(cameraInput, "Update");
                HandleInput(tool);
                Require(orbits == 1 && Get<bool>(tool, "active"), "Right-click did not orbit or canceled H mode.");
                Require(!Get<bool>(tool, "hasSourceHole"), "Orbit selected a source hole.");
                Button(MouseButton.Right, false);
                Get<InputEvent>(tool, "orbitInput").Update();
                Button(MouseButton.Middle, true);
                Get<InputEvent>(tool, "panInput").Update();
                Invoke(cameraInput, "Update");
                HandleInput(tool);
                Require(pans == 1 && Get<bool>(tool, "active"), "H mode blocked camera pan.");
                Button(MouseButton.Middle, false);
                Get<InputEvent>(tool, "panInput").Update();
                InputState.Change(Mouse.current.position, new Vector2(Screen.width / 2f, Screen.height / 2f), InputUpdateType.Dynamic);
                InputState.Change(Mouse.current.scroll, Vector2.up, InputUpdateType.Dynamic);
                Invoke(cameraInput, "Update");
                Require(zooms == 1, "H mode blocked scroll zoom.");
                InputEvent.SetNoclipMovementCapture(true);
                Button(MouseButton.Left, true);
                HandleInput(tool);
                Require(!Get<bool>(tool, "hasSourceHole"), "Noclip click selected a source hole.");
                Near(source.transform.position, start, "Camera navigation moved the selection.");
                InputEvent.SetNoclipMovementCapture(false);
                Button(MouseButton.Left, false);
                Invoke(tool, "LeaveHoleSnap");
                Require(KeyboardTransformTool.BlocksCameraNavigation && !(bool)Invoke(noclip, "CanCaptureInput"),
                    "Free grab unexpectedly allowed camera input.");
                Invoke(tool, "ToggleHoleSnap");
                var numeric = Get<TransformNumericInput>(tool, "numericInput");
                numeric.Cycle(true);
                Require(KeyboardTransformTool.BlocksCameraNavigation, "Numeric entry allowed camera input.");
                numeric.Close();
            }
            finally {
                Button(MouseButton.Right, false);
                Button(MouseButton.Middle, false);
                Button(MouseButton.Left, false);
                InputState.Change(Mouse.current.scroll, Vector2.zero, InputUpdateType.Dynamic);
                InputState.Change(Mouse.current.delta, Vector2.zero, InputUpdateType.Dynamic);
                InputEvent.SetNoclipMovementCapture(false);
            }
        }

        private static void CheckPriority(KeyboardTransformTool tool) {
            var grab = Get<InputEvent>(tool, "grabInput");
            var rotate = Get<InputEvent>(tool, "rotateInput");
            var holeInput = Get<InputEvent>(tool, "holeSnapInput");
            try {
                foreach (bool rotating in new[] { false, true }) {
                    Set(tool, "rotating", rotating);
                    foreach (var key in new[] { Key.X, Key.Y, Key.Z }) {
                        foreach (var name in new[] { "Grab Selection", "Rotate Placement", "Snap Bypass", "Grouping Bypass", "Toggle Grid", "Duplicate", "Grab Hole Snap" }) {
                            var input = Input(name, "<Keyboard>/" + key.ToString().ToLowerInvariant());
                            if (name == "Grab Selection") Set(tool, "grabInput", input);
                            if (name == "Rotate Placement") Set(tool, "rotateInput", input);
                            if (name == "Grab Hole Snap") Set(tool, "holeSnapInput", input);
                            int fired = 0;
                            input.performed += () => fired++;
                            Keys(key);
                            input.Update();
                            Require(fired == 0 && !input.IsPressed, "Axis leaked to allowed/rebound action: " + name);
                            Require(TransformGesture.TryReadAxis(out var axis) && axis != Vector3.zero,
                                $"Reserved axis was not readable: {key}, {name}, held={Keyboard.current[key].isPressed}, pressed={Keyboard.current[key].wasPressedThisFrame}.");
                            Keys(); input.Update();
                            Set(tool, "grabInput", grab); Set(tool, "rotateInput", rotate); Set(tool, "holeSnapInput", holeInput);
                        }
                    }
                }
                Set(tool, "rotating", false);
                var grid = Input("Toggle Grid", "<Keyboard>/h");
                var h = Input("Grab Hole Snap", "<Keyboard>/h");
                Set(tool, "holeSnapInput", h);
                h.performed += () => Invoke(tool, "ToggleHoleSnap");
                int gridChanges = 0;
                grid.performed += () => gridChanges++;
                Keys(Key.H); grid.Update(); h.Update();
                Require(gridChanges == 0 && Get<bool>(tool, "holeSnapping"), "H did not override grid before its handler ran.");
                Keys(); grid.Update(); h.Update();
                Keys(Key.H); h.Update(); grid.Update();
                Require(gridChanges == 0 && !Get<bool>(tool, "holeSnapping"), "Second H did not restore free movement.");
                Keys(); grid.Update(); h.Update();
                h.defaultAction.Disable(); h.rebindAction.action.AddBinding("<Keyboard>/k");
                Keys(Key.K); h.Update();
                Require(Get<bool>(tool, "holeSnapping"), "Rebound H mode was ignored.");
                Invoke(tool, "LeaveHoleSnap");
                Keys(); h.Update();
                Set(tool, "rotating", true);
                Keys(Key.H); grid.Update();
                Require(gridChanges == 1, "Grid H should remain available in rotation mode.");
            }
            finally {
                Keys(); Set(tool, "rotating", false);
                Set(tool, "grabInput", grab); Set(tool, "rotateInput", rotate); Set(tool, "holeSnapInput", holeInput);
            }
        }

        private static void Snap(KeyboardTransformTool tool, Camera camera, HoleCollider target, bool opposite) {
            if (opposite) Keys(Key.Space); else Keys();
            Point(camera, target.transform.position);
            Invoke(tool, "Apply", (Vector2)MouseInput.Position);
            Require(Get<bool>(tool, "hasHoleTarget"), "Target hole was not resolved.");
            var preview = Get<HoleFace>(tool, "holePreview");
            Require(preview.gameObject.activeSelf && preview.hole == target.holeData, "Target preview did not highlight the target hole.");
            Near(preview.position, target.transform.position + (opposite ? 1 : -1) * target.transform.forward * .1f,
                "Target preview highlighted the wrong face.");
            Complete(tool);
            Physics.SyncTransforms();
        }

        private static void Complete(KeyboardTransformTool tool) => Get<Tween>(tool, "smoothingTween")?.Complete();
        private static void Point(Camera camera, Vector3 point) {
            InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point), InputUpdateType.Dynamic);
            Physics.SyncTransforms();
        }
        private static void Button(MouseButton button, bool pressed) {
            var mouse = Mouse.current;
            var state = new MouseState {
                position = mouse.position.ReadValue(), delta = mouse.delta.ReadValue(), scroll = mouse.scroll.ReadValue()
            }.WithButton(MouseButton.Left, mouse.leftButton.isPressed)
                .WithButton(MouseButton.Right, mouse.rightButton.isPressed)
                .WithButton(MouseButton.Middle, mouse.middleButton.isPressed)
                .WithButton(button, pressed);
            InputState.Change(mouse, state, InputUpdateType.Dynamic);
        }
        private static void Keys(params Key[] keys) {
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
            InputState.Change(Keyboard.current, new KeyboardState(keys), InputUpdateType.Dynamic);
        }
        private static InputEvent Input(string name, string path) {
            var obj = New(name); obj.SetActive(false);
            var input = obj.AddComponent<InputEvent>();
            input.defaultAction = new InputAction(name, InputActionType.Button, path);
            input.defaultAction.Enable();
            input.rebindAction = new RebindAction("Grab validation " + Guid.NewGuid());
            inputs.Add(input);
            return input;
        }
        private static GameObject New(string name) { var obj = new GameObject(name); temporary.Add(obj); return obj; }
        private static GameObject Body(string name, Vector3 position) {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Cube); temporary.Add(obj); obj.name = name;
            Object.DestroyImmediate(obj.GetComponent<Collider>());
            obj.transform.position = position; obj.AddComponent<SavedObject>(); return obj;
        }
        private static HoleCollider Hole(GameObject body, Vector3 position) {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Cube); obj.name = "Validation hole";
            Object.DestroyImmediate(obj.GetComponent<Collider>());
            obj.transform.SetParent(body.transform, false); obj.transform.localPosition = position;
            obj.transform.localScale = new Vector3(.4f, .4f, .2f); obj.layer = HoleCollider.HOLE_COLLISIONS_LAYER;
            obj.AddComponent<MeshCollider>().sharedMesh = obj.GetComponent<MeshFilter>().sharedMesh;
            return obj.AddComponent<HoleCollider>();
        }
        private static object Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Near(Vector3 value, Vector3 expected, string message) => Require(Vector3.Distance(value, expected) < .003f, message);
        private static void Near(Quaternion value, Quaternion expected, string message) => Require(Quaternion.Angle(value, expected) < .03f, message);
    }
}
