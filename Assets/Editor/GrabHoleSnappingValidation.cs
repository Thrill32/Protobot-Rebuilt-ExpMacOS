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
                Debug.Log("Grab hole snapping smoke passed: shared hierarchy-safe preview geometry, Move/GH screw parity and endpoint tracking without drift, preview cleanup, source/target faces, camera navigation, input priority, rigid alignment, original anchors, undo/redo and fastener grouping.");
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
            foreach (var field in new[] { "holeSnapInput", "oppositeHoleInput", "mouseCast", "holePreview", "moveSourceIcon", "holeMoveMaterial", "orbitInput", "panInput", "noclipInput" })
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
            CheckSingleClickMove(manager, camera, source, target);
            var firstOutline = first.AddComponent<EPOOutline.Outlinable>();
            var secondOutline = second.AddComponent<EPOOutline.Outlinable>();
            secondOutline.enabled = false;
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
                CheckMovePresentation(tool, first, second, targetBody);
                Snap(tool, camera, target, false);
                Near(source.transform.position - source.transform.forward * .1f,
                    target.transform.position - target.transform.forward * .1f, "Source face missed the target face.");
                Require(Vector3.Dot(-source.transform.forward, -target.transform.forward) < -.999f, "Source face did not face the target.");
                Near(Quaternion.Inverse(first.transform.rotation) * (second.transform.position - first.transform.position), relative, "Rigid spacing changed.");
                Require(first.transform.parent == multi.pivot.transform && second.transform.parent == multi.pivot.transform,
                    "Hole snapping changed MultiPivot parenting.");
                Snap(tool, camera, target, false); // Moving geometry now covers the target in the ray.
                Require(Get<bool>(tool, "hasMoveTarget"), "Moving colliders blocked the target hole.");
                Snap(tool, camera, target, true);
                Near(source.transform.position - source.transform.forward * .1f,
                    target.transform.position + target.transform.forward * .1f, "Opposite target face failed.");
                CheckGrabRotation(tool, camera, target.transform.position);

                Keys();
                var snappedPosition = first.transform.position;
                var snappedRotation = first.transform.rotation;
                Invoke(tool, "ToggleHoleSnap");
                Require(Get<object>(tool, "movePreview") == null && first.GetComponent<Renderer>().enabled &&
                    second.GetComponent<Renderer>().enabled, "H-to-free did not restore the real renderers.");
                Require(firstOutline.enabled && !secondOutline.enabled, "H-to-free changed the original outline states.");
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
                Require(Get<object>(tool, "movePreview") != null, "A preselected hole did not get a Move preview.");
                Keys();
                Point(camera, target.transform.position);
                Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                StateSystem.states = new List<State> { new(new List<IElement>()) };
                StateSystem.curIndex = 0;
                Invoke(tool, "Finish", true);
                Require(Get<bool>(tool, "settling") && Get<object>(tool, "movePreview") != null &&
                    !first.GetComponent<Renderer>().enabled, "Confirmation removed the preview before its easing finished.");
                Complete(tool);
                Require(StateSystem.states.Count == 2 && !Get<bool>(tool, "active"), "Hole snap must commit as one undo step.");
                Require(Get<object>(tool, "movePreview") == null && first.GetComponent<Renderer>().enabled &&
                    second.GetComponent<Renderer>().enabled, "Confirmation did not restore the real renderers.");
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
                CheckSharedGeometry(tool);
                CheckScrewTargets(tool, manager, camera);
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

        private static void CheckGrabRotation(KeyboardTransformTool tool, Camera camera, Vector3 targetPoint) {
            var before = new Pose(Get<Vector3>(tool, "origin") + Get<Vector3>(tool, "targetOffset"), Get<Quaternion>(tool, "targetRotation"));
            var target = Get<MoveTarget>(tool, "currentMoveTarget");
            Invoke(tool, "Rotate");
            var adjustment = Get<MoveRotation>(tool, "moveRotation");
            Require(adjustment.Active && adjustment.Attachment.HasValue, "R did not rotate G/H around its target.");
            Near(adjustment.Axis, target.face.rotation * Vector3.forward, "G/H used the wrong target axis.");
            MoveRotationValidation.TurnPointer(camera, adjustment, () => Invoke(tool, "Apply", (Vector2)MouseInput.Position));
            Complete(tool);
            var delta = Quaternion.AngleAxis(adjustment.Degrees, adjustment.Axis);
            var expected = MoveRotation.Around(before, target.face.position, delta);
            Near(Get<Vector3>(tool, "origin") + Get<Vector3>(tool, "targetOffset"), expected.position, "G/H rotated around the wrong pivot.");
            Near(Get<Quaternion>(tool, "targetRotation"), expected.rotation, "G/H rotation did not use the shared placement gesture.");
            Require(Mathf.Abs(adjustment.Degrees) > 80 && Get<bool>(tool, "holeSnapping"), "G/H rotation discarded hole mode.");
            Invoke(tool, "Rotate");
            Require(!adjustment.Active, "R did not resume G/H positioning.");
            Point(camera, targetPoint);
            Invoke(tool, "Apply", (Vector2)MouseInput.Position);
            Complete(tool);
            Near(Get<Quaternion>(tool, "targetRotation"), expected.rotation, "G/H retargeting discarded its twist.");

            // A second rotation can finish with no target under the pointer,
            // rotating about the camera's nearest world axis at the current pose.
            InputState.Change(Mouse.current.position, new Vector2(750, 550), InputUpdateType.Dynamic);
            Invoke(tool, "Apply", (Vector2)MouseInput.Position);
            Require(!Get<bool>(tool, "hasMoveTarget"), "Free-rotation probe still targets geometry.");
            Invoke(tool, "Rotate");
            Require(adjustment.Active && !adjustment.Attachment.HasValue, "G/H without a target did not start free rotation.");
            Near(adjustment.Axis, TransformGesture.PrimaryAxis(camera.transform.forward), "Free G/H rotation used the wrong camera axis.");
            Invoke(tool, "Rotate");
        }

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

        private static void CheckScrewTargets(KeyboardTransformTool tool, SelectionManager manager, Camera camera) {
            var previous = manager.current;
            var cameraPosition = camera.transform.position;
            var cameraRotation = camera.transform.rotation;
            var center = new Vector3(1300, -1000, 1000);
            camera.transform.SetPositionAndRotation(center + Vector3.back * 20, Quaternion.identity);
            var body = Body("Screw snapped body", center + new Vector3(-3, 2, 0));
            var source = Hole(body, Vector3.zero);
            var marker = New("Screw snap source");
            marker.tag = "Hole";
            marker.AddComponent<MeshFilter>();
            var face = marker.AddComponent<HoleFace>();
            face.Set(source.holeData, Vector3.back);
            var screwObject = PartsManager.GeneratePart("SCRW-1.00in", center, Quaternion.Euler(20, 35, 12));
            temporary.Add(screwObject);
            var screw = screwObject.GetComponent<Screw>();
            camera.transform.position = center + screw.transform.forward * 20;
            camera.transform.LookAt(center);
            var support = Body("Stationary screw stack", center - screw.transform.forward * .45f);
            support.transform.rotation = screw.transform.rotation;
            var bore = Hole(support, Vector3.zero);
            var legacy = Get<Placement>(tool, "placement").GetComponent<HolePlaceDisplacement>();
            var previousOpposite = Get<InputEvent>(legacy, "oppositeHoleInput");
            var previousCast = Get<MouseCast>(legacy, "mouseCast");
            Set(legacy, "oppositeHoleInput", Get<InputEvent>(tool, "oppositeHoleInput"));
            Set(legacy, "mouseCast", Get<MouseCast>(tool, "mouseCast"));
            manager.current = new HoleSelection { gameObject = marker, holeData = source.holeData, holeFace = face, faceDirection = Vector3.back };
            var start = body.transform.position;
            try {
                Keys();
                Invoke(tool, "BeginSelection", false);
                Invoke(tool, "ToggleHoleSnap");
                Point(camera, screw.transform.position);
                support.SetActive(false);
                Physics.SyncTransforms();
                Require(MoveTargeting.TryRaycast(Get<MouseCast>(tool, "mouseCast").ray,
                    Get<MouseCast>(tool, "mouseCast").layerMask, false, true, null, out var bare), "Bare screw is not targetable.");
                Near(bare.face.position, screw.transform.position, "Bare screw did not use its original head anchor.");
                support.SetActive(true);
                var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                temporary.Add(blocker);
                blocker.transform.position = center + screw.transform.forward;
                Point(camera, screw.transform.position);
                Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                Require(!Get<bool>(tool, "hasMoveTarget"), "A screw behind an ordinary body remained targetable.");
                blocker.SetActive(false);
                Physics.SyncTransforms();
                Require(MoveTargeting.TryRaycast(Get<MouseCast>(tool, "mouseCast").ray,
                    Get<MouseCast>(tool, "mouseCast").layerMask, false, true, null, out var screwTarget) && screwTarget.screw == screw,
                    "Screw fixture does not expose the screw head to the pointer.");
                var probeRay = new Ray(screw.transform.position + screw.transform.forward * .1f, -screw.transform.forward);
                var probeHits = Physics.RaycastAll(probeRay, screw.ScrewLength);
                Near(screwTarget.face.position, bore.transform.position - bore.transform.forward * .1f,
                    $"Screw did not find the stationary endpoint (length {screw.ScrewLength}, hits {string.Join(", ", probeHits.Select(hit => hit.collider.name))}).");
                Require(legacy.TryGetDisplacement(new GameObjectPlacementData(marker, legacy.transform), out var move),
                    "Legacy Move did not resolve the screw through the shared target resolver.");
                var rotation = move.rotation.Orientation * Quaternion.Inverse(face.Rotation);
                var expectedPosition = move.translation.Position + rotation * (body.transform.position - face.position);
                var expectedRotation = rotation * body.transform.rotation;
                for (int i = 0; i < 4; i++) {
                    Point(camera, screw.transform.position);
                    Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                    Require(Get<bool>(tool, "hasMoveTarget"), "G/H failed to acquire a screw target.");
                    Complete(tool);
                    Physics.SyncTransforms();
                    Near(body.transform.position, expectedPosition, "Screw endpoint drifted as moving holes entered its stack.");
                    Near(body.transform.rotation, expectedRotation, "G/H screw alignment differs from Move.");
                    Near(source.transform.position - source.transform.forward * .1f,
                        bore.transform.position - bore.transform.forward * .1f, "Screw snap missed the last stationary hole's outer face.");
                }
                var tracker = Get<GameObject>(Get<object>(tool, "targetPreview"), "screwTracker");
                Require(tracker.activeSelf && tracker.GetComponentsInChildren<Collider>().Length == 0,
                    "Screw tracker is missing or intercepts input.");
                Near(tracker.transform.position, move.translation.Position, "Screw tracker is not at the actual endpoint.");
                CheckGrabRotation(tool, camera, screw.transform.position);
                Invoke(tool, "ToggleHoleSnap");
                Require(!tracker.activeSelf, "Leaving H mode left a screw tracker visible.");
                Invoke(tool, "ToggleHoleSnap");
                support.transform.position -= screw.transform.forward * .15f;
                Physics.SyncTransforms();
                Point(camera, screw.transform.position);
                Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                Complete(tool);
                Near(tracker.transform.position, bore.transform.position - bore.transform.forward * .1f,
                    "Screw tracker did not refresh after the stationary stack changed.");
                Keys(Key.Space);
                Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                Complete(tool);
                Near(tracker.transform.position, bore.transform.position - bore.transform.forward * .1f,
                    "Opposite-hole input changed the screw endpoint.");
                Keys();
                Invoke(tool, "Finish", false);
                Near(body.transform.position, start, "Cancel did not restore a screw-snapped body.");
                Require(!tracker.activeSelf && body.GetComponent<Renderer>().enabled, "Cancel left screw presentation active.");

                Invoke(tool, "BeginSelection", false);
                Invoke(tool, "ToggleHoleSnap");
                Point(camera, screw.transform.position);
                Invoke(tool, "Apply", (Vector2)MouseInput.Position);
                Complete(tool);
                StateSystem.states = new List<State> { new(new List<IElement>()) };
                StateSystem.curIndex = 0;
                Invoke(tool, "Rotate");
                var rotationEntry = Get<MoveRotation>(tool, "moveRotation");
                var beforeNumeric = new Pose(Get<Vector3>(tool, "origin") + Get<Vector3>(tool, "targetOffset"), Get<Quaternion>(tool, "targetRotation"));
                Keys(Key.Slash); HandleInput(tool); Keys();
                Require(rotationEntry.NumericInput.IsOpen && !KeyboardTransformTool.AllowsInput(Get<InputEvent>(tool, "rotateInput")) &&
                    KeyboardTransformTool.BlocksCameraNavigation, "G/H slash entry did not capture input.");
                MoveRotationValidation.TypeText("-22.5");
                Require(rotationEntry.NumericInput.Text == "-22.5", "G/H rotation did not receive numeric text.");
                var expectedNumeric = MoveRotation.Twist(beforeNumeric, rotationEntry.Attachment.Value, -22.5f);
                Keys(Key.Enter); HandleInput(tool); Complete(tool); Keys();
                Near(Get<Quaternion>(tool, "targetRotation"), expectedNumeric.rotation, "G/H did not apply the exact entered rotation.");
                var finalPosition = body.transform.position;
                var finalRotation = body.transform.rotation;
                Require(StateSystem.states.Count == 2 && !tracker.activeSelf, "Screw snap did not commit as one operation and clear its tracker.");
                StateSystem.instance.Undo(); body.transform.DOComplete();
                Near(body.transform.position, start, "Undo did not restore the screw-snapped source.");
                StateSystem.instance.Redo(); body.transform.DOComplete();
                Near(body.transform.position, finalPosition, "Redo lost the screw endpoint.");
                Near(body.transform.rotation, finalRotation, "Redo lost the screw alignment.");
                Near(screw.transform.position, center, "Screw snap or undo/redo moved the stationary screw.");

                manager.current = new ObjectSelection { gameObject = body };
                Invoke(tool, "BeginSelection", false);
                Invoke(tool, "ToggleHoleSnap");
                Point(camera, screw.transform.position);
                Require(!(bool)Invoke(tool, "PickSourceHole"), "Source picking accepted an outside screw as a source hole.");
                Invoke(tool, "Finish", false);
            }
            finally {
                Invoke(tool, "Finish", false);
                Set(legacy, "oppositeHoleInput", previousOpposite);
                Set(legacy, "mouseCast", previousCast);
                Invoke(legacy, "LateUpdate");
                manager.current = previous;
                camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
                Keys();
            }
        }

        private static void CheckMovePresentation(KeyboardTransformTool tool, GameObject first, GameObject second, GameObject target) {
            var presentation = Get<object>(tool, "movePreview");
            Require(presentation != null, "Source selection did not show the Move preview.");
            var preview = Get<GameObject>(presentation, "preview");
            var renderer = preview.GetComponent<MeshRenderer>();
            var icon = Get<UnityEngine.UI.Image>(presentation, "sourceIcon");
            Require(!first.GetComponent<Renderer>().enabled && !second.GetComponent<Renderer>().enabled &&
                target.GetComponent<Renderer>().enabled, "Move preview must replace only the moving bodies.");
            Require(!first.GetComponent<EPOOutline.Outlinable>().enabled && !second.GetComponent<EPOOutline.Outlinable>().enabled,
                "The solid bodies' outlines must not overlay the Move preview.");
            Require(preview.GetComponentsInChildren<Collider>().Length == 0 && !icon.raycastTarget &&
                icon.GetComponentInParent<UnityEngine.UI.GraphicRaycaster>() == null, "Preview visuals must not intercept source/target input.");
            Require(icon.sprite == Get<UnityEngine.UI.Image>(tool, "moveSourceIcon").sprite, "Source marker does not use Move's icon.");
            Near(preview.transform.position, Get<Vector3>(tool, "origin") + Get<Vector3>(tool, "displayedOffset"),
                "Move preview is not anchored to the source face.");
            var expected = first.GetComponent<Renderer>().bounds;
            expected.Encapsulate(second.GetComponent<Renderer>().bounds);
            Near(renderer.bounds.center, expected.center, "Combined preview changed the rigid set's center.");
            Near(renderer.bounds.size, expected.size, "Combined preview lost part geometry.");
            Require(renderer.sharedMaterial.GetColor("_BaseColor").a < .5f && renderer.sharedMaterial.GetFloat("_ZWrite") == 0,
                "Move preview is not translucent.");
            var placement = Get<Placement>(tool, "placement");
            Require(placement.GetComponent<Renderer>().sharedMaterial.GetFloat("_Surface") == 0,
                "Grab changed the shared placement material.");
            var style = preview.GetComponent<EPOOutline.Outlinable>();
            var template = placement.GetComponent<EPOOutline.Outlinable>();
            Require(style.OutlineParameters.FillPass.Shader == template.OutlineParameters.FillPass.Shader &&
                style.OutlineParameters.FillPass.Material != null, "Move's animated fill was not carried across.");
        }

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
            Require(Get<object>(tool, "movePreview") == null && source.holeData.part.GetComponent<Renderer>().enabled,
                "Cancel did not restore the source renderer.");
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
            Require(Get<bool>(tool, "hasMoveTarget"), "Target hole was not resolved.");
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
        private static void Near(Vector3 value, Vector3 expected, string message) => Require(Vector3.Distance(value, expected) < .003f,
            $"{message} Actual {value:F5}; expected {expected:F5}.");
        private static void Near(Quaternion value, Quaternion expected, string message) => Require(Quaternion.Angle(value, expected) < .03f, message);
    }
}
