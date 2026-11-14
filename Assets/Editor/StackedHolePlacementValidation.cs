using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Protobot;
using Protobot.SelectionSystem;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class StackedHolePlacementValidation {
        private const string SmokeKey = "Protobot.StackedHolePlacement";
        private static int frames;

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0; EditorApplication.update += Tick;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        private static void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                Check();
                Debug.Log("Stacked hole placement passed: contiguous coaxial stacks, oblique views, reversed axes, gaps/misalignment, excluded geometry, shared preview/placement and source-selection boundaries.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static void Check() {
            var origin = new Vector3(1300, 900, 700);
            var rotation = Quaternion.Euler(31, 47, 19);
            var axis = rotation * Vector3.forward;
            var right = rotation * Vector3.right;
            var first = Hole("Front plate", origin, rotation, .2f);
            var middle = Hole("Middle plate", origin + axis * .25f, rotation * Quaternion.Euler(0, 180, 0), .3f);
            var last = Hole("Back plate", origin + axis * .45f, rotation, .1f);
            var distant = Hole("Separate plate", origin + axis * 2, rotation, .2f);
            var bodies = new[] { first, middle, last, distant }.Select(h => h.holeData.part).ToArray();
            int mask = LayerMask.GetMask("Default", "HoleCollisions");
            var ray = new Ray(origin - axis * 5 + right * 5, (axis - right).normalized);
            try {
                Physics.SyncTransforms();
                Require(MoveTargeting.TryRaycast(ray, mask, false, false, null, out var near) && near.hole == first.holeData,
                    "The oblique pointer must hit the front hole first.");
                Near(near.face.position, origin - axis * .1f, "Unmodified targeting moved off the front face.");
                Require(MoveTargeting.TryRaycast(ray, mask, true, false, null, out var far) && far.hole == last.holeData,
                    "Space selected an interior face instead of the outside of the three-part stack.");
                Near(far.face.position, origin + axis * .5f, "Opposite placement did not clear the full stack.");
                Require(Vector3.Dot(far.face.rotation * Vector3.forward, axis) > .999f, "The stack's far face points inward.");
                var reverse = new Ray(last.transform.position + axis * 5 - right * 5, (-axis + right).normalized);
                Require(MoveTargeting.TryRaycast(reverse, mask, true, false, null, out var back) && back.hole == first.holeData,
                    "Opposite targeting did not traverse the stack from its other side.");
                Near(back.face.position, origin - axis * .1f, "Reverse stack traversal picked an interior face.");

                middle.transform.parent.position += right * .03f;
                ExpectFirst("A laterally misaligned hole bridged the stack.");
                middle.transform.parent.position -= right * .03f;
                middle.transform.parent.rotation = rotation * Quaternion.Euler(0, 90, 0);
                ExpectFirst("A perpendicular hole bridged the stack.");
                middle.transform.parent.rotation = rotation * Quaternion.Euler(0, 180, 0);
                middle.transform.parent.gameObject.SetActive(false);
                ExpectFirst("An inactive hole bridged the stack.");
                middle.transform.parent.gameObject.SetActive(true);
                middle.enabled = false;
                ExpectFirst("A disabled hole bridged the stack.");
                middle.enabled = true;
                middle.holeData.part.layer = Placement.PLACEMENT_LAYER;
                ExpectFirst("Moving placement geometry bridged the stack.");
                middle.holeData.part.layer = LayerMask.NameToLayer("Deleted");
                ExpectFirst("Deleted geometry bridged the stack.");
                middle.holeData.part.layer = 0;
                Physics.SyncTransforms();
                Require(MoveTargeting.TryRaycast(ray, mask, true, false, t => t.IsChildOf(middle.holeData.part.transform), out var ignored) &&
                    ignored.hole == first.holeData, "Ignored moving geometry extended the target stack.");
                Require(MoveTargeting.TryRaycast(ray, mask, true, false, null, out var source,
                    t => t.IsChildOf(first.holeData.part.transform) || t.IsChildOf(middle.holeData.part.transform)) && source.hole == middle.holeData,
                    "Source picking traversed outside the moving selection.");
                Require(MoveTargeting.TryRaycast(ray, mask, true, false, null, out var blocked,
                    t => t.IsChildOf(last.holeData.part.transform)) && blocked.hole == first.holeData,
                    "A source behind a stationary foreground hole became selectable.");
                CheckPlacementPaths(first, middle, last, ray);
            }
            finally { foreach (var body in bodies) if (body != null) Object.DestroyImmediate(body); }

            void ExpectFirst(string message) {
                Physics.SyncTransforms();
                Require(MoveTargeting.TryRaycast(ray, mask, true, false, null, out var result) && result.hole == first.holeData, message);
                Near(result.face.position, origin + axis * .1f, message);
            }
        }

        private static void CheckPlacementPaths(HoleCollider first, HoleCollider middle, HoleCollider last, Ray ray) {
            var placement = Object.FindFirstObjectByType<Placement>();
            var fallback = Object.FindFirstObjectByType<PartHolePlaceDisplacement>();
            var rule = Object.FindFirstObjectByType<HolePlaceDisplacement>();
            var cast = Get<MouseCast>(fallback, "mouseCast");
            var camera = cast.GetComponent<Camera>();
            var cameraPose = new Pose(camera.transform.position, camera.transform.rotation);
            var flip = Get<InputEvent>(fallback, "flipInput");
            bool defaultEnabled = flip.defaultAction.enabled, rebindEnabled = flip.rebindAction.action.enabled;
            var hoverResponse = Object.FindObjectsByType<HoleFaceSelectionResponse>(FindObjectsSortMode.None)
                .Single(r => Get<MouseCast>(r, "mouseCast") == cast);
            var hover = Get<HoleFace>(hoverResponse, "holeFace");
            var manager = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            var click = manager.GetComponent<ClickSelector>();
            var move = Get<ToolToggle>(click, "moveTool");
            bool moveToggled = move.toggled;
            var tool = placement.GetComponent<KeyboardTransformTool>();
            var toolCamera = Get<Camera>(tool, "refCamera");
            var source = Hole("Moving source", first.transform.position + first.transform.right * 6, first.transform.rotation, .2f);
            var marker = new GameObject("Stack test source face", typeof(MeshFilter)).AddComponent<HoleFace>();
            MultiSelection multi = null;
            try {
                camera.transform.SetPositionAndRotation(ray.origin, Quaternion.LookRotation(ray.direction, first.transform.up));
                typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
                InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(first.transform.position), InputUpdateType.Dynamic);
                flip.rebindAction.action.Disable(); flip.defaultAction.Enable();
                InputState.Change(Keyboard.current, new KeyboardState(Key.Space), InputUpdateType.Dynamic); flip.Update();
                Physics.SyncTransforms();
                Require(cast.hit.collider == first.GetComponent<Collider>(), "The placement test pointer missed the front hole.");
                Require(flip.IsPressed && flip.IsCurrentlyPressed, "The placement test did not hold the Space binding.");
                var expected = MoveTargeting.HoleFace(last.holeData, first.transform.forward);
                hoverResponse.OnSet(new ObjectSelection { gameObject = first.gameObject });
                Require(hover.hole == last.holeData, "The hover marker still highlights an interior hole.");
                Near(hover.position, expected.position, "The hover marker missed the outside face.");
                var generator = PartsManager.GetPartType("PLTE").GetComponent<PartGenerator>();
                var part = new PartPlacementData(generator, generator.GetComponent<PartType>(), placement.transform);
                Require(fallback.TryGetDisplacement(part, out var initial), "Catalog placement lost the stack target.");
                var sourceFace = part.attachmentPose.position - part.attachmentPose.rotation * Vector3.forward * (part.attachmentDepth * .5f);
                Near(initial.translation.Position + initial.rotation.Orientation * sourceFace, expected.position,
                    "Catalog placement would embed its mating face inside the stack.");
                var screw = PartsManager.GetPartType("SCRW").GetComponent<PartGenerator>();
                Require(Object.FindFirstObjectByType<ScrewPlaceDisplacement>().TryGetDisplacement(
                    new PartPlacementData(screw, screw.GetComponent<PartType>(), placement.transform), out var hardware),
                    "Screw placement lost the far stack face.");
                Near(hardware.translation.Position, expected.position, "Screw placement used an interior face.");

                marker.Set(source.holeData, source.transform.forward);
                Require(rule.TryGetDisplacement(new GameObjectPlacementData(marker.gameObject, placement.transform, true), out var moved),
                    "Move placement lost the far stack face.");
                Near(moved.translation.Position, expected.position, "Move placement used an interior face.");
                var sourceSelection = new HoleSelection { gameObject = marker.gameObject, holeData = source.holeData,
                    holeFace = marker, faceDirection = source.transform.forward, individual = true };
                using (var copy = SelectionPlacementData.Capture(sourceSelection, placement.transform, Object.FindFirstObjectByType<ObjectDuplicator>())) {
                    Require(rule.TryGetDisplacement(copy, out var stamped), "P placement lost the far stack face.");
                    var copies = copy.Generate(stamped.translation.Position, stamped.rotation.Orientation);
                    var copiedHole = copies.Single().GetComponentInChildren<HoleCollider>();
                    Near(MoveTargeting.HoleFace(copiedHole.holeData, copiedHole.transform.forward).position,
                        expected.position, "A copied source face was placed inside the stack.");
                    ObjectDuplicator.DiscardCopies(copies);
                }

                // A Space-click resolves a different hole than the physical hit;
                // single-click Move must use that resolved face and still start.
                manager.SetCurrent(new ObjectSelection { gameObject = first.holeData.part, individual = true }, true, true);
                hoverResponse.OnSet(new ObjectSelection { gameObject = first.gameObject });
                move.toggled = true;
                Invoke(click, "OnPerformInput");
                Require(placement.placing && manager.current is HoleSelection selected && selected.holeData == last.holeData,
                    "Space-clicking a stack failed to start Move from its outside face.");
                placement.CancelPlacement(); move.toggled = false;
                manager.ClearCurrent(false, true, true);

                // G/H is the same Move entry point for the existing selection.
                Set(tool, "refCamera", camera);
                manager.SetCurrent(sourceSelection, true, true);
                Invoke(tool, "BeginSelection", false); Invoke(tool, "MoveFromGrab");
                var shortcutData = Get<PlacementData>(placement, "currentPlacementData") as GameObjectPlacementData;
                Require(placement.placing && shortcutData != null && shortcutData.GetGameObject() == marker.gameObject,
                    "G/H did not reuse the selected source face for Move.");
                Require(rule.TryGetDisplacement(shortcutData, out var shortcutPose), "G/H lost the Move stack target.");
                Near(shortcutPose.translation.Position, expected.position, "G/H used a different stack target than Move.");
                placement.CancelPlacement(); manager.ClearCurrent(false, true, true);
                multi = new MultiSelection(null, new List<GameObject> { source.holeData.part, middle.holeData.part }) {
                    individual = true,
                    placementHole = SelectedHoleAnchor.From(sourceSelection)
                };
                manager.SetCurrent(multi, true, true);
                Invoke(tool, "BeginSelection", false); Invoke(tool, "MoveFromGrab");
                shortcutData = Get<PlacementData>(placement, "currentPlacementData") as GameObjectPlacementData;
                Require(placement.placing && shortcutData?.GetGameObject() == multi.gameObject && shortcutData.individual &&
                    shortcutData.sourceHole?.Hole == source.holeData,
                    "G/H did not retain the additive selection's concrete source hole and grouping scope.");

            }
            finally {
                Invoke(tool, "Finish", false);
                placement.CancelPlacement(); manager.ClearCurrent(false, true, true);
                move.toggled = moveToggled;
                Set(tool, "refCamera", toolCamera);
                camera.transform.SetPositionAndRotation(cameraPose.position, cameraPose.rotation);
                InputState.Change(Keyboard.current, new KeyboardState(), InputUpdateType.Dynamic); flip.Update();
                if (!defaultEnabled) flip.defaultAction.Disable();
                if (rebindEnabled) flip.rebindAction.action.Enable();
                hover.gameObject.SetActive(false);
                if (multi != null) Object.DestroyImmediate(multi.gameObject);
                Object.DestroyImmediate(marker.gameObject); Object.DestroyImmediate(source.holeData.part);
            }
        }

        private static HoleCollider Hole(string name, Vector3 position, Quaternion rotation, float depth) {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube); body.name = name;
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetPositionAndRotation(position, rotation);
            body.AddComponent<SavedObject>().id = "PLTE-3-3";
            var obj = GameObject.CreatePrimitive(PrimitiveType.Cube); obj.name = "Stack validation hole";
            Object.DestroyImmediate(obj.GetComponent<Collider>());
            obj.transform.SetParent(body.transform, false);
            obj.transform.localScale = new Vector3(.4f, .4f, depth);
            obj.layer = HoleCollider.HOLE_COLLISIONS_LAYER; obj.tag = "HoleCollider";
            obj.AddComponent<MeshCollider>().sharedMesh = obj.GetComponent<MeshFilter>().sharedMesh;
            return obj.AddComponent<HoleCollider>();
        }
        private static void Near(Vector3 value, Vector3 expected, string message) => Require(Vector3.Distance(value, expected) < .002f,
            $"{message} Actual {value:F5}; expected {expected:F5}.");
        private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static object Invoke(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
