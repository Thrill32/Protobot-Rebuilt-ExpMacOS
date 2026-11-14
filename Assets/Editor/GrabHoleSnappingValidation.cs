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
                Debug.Log("Grab-to-Move smoke passed: concrete hole enforcement, anchored additive scope, toolbar preservation, rollback/source loss, rebound input priority, duplicate cleanup, shared preview geometry, single-click Move rotation/commit and fastener grouping.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            GameObject.Find("Add Parts Menu")?.SetActive(false);
            foreach (var raycaster in Object.FindObjectsByType<UnityEngine.EventSystems.BaseRaycaster>(FindObjectsSortMode.None)) raycaster.enabled = false;
            var tool = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var manager = Get<SelectionManager>(tool, "selectionManager");
            var placement = Get<Placement>(tool, "placement");
            foreach (var field in new[] { "holeSnapInput", "moveTool", "moveToolManager", "orbitInput", "panInput", "noclipInput" })
                Require(new SerializedObject(tool).FindProperty(field).objectReferenceValue != null, "Missing reference: " + field);
            var binding = Get<InputEvent>(tool, "holeSnapInput");
            Require(binding.defaultAction.bindings[0].path == "<Keyboard>/h", "Move during grab must default to H.");
            Require(Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(row =>
                new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue == binding), "Missing H Preferences row.");
            var previousStates = StateSystem.states;
            int previousIndex = StateSystem.curIndex;
            var previousCamera = Get<Camera>(tool, "refCamera");
            var camera = New("Move shortcut camera").AddComponent<Camera>();
            camera.orthographic = true; camera.orthographicSize = 10; camera.pixelRect = new Rect(0, 0, 800, 600);
            var center = new Vector3(1000, -1000, 1000);
            camera.transform.position = center + Vector3.back * 20;
            Set(tool, "refCamera", camera);
            var first = Body("First moving body", center + new Vector3(-3, 0, 0));
            var second = Body("Second moving body", center + new Vector3(-1, 2, 0));
            var source = Hole(first, new Vector3(.2f, 0, 0));
            var target = Hole(Body("Target body", center + new Vector3(4, 1, 0)), Vector3.zero);
            var marker = New("Move shortcut source face"); marker.AddComponent<MeshFilter>();
            var face = marker.AddComponent<HoleFace>(); face.Set(source.holeData, Vector3.back);
            var move = Get<MoveToolManager>(tool, "moveToolManager");
            var toggle = Get<ToolToggle>(tool, "moveTool");
            try {
                CheckSingleClickMove(manager, camera, source, target);
                CheckSharedGeometry(tool);
                var multi = new MultiSelection(null, new List<GameObject> { first, second }) { individual = true };
                temporary.Add(multi.gameObject);
                var holeSelection = new HoleSelection { gameObject = marker, holeData = source.holeData,
                    holeFace = face, faceDirection = Vector3.back, individual = true };
                var anchoredMulti = new MultiSelection(null, new List<GameObject> { first, second }) {
                    individual = true,
                    placementHole = SelectedHoleAnchor.From(holeSelection)
                };
                temporary.Add(anchoredMulti.gameObject);
                var selections = new ISelection[] {
                    new ObjectSelection { gameObject = first, individual = true },
                    multi,
                    holeSelection,
                    anchoredMulti
                };
                var transformToggle = GameObject.Find("Transform Tool Toggle").GetComponent<ToolToggle>();
                transformToggle.Toggle(true);
                Require(transformToggle.toggled && !toggle.toggled, "The transform tool was not the pre-handoff toolbar state.");
                foreach (var selected in selections) {
                    manager.SetCurrent(selected, true, true);
                    ResetCapture();
                    move.StartMoving();
                    Require(placement.placing, "Tool 2 did not start a reference preview.");
                    var expectedMesh = Get<MeshFilter>(placement, "meshFilter").sharedMesh;
                    var expectedBounds = expectedMesh.bounds;
                    int expectedVertices = expectedMesh.vertexCount;
                    placement.CancelPlacement();
                    var start = new Pose(first.transform.position, first.transform.rotation);
                    var secondStart = second.transform.position;
                    int stateIndex = StateSystem.curIndex;
                    Invoke(tool, "BeginSelection", false);
                    Invoke(tool, "SetTarget", new Vector3(2, 3, 4), Quaternion.Euler(0, 0, 30)); Complete(tool);
                    if (selected is HoleSelection movingHole) movingHole.holeFace.Refresh();
                    Require(Vector3.Distance(first.transform.position, start.position) > .1f, "The grab did not move before handoff.");
                    Invoke(tool, "MoveFromGrab");
                    bool hasHole = MoveToolManager.HasConcreteSourceHole(selected);
                    Require(!Get<bool>(tool, "active") && placement.placing == hasHole,
                        "G/H did not enforce a concrete selected source hole.");
                    Require(transformToggle.toggled && !toggle.toggled,
                        "G/H changed the previously active toolbar tool.");
                    Require(manager.current == selected, "G/H changed the current selection.");
                    Near(first.transform.position, start.position, "The handoff kept an uncommitted grab offset.");
                    Near(first.transform.rotation, start.rotation, "The handoff kept an uncommitted grab rotation.");
                    Near(second.transform.position, secondStart, "The handoff moved another selection member.");
                    if (!hasHole) {
                        Require(StateSystem.curIndex == stateIndex && first.GetComponent<Renderer>().enabled,
                            "Denied body-only G/H left preview or undo state behind.");
                        continue;
                    }
                    ResetCapture();
                    Require(!transformToggle.active, "The previous tool's handles stayed active during borrowed Move.");
                    var data = Get<PlacementData>(placement, "currentPlacementData") as GameObjectPlacementData;
                    Require(data != null && data.GetGameObject() == selected.gameObject && data.individual == selected.individual &&
                        data.sourceHole?.Hole == source.holeData,
                        "G/H did not pass the concrete source hole, selection and grouping scope to Move.");
                    var preview = Get<MeshFilter>(placement, "meshFilter").sharedMesh;
                    Require(preview.vertexCount == expectedVertices, "G/H generated different geometry from tool 2.");
                    Near(preview.bounds.center, expectedBounds.center, "G/H preview center differs from tool 2.");
                    Near(preview.bounds.size, expectedBounds.size, "G/H preview bounds differ from tool 2.");
                    Get<InputEvent>(placement, "placeInput").performed?.Invoke();
                    Require(placement.placing, "The shortcut also committed Move on its starting frame.");
                    Require(StateSystem.curIndex == stateIndex, "Handing off added an undo operation.");
                    placement.CancelPlacement();
                    Invoke(tool, "MaintainMoveHandoff"); ResetCapture();
                    Require(StateSystem.curIndex == stateIndex && first.GetComponent<Renderer>().enabled &&
                        transformToggle.toggled && transformToggle.active && !toggle.toggled,
                        "Move cancellation left undo/renderer/tool state behind.");
                }
                manager.SetCurrent(holeSelection, true, true);
                Invoke(tool, "BeginSelection", false); Invoke(tool, "MoveFromGrab");
                Require(placement.placing, "Hole G/H did not start for source-loss validation.");
                source.gameObject.SetActive(false);
                Invoke(tool, "MaintainMoveHandoff");
                Require(!placement.placing && transformToggle.toggled && !toggle.toggled,
                    "Losing the selected source hole did not cancel and restore the prior toolbar state.");
                source.gameObject.SetActive(true); face.Set(source.holeData, Vector3.back);

                manager.SetCurrent(holeSelection, true, true);
                Invoke(tool, "BeginSelection", false); Invoke(tool, "MoveFromGrab");
                Invoke(placement, "Place");
                placement.CancelPlacement();
                Require(transformToggle.toggled && !toggle.toggled,
                    "Confirming G/H left the Move toolbar tool active.");

                manager.SetCurrent(holeSelection, true, true);
                Invoke(tool, "BeginSelection", false);
                CheckPriority(tool);
                // Duplicate's ordinary rollback must also run during the handoff.
                manager.SetCurrent(new ObjectSelection { gameObject = first, individual = true }, true, true);
                Invoke(tool, "BeginSelection", false);
                var copies = Object.FindFirstObjectByType<ObjectDuplicator>().CreateCopies(new[] { first });
                Get<List<GameObject>>(tool, "duplicates").AddRange(copies);
                Invoke(tool, "MoveFromGrab");
                Require(copies.All(copy => copy == null || !copy.activeInHierarchy) && !placement.placing,
                    "Denied body-only G/H kept temporary grab duplicates or opened free Move.");
                placement.CancelPlacement();
                FastenerGroupingValidation.Check(tool, manager);
            }
            finally {
                Invoke(tool, "Finish", false); placement.CancelPlacement();
                manager.ClearCurrent(false, true, true);
                Set(tool, "refCamera", previousCamera);
                StateSystem.states = previousStates; StateSystem.curIndex = previousIndex;
                foreach (var input in inputs) { input.defaultAction.Dispose(); input.rebindAction.action.Dispose(); }
                foreach (var obj in temporary.Where(obj => obj != null)) Object.DestroyImmediate(obj);
                inputs.Clear(); temporary.Clear();
            }
        }

        private static void ResetCapture() => typeof(KeyboardTransformTool)
            .GetField("finishedFrame", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, -1);

        private static void CheckSingleClickMove(SelectionManager manager, Camera camera, HoleCollider source, HoleCollider target) {
            var click = manager.GetComponent<ClickSelector>();
            var toggle = Get<ToolToggle>(click, "moveTool");
            var move = Get<MoveToolManager>(click, "moveToolManager");
            Require(toggle != null && move != null, "Single-click Move is not wired in Main.");
            var placement = Get<Placement>(move, "placement");
            var movement = Get<MovementManager>(placement, "movementManager");
            var hover = Get<HoleFace>(manager.GetComponent<HoleFaceResponseSelector>(), "hoverHoleFace");
            var cast = Get<MouseCast>(click, "mouseCast");
            var previousCast = Get<Camera>(cast, "camera");
            var previousPlacementCamera = Get<Camera>(placement, "refCamera");
            var previousStates = StateSystem.states;
            var previousIndex = StateSystem.curIndex;
            var previousSelection = manager.current;
            bool previousToggle = toggle.toggled;
            var start = new Pose(source.holeData.part.transform.position, source.holeData.part.transform.rotation);
            var previousTag = source.tag;
            try {
                StateSystem.states = new List<State> { new(new List<IElement>()) };
                StateSystem.curIndex = 0;
                Set(cast, "camera", camera);
                Set(placement, "refCamera", camera);
                source.tag = "HoleCollider";
                Keys();
                Point(camera, source.transform.position);
                Require(!MouseInput.overUI, "Move source pointer is over UI.");
                Require(cast.gameObject == source.gameObject,
                    $"Move source ray hit {cast.gameObject?.name ?? "nothing"}; mask {cast.layerMask}, pointer {MouseInput.Position}.");
                hover.Set(source.holeData, Vector3.back);
                toggle.toggled = false;
                Invoke(click, "OnPerformInput");
                Require(manager.current is HoleSelection && !placement.placing, "Inactive Move must only select the hole.");

                // Body clicks remain ordinary selection even with Move active.
                toggle.toggled = true;
                source.tag = "Untagged";
                Invoke(click, "OnPerformInput");
                Require(manager.current is ObjectSelection && !placement.placing, "Move started on a body click.");
                source.tag = "HoleCollider";

                // The source can be the opposite face, with individual scope.
                Keys(Key.LeftMeta);
                hover.Set(source.holeData, Vector3.forward);
                Invoke(click, "OnPerformInput");
                Require(placement.placing && manager.current is HoleSelection selected && selected.individual,
                    "One hole click did not start Move with the clicked grouping scope.");
                var data = Get<PlacementData>(placement, "currentPlacementData") as GameObjectPlacementData;
                Require(data != null && data.individual && data.GetGameObject() == manager.current.gameObject,
                    "Move did not use the newly selected hole.");
                Require(Vector3.Dot(data.GetGameObject().GetComponent<HoleFace>().direction, Vector3.forward) > .999f,
                    "Single-click Move lost the opposite source face.");
                Get<InputEvent>(placement, "placeInput").performed?.Invoke();
                Require(placement.placing && source.holeData.part.layer == Placement.PLACEMENT_LAYER,
                    "The source click also confirmed Move on its starting frame.");
                Near(source.holeData.part.transform.position, start.position, "Starting Move changed the source pose.");

                // Exercise the next-click path after advancing past the start frame.
                Set(placement, "placementStartFrame", Time.frameCount - 1);
                Keys();
                Point(camera, target.transform.position);
                Invoke(placement, "Update");
                Get<InputEvent>(placement, "toggleRotateInput").performed?.Invoke();
                Require(placement.rotating, "R did not rotate the Move preview.");
                var anchor = Get<Pose?>(placement, "rotationAnchor");
                var adjustment = Get<MoveRotation>(placement, "moveRotation");
                Require(anchor.HasValue, "Move lost its target axis.");
                Near(adjustment.Axis, anchor.Value.rotation * Vector3.forward, "Move did not rotate about the hole axis.");
                MoveRotationValidation.TurnPointer(camera, adjustment, () => Invoke(placement, "Update"));
                Require(Mathf.Abs(adjustment.Degrees) > 80, "Move preview did not rotate.");
                Keys(Key.Slash); Invoke(placement, "LateUpdate"); Keys();
                Require(placement.IsTypingRotation, "Move did not open slash rotation entry.");
                MoveRotationValidation.TypeText("12.5");
                Keys(Key.Enter); Invoke(placement, "LateUpdate"); Keys();
                Require(Mathf.Abs(adjustment.Degrees - 12.5f) < .001f, "Move did not retain the exact entered rotation.");
                Get<Tween>(movement, "movementTween")?.Complete();
                Invoke(movement, "Update");
                Require(source.holeData.part.layer == 0 &&
                    Vector3.Distance(source.holeData.part.transform.position, start.position) > .1f,
                    "The next click did not commit Move and restore the moving body.");
                Near(source.holeData.position + source.transform.forward * .1f, anchor.Value.position,
                    "Rotated Move lost source-face contact on commit.");
                Debug.Log("Single-click Move passed: inactive/body selection, immediate hole preview, opposite source face, individual scope, starting-click capture and next-click confirmation.");
            }
            finally {
                if (placement.placing) placement.StopPlacing();
                placement.placing = false;
                source.holeData.part.transform.SetPositionAndRotation(start.position, start.rotation);
                source.tag = previousTag;
                manager.ClearCurrent(bypassConditions: true);
                manager.current = previousSelection;
                toggle.toggled = previousToggle;
                Set(cast, "camera", previousCast);
                Set(placement, "refCamera", previousPlacementCamera);
                StateSystem.states = previousStates;
                StateSystem.curIndex = previousIndex;
                Keys();
                Physics.SyncTransforms();
            }
        }

        private static void CheckSharedGeometry(KeyboardTransformTool tool) {
            var root = New("Shared preview hierarchy");
            root.transform.SetPositionAndRotation(new Vector3(4000, 4000, 4000), Quaternion.Euler(13, 27, 19));
            root.transform.localScale = new Vector3(1.2f, .8f, 1.6f);
            var pivot = root.AddComponent<Pivot>();
            var first = Body("Nested preview part", root.transform.position);
            var second = Body("Second preview part", root.transform.position + Vector3.right * 2);
            pivot.AddObjects(new List<GameObject> { first, second });
            first.transform.localRotation = Quaternion.Euler(17, 41, 29);
            first.transform.localScale = new Vector3(.6f, 1.3f, .4f);
            var child = Body("Nested visible geometry", first.transform.position);
            child.transform.SetParent(first.transform, false);
            child.transform.localPosition = new Vector3(.7f, .4f, .2f);
            var hidden = Body("Disabled geometry", first.transform.position);
            hidden.transform.SetParent(first.transform, false);
            hidden.GetComponent<Renderer>().enabled = false;
            Hole(first, Vector3.zero);
            var filter = first.GetComponent<MeshFilter>();
            var originalMesh = filter.sharedMesh;
            var split = Object.Instantiate(originalMesh);
            var triangles = split.triangles;
            split.subMeshCount = 2;
            split.SetTriangles(triangles.Take(triangles.Length / 2).ToArray(), 0);
            split.SetTriangles(triangles.Skip(triangles.Length / 2).ToArray(), 1);
            filter.sharedMesh = split;
            var transforms = new[] { root.transform, first.transform, second.transform, child.transform };
            var parents = transforms.Select(transform => transform.parent).ToArray();
            var positions = transforms.Select(transform => transform.localPosition).ToArray();
            var rotations = transforms.Select(transform => transform.localRotation).ToArray();
            var scales = transforms.Select(transform => transform.localScale).ToArray();
            Mesh legacy = null, keyboard = null;
            var placement = Get<Placement>(tool, "placement");
            try {
                var renderers = MeshCombiner.GetRenderers(new[] { root, first, child });
                Require(renderers.Length == 3, "Shared geometry duplicated nested renderers or included hidden/hole geometry.");
                legacy = new GameObjectPlacementData(root, placement.transform).GetDisplayMesh();
                keyboard = MeshCombiner.CombineMeshes(renderers, root.transform.position, root.transform.rotation);
                Require(legacy.triangles.Length == triangles.Length * 3 && keyboard.triangles.Length == legacy.triangles.Length,
                    "Shared geometry lost submeshes or repeated overlapping input objects.");
                Near(legacy.bounds.center, keyboard.bounds.center, "Move and G/H generated different preview centers.");
                Near(legacy.bounds.size, keyboard.bounds.size, "Move and G/H generated different preview geometry.");
                placement.StartPlacing(new GameObjectPlacementData(root, placement.transform));
                Require(renderers.All(renderer => !renderer.enabled), "Legacy Move did not hide nested solid geometry.");
                placement.StopPlacing();
                placement.placing = false;
                Require(renderers.All(renderer => renderer.enabled) && !hidden.GetComponent<Renderer>().enabled,
                    "Legacy Move did not restore original renderer states.");
                for (int i = 0; i < transforms.Length; i++) {
                    Require(transforms[i].parent == parents[i], "Preview generation changed source parenting.");
                    Near(transforms[i].localPosition, positions[i], "Preview generation moved source geometry.");
                    Near(transforms[i].localRotation, rotations[i], "Preview generation rotated source geometry.");
                    Near(transforms[i].localScale, scales[i], "Preview generation changed source scale.");
                }
            }
            finally {
                placement.placing = false;
                filter.sharedMesh = originalMesh;
                Object.DestroyImmediate(split);
                if (legacy != null) Object.DestroyImmediate(legacy);
                if (keyboard != null) Object.DestroyImmediate(keyboard);
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
                var placement = Get<Placement>(tool, "placement");
                var grid = Input("Toggle Grid", "<Keyboard>/h");
                var h = Input("Grab Hole Snap", "<Keyboard>/h");
                Set(tool, "holeSnapInput", h);
                h.performed += () => Invoke(tool, "MoveFromGrab");
                int gridChanges = 0;
                grid.performed += () => gridChanges++;
                Keys(Key.H); grid.Update(); h.Update();
                Require(gridChanges == 0 && placement.placing && !Get<bool>(tool, "active"), "H did not hand off to Move before the grid handler.");
                Keys(); grid.Update(); h.Update(); placement.CancelPlacement();
                Invoke(tool, "BeginSelection", false);
                Keys(Key.H); h.Update(); grid.Update();
                Require(gridChanges == 0 && placement.placing, "H leaked to the grid when the shortcut handler ran first.");
                Keys(); grid.Update(); h.Update(); placement.CancelPlacement();
                Invoke(tool, "BeginSelection", false);
                h.defaultAction.Disable(); h.rebindAction.action.AddBinding("<Keyboard>/k"); h.rebindAction.action.Enable();
                Keys(Key.K); h.Update();
                Require(placement.placing && !Get<bool>(tool, "active"), "The rebound shortcut did not start Move.");
                Keys(); h.Update(); placement.CancelPlacement();
                Invoke(tool, "BeginSelection", true);
                Keys(Key.K); h.Update();
                Require(!placement.placing && Get<bool>(tool, "active"), "The Move shortcut interrupted rotation.");
                Keys(); h.Update();
                var numeric = Get<TransformNumericInput>(tool, "numericInput");
                numeric.Cycle(false);
                Require(!KeyboardTransformTool.AllowsInput(h), "Numeric entry allowed the Move shortcut.");
                numeric.Close();
                Invoke(tool, "Finish", false);
            }
            finally {
                Keys(); Set(tool, "rotating", false);
                Set(tool, "grabInput", grab); Set(tool, "rotateInput", rotate); Set(tool, "holeSnapInput", holeInput);
            }
        }

        private static void Complete(KeyboardTransformTool tool) => Get<Tween>(tool, "smoothingTween")?.Complete();
        private static void Point(Camera camera, Vector3 point) {
            InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point), InputUpdateType.Dynamic);
            Physics.SyncTransforms();
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
        private static void Near(Vector3 value, Vector3 expected, string message) => Require(Vector3.Distance(value, expected) < .003f,
            $"{message} Actual {value:F5}; expected {expected:F5}.");
        private static void Near(Quaternion value, Quaternion expected, string message) => Require(Quaternion.Angle(value, expected) < .03f, message);
    }
}
