using System;
using System.Linq;
using System.Reflection;
using Protobot;
using InputEvent = Protobot.InputEvents.InputEvent;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class HolePlacementValidation {
        private const string SmokeKey = "Protobot.HolePlacementSmoke";
        private static int frames;

        [InitializeOnLoadMethod]
        private static void ResumeSmoke() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0;
            EditorApplication.update += SmokeTick;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnsureDevices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }

        private static void SmokeTick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= SmokeTick;
            SessionState.SetBool(SmokeKey, false);
            try {
                var placement = Object.FindFirstObjectByType<Placement>();
                var fallback = Object.FindFirstObjectByType<PartHolePlaceDisplacement>();
                var fields = new SerializedObject(fallback);
                var cast = (MouseCast)fields.FindProperty("mouseCast").objectReferenceValue;
                var flip = (InputEvent)fields.FindProperty("flipInput").objectReferenceValue;
                var camera = cast.GetComponent<Camera>();
                var generator = PartsManager.partTypes.Select(t => t.GetComponent<PartGenerator>()).OfType<PlatePartGenerator>().Single();
                generator.param1.value = "4";
                generator.param2.value = "2";
                var target = generator.Generate(new Vector3(300, 300, 300), Quaternion.Euler(20, 40, 10));
                var hole = target.GetComponentsInChildren<HoleCollider>()[0];
                camera.transform.SetPositionAndRotation(hole.transform.position + hole.transform.forward * 5,
                    Quaternion.LookRotation(-hole.transform.forward, hole.transform.up));
                InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(hole.transform.position));
                Physics.SyncTransforms();
                Require(cast.hit.collider != null && cast.hit.collider.gameObject == hole.gameObject, "Pointer did not resolve the target hole.");
                var data = new PartPlacementData(generator, generator.GetComponent<PartType>(), placement.transform);
                flip.IsPressed = false;
                Require(fallback.TryGetDisplacement(data, out var front), "Initial placement did not resolve a hole.");
                flip.IsPressed = true;
                Require(fallback.TryGetDisplacement(data, out var back), "Opposite hole face did not resolve.");
                Require(Vector3.Dot(front.rotation.Forward, back.rotation.Forward) < -.999f, "Space did not flip the attachment direction.");
                flip.IsPressed = false;
                placement.StartPlacing(data);
                typeof(Placement).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(placement, null);
                var previous = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None);
                typeof(Placement).GetMethod("Place", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(placement, null);
                var placed = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None).Single(p => !previous.Contains(p));
                Require(Vector3.Distance(placed.transform.position, front.translation.Position) < .001f &&
                    Quaternion.Angle(placed.transform.rotation, front.rotation.Orientation) < .01f,
                    "Committed placement differs from the hole-snapped target.");
                Require(placement.placing, "Initial placement must support repeated placement.");
                var previousAnchor = data.attachmentPose.position;
                generator.param1.value = "3";
                placement.RefreshPartPreview();
                generator.TryGetPlacementHole(out var expectedAnchor, out _);
                Require(data.attachmentPose.position != previousAnchor && data.attachmentPose.position == expectedAnchor.position,
                    "Preview attachment was not refreshed.");
                Require(!fallback.TryGetDisplacement(new GameObjectPlacementData(target, placement.transform), out _),
                    "Initial placement fallback captured an existing-object move.");
                Debug.Log("Initial hole placement runtime smoke passed: pointer raycast, opposite face, preview rule chain, committed pose, repeated placement, property refresh, and existing-object exclusion.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("Tools/Protobot/Validate Initial Hole Placement")]
        public static void Validate() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var placement = Object.FindFirstObjectByType<Placement>();
            var rules = new SerializedObject(placement).FindProperty("placePositions");
            var fallback = Object.FindFirstObjectByType<PartHolePlaceDisplacement>();
            Require(fallback != null, "Initial hole placement is missing from Main.");
            var serialized = new SerializedObject(fallback);
            foreach (var field in new[] { "mouseCast", "flipInput" })
                Require(serialized.FindProperty(field).objectReferenceValue != null, "Missing reference: " + field);
            Require(rules.GetArrayElementAtIndex(rules.arraySize - 2).objectReferenceValue == fallback &&
                rules.GetArrayElementAtIndex(rules.arraySize - 1).objectReferenceValue is PlanePlaceDisplacement,
                "Initial hole placement must follow hardware rules and precede the plane.");

            PartsManager.LoadPartTypes();
            int checkedParts = 0;
            foreach (var type in PartsManager.partTypes) {
                var generator = type.GetComponent<PartGenerator>();
                if (!generator.TryGetPlacementHole(out _, out _)) continue;
                ValidateGeometry(type, placement.transform);
                checkedParts++;
                // Even hole counts put the object origin between holes.
                if (generator is PlatePartGenerator) {
                    generator.param1.value = "4";
                    generator.param2.value = "2";
                    ValidateGeometry(type, placement.transform);
                }
                if (generator is AluminumPartGenerator) {
                    foreach (var option in generator.GetParam1Options()) {
                        generator.param1.value = option;
                        generator.param2.value = "6";
                        ValidateGeometry(type, placement.transform);
                    }
                }
            }
            Require(checkedParts > 5, "Too few hole-bearing part types were checked.");
            Debug.Log($"Initial hole placement validation passed: Main wiring and rule priority, {checkedParts} part types, even-count plates/channels, all structural profiles, rotated and opposite faces, and attachment refresh.");
        }

        private static void ValidateGeometry(PartType type, Transform preview) {
            var generator = type.GetComponent<PartGenerator>();
            var data = new PartPlacementData(generator, type, preview);
            // Use off-grid targets so quantization cannot hide alignment mistakes.
            var target = new Vector3(3.17f, 4.23f, -2.31f);
            foreach (var rotation in new[] { Quaternion.Euler(31, 72, 17), Quaternion.Euler(31, 72, 17) * Quaternion.Euler(0, 180, 0) }) {
                data.RefreshAttachment();
                var displacement = PartHolePlaceDisplacement.Align(data, target, rotation);
                var part = generator.Generate(displacement.translation.Position, displacement.rotation.Orientation);
                try {
                    var normal = rotation * Vector3.forward;
                    bool matched = part.GetComponentsInChildren<HoleCollider>(true).Any(hole => {
                        var face = hole.transform.position - hole.transform.forward *
                            (hole.transform.TransformVector(Vector3.forward).magnitude / 2f);
                        return Vector3.Distance(face, target) < .001f && Vector3.Dot(hole.transform.forward, normal) > .999f;
                    });
                    Require(matched, "No generated hole mates to the target: " + generator.GetId());
                }
                finally { Object.DestroyImmediate(part); }
            }
        }

        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
