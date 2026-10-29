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

        private static void SmokeTick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= SmokeTick;
            SessionState.SetBool(SmokeKey, false);
            try {
                MoveRotationValidation.Keys();
                MoveRotationValidation.CheckUIEnterRouting(blocked: false);
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
                MoveRotationValidation.CheckPlacement(placement, camera, cast, data, hole);
                ValidateFreePlacement(placement, camera, cast);
                ValidateMotorShaftInsertion();
                Debug.Log("Initial hole placement runtime smoke passed: pointer raycast, opposite face, preview rule chain, committed pose, repeated placement, property refresh, existing-object exclusion, corrected free-placement previews/commits for both rail types and sensor/motor variants, and normal/high-strength shaft insertion into both motor sockets.");
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
            Require(rules.GetArrayElementAtIndex(rules.arraySize - 3).objectReferenceValue == fallback &&
                rules.GetArrayElementAtIndex(rules.arraySize - 2).objectReferenceValue is Protobot.Simulation.GearPlaceDisplacement &&
                rules.GetArrayElementAtIndex(rules.arraySize - 1).objectReferenceValue is PlanePlaceDisplacement,
                "Initial hole placement must follow hardware rules and precede gear vicinity snapping and the plane.");

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
            foreach (var id in FlippedVariants) {
                var type = PartsManager.GetPartType(id);
                var generator = type.GetComponent<PartGenerator>();
                PartParameterEditing.Load(generator, id);
                Require(generator.FlipPlacement, "Missing orientation correction: " + id);
                ValidateGeometry(type, placement.transform);
            }
            ValidatePivot("MOTR-11W", new Vector3(0, 0, 1.5f));
            ValidatePivot("MOTR-5.5W", new Vector3(.125f, 0, 1.291f));
            ValidatePivot("BLCK-High Strength", Vector3.zero);
            ValidateMotorMounts("MOTR-11W");
            ValidateMotorMounts("MOTR-5.5W");
            Debug.Log($"Initial hole placement validation passed: Main wiring and rule priority, {checkedParts} part types, all structural profiles, reported mechanism/slide/sensor variants, both rail types above the mounting surface, rotated/opposite mating faces, free-placement defaults, motor/bearing pivot ownership, and both motor screw holes on the half-inch pattern independently of their shaft sockets.");
        }

        private static readonly string[] FlippedVariants = {
            "UCHL-2x2-20", "RAILS-2x25", "RAILS-2x35", "CCHL-1x2-5", "CCHL-1x3-5", "CCHL-1x5-5",
            "ANGL-1x1-5", "ANGL-2x2-5", "ANGL-3x3-5", "MCMS-Bevel Gear-16T",
            "MCMS-Bevel Gear-32T", "MCMS-Rack-", "MCMS-Hand Crank-", "LMTK-Inner Slide Trunk",
            "SNSR-Vision-V5", "SNSR-Potentiometer-V5", "MOTR-11W", "MOTR-5.5W"
        };

        private static void ValidatePivot(string id, Vector3 expected) {
            var gen = PartsManager.GetPartType(id).GetComponent<PartGenerator>();
            PartParameterEditing.Load(gen, id);
            Require(gen.TryGetPartData(out var data) && data.primaryHole != null &&
                data.primaryHole.transform.IsChildOf(data.transform), "Primary hole belongs to another variant: " + id);
            Require(gen.TryGetPlacementHole(out var pose, out var depth), "Missing pivot: " + id);
            Require(Vector3.Distance(pose.position, expected) < .001f, "Incorrect local pivot: " + id);
            var mountingHole = data.placementHole != null ? data.placementHole : data.primaryHole;
            Require(mountingHole.transform.IsChildOf(data.transform), "Mounting hole belongs to another variant: " + id);
            Require(Mathf.Abs(depth - mountingHole.transform.TransformVector(Vector3.forward).magnitude) < .001f,
                "Incorrect mounting hole depth: " + id);
        }

        private static void ValidateMotorMounts(string id) {
            var type = PartsManager.GetPartType(id);
            var gen = type.GetComponent<PartGenerator>();
            PartParameterEditing.Load(gen, id);
            Require(gen.TryGetPartData(out var data) && data.placementHole != null &&
                data.placementHole != data.primaryHole && data.placementHole.holeType == HoleCollider.HoleType.Threaded,
                "Motor placement must use a screw hole independently of the shaft socket: " + id);
            var partData = new PartPlacementData(gen, type, null);
            var target = new Vector3(3.17f, 4.23f, -2.31f);
            foreach (var rotation in new[] { Quaternion.Euler(31, 72, 17), Quaternion.Euler(31, 72, 17) * Quaternion.Euler(0, 180, 0) }) {
                var displacement = PartHolePlaceDisplacement.Align(partData, target, rotation);
                var part = gen.Generate(displacement.translation.Position, displacement.rotation.Orientation);
                try {
                    var screws = part.GetComponentsInChildren<HoleCollider>().Where(h => h.holeType == HoleCollider.HoleType.Threaded).ToArray();
                    Require(screws.Length == 2, "Expected two motor mounting holes: " + id);
                    var faces = screws.Select(h => h.transform.position + h.transform.forward *
                        (h.transform.TransformVector(Vector3.forward).magnitude / 2f)).ToArray();
                    Require(faces.Any(p => Vector3.Distance(p, target) < .001f) &&
                        faces.Any(p => Vector3.Distance(p, target + rotation * Vector3.right * .5f) < .001f),
                        "Both motor screw holes must mate to the structural half-inch hole pattern: " + id);
                    var socket = (HoleCollider)new SerializedObject(part.GetComponent<Motor>()).FindProperty("shaftHole").objectReferenceValue;
                    Require(socket != null && !screws.Contains(socket) && socket.transform.IsChildOf(part.transform),
                        "Shaft insertion socket must remain independent of the screw holes: " + id);
                }
                finally { Object.DestroyImmediate(part); }
            }
        }

        private static void ValidateMotorShaftInsertion() {
            foreach (var id in new[] { "MOTR-11W", "MOTR-5.5W" }) {
                var gen = PartsManager.GetPartType(id).GetComponent<PartGenerator>();
                PartParameterEditing.Load(gen, id);
                var part = gen.Generate(new Vector3(800, 800, 800), Quaternion.Euler(23, 47, 91));
                var motor = part.GetComponent<Motor>();
                var socket = (HoleCollider)new SerializedObject(motor).FindProperty("shaftHole").objectReferenceValue;
                foreach (bool highStrength in new[] { false, true }) {
                    var shaft = motor.GetShaftDisplacement(6, highStrength);
                    var expected = socket.transform.position + socket.transform.forward * (3 - (highStrength ? .2775f : .47f));
                    Require(shaft != null && Vector3.Distance(shaft.translation.Position, expected) < .001f,
                        "Shaft insertion used a mounting hole: " + id);
                }
            }
        }

        private static void ValidateFreePlacement(Placement placement, Camera camera, MouseCast cast) {
            camera.transform.SetPositionAndRotation(new Vector3(700, 700, 700), Quaternion.identity);
            InputState.Change(Mouse.current.position, new Vector2(Screen.width / 2f, Screen.height / 2f));
            Physics.SyncTransforms();
            Require(cast.hit.collider == null, "Free-placement ray must miss existing objects.");
            foreach (var id in FlippedVariants.Concat(new[] { "RAIL-Rail-24", "BLCK-High Strength", "MCMS-Cam Follower-" })) {
                var type = PartsManager.GetPartType(id);
                var gen = type.GetComponent<PartGenerator>();
                PartParameterEditing.Load(gen, id);
                var data = new PartPlacementData(gen, type, placement.transform);
                placement.transform.rotation = Quaternion.Euler(13, 27, 41);
                placement.StartPlacing(data);
                var expected = FlippedVariants.Contains(id) ? Quaternion.Euler(180, 0, 0) : Quaternion.identity;
                Require(Quaternion.Angle(placement.transform.rotation, expected) < .01f, "Inherited starting rotation: " + id);
                typeof(Placement).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(placement, null);
                var displacement = (Protobot.Transformations.Displacement)typeof(Placement).GetField("currentDisplacement", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(placement);
                Require(Quaternion.Angle(displacement.rotation.Orientation, expected) < .01f, "Incorrect free-plane target: " + id);
                var previous = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None);
                typeof(Placement).GetMethod("Place", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(placement, null);
                var placed = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None).Single(p => !previous.Contains(p));
                Require(Quaternion.Angle(placed.transform.rotation, expected) < .01f, "Incorrect free-placement commit: " + id);
            }
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
                        float faceSign = generator.FlipPlacement ? -1 : 1;
                        var face = hole.transform.position - hole.transform.forward * faceSign *
                            (hole.transform.TransformVector(Vector3.forward).magnitude / 2f);
                        return Vector3.Distance(face, target) < .001f && Vector3.Dot(hole.transform.forward, normal) * faceSign > .999f;
                    });
                    Require(matched, "No generated hole mates to the target: " + generator.GetId());
                    if (type.id == "RAIL" || type.id == "RAILS" || type.id == "MOTR") {
                        var bodyCenter = part.transform.TransformPoint(generator.GetMesh().bounds.center);
                        Require(Vector3.Dot(bodyCenter - target, normal) > .01f,
                            "Part body is upside down through the mounting surface: " + generator.GetId());
                    }
                }
                finally { Object.DestroyImmediate(part); }
            }
        }

        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
