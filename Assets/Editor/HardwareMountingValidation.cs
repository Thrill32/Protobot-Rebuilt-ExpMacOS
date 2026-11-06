using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Protobot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class HardwareMountingValidation {
        private const string SmokeKey = "Protobot.HardwareMountingSmoke";
        private static int frames;
        private static readonly string[] Parts = {
            "HexNR-Nut Retainer", "HexNR-Bearing Retainer", "HexNR-Square Retainer",
            "BRNG-Normal", "BRNG-High Strength", "BRNG-Low Profile", "BRNG-Collar Retainer",
            "BLCK-Normal", "BLCK-High Strength"
        };

        public static void Report() {
            PartsManager.LoadPartTypes();
            foreach (var id in Parts) {
                var generator = PartsManager.GetPartType(id).GetComponent<PartGenerator>();
                PartParameterEditing.Load(generator, id);
                generator.TryGetPartData(out var data);
                var mesh = generator.GetMesh();
                Debug.Log($"MOUNT {id}: bounds={mesh.bounds.ToString("F5")}, flip={data.flipPlacement}, primary={data.primaryHole?.transform.localPosition}, mounting={data.placementHole?.transform.localPosition}");
                foreach (var hole in data.GetComponentsInChildren<HoleCollider>(true)) {
                    var center = data.transform.InverseTransformPoint(hole.transform.position);
                    var axis = data.transform.InverseTransformDirection(hole.transform.forward);
                    var rim = mesh.vertices.Where(v => Vector3.ProjectOnPlane(v - center, axis).magnitude < .17f).ToArray();
                    Debug.Log($"BORE {id}: center={center:F5}, axis={axis:F3}, depth={hole.transform.TransformVector(Vector3.forward).magnitude:F6}, rim={(rim.Length == 0 ? "none" : $"{rim.Min(v => Vector3.Dot(v, axis)):F6}..{rim.Max(v => Vector3.Dot(v, axis)):F6}")}");
                }
            }
        }

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
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
        private static void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                Check();
                Debug.Log("Hardware mounting passed: all three hex nut retainers face outward, both block bearings use their paired mounting holes while preserving the shaft bore, every flat bearing variant uses live bore dimensions, rotated/opposite faces, actual preview/commit parity, screws and nuts on every flat-bearing bore from both sides, screw endpoint stacking, full fastener groups and bearing rotation anchors.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static void Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            GameObject.Find("Add Parts Menu")?.SetActive(false);
            var placement = Object.FindFirstObjectByType<Placement>();
            var standoff = placement.GetComponent<StandoffPlaceDisplacement>();
            var serialized = new SerializedObject(standoff);
            var cast = (MouseCast)serialized.FindProperty("mouseCast").objectReferenceValue;
            var hover = (HoleFace)serialized.FindProperty("currentHoleFace").objectReferenceValue;
            var fallback = new SerializedObject(placement.GetComponent<PartHolePlaceDisplacement>());
            var flip = (InputEvent)fallback.FindProperty("flipInput").objectReferenceValue;
            var camera = cast.GetComponent<Camera>();
            var temporary = new List<GameObject>();
            var oldCamera = new Pose(camera.transform.position, camera.transform.rotation);
            bool oldFlip = flip.IsPressed;
            var update = typeof(Placement).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            var place = typeof(Placement).GetMethod("Place", BindingFlags.Instance | BindingFlags.NonPublic);
            try {
                foreach (var rotation in new[] { Quaternion.identity, Quaternion.Euler(23, 47, 61) }) {
                    foreach (var id in Parts) {
                        var data = Data(id);
                        data.partGenerator.TryGetPartData(out var definition);
                        if (id.StartsWith("HexNR")) Require(definition.flipPlacement && definition.placementHole != null,
                            "Retainer is missing its corrected mounting face: " + id);
                        if (id.StartsWith("BLCK")) {
                            Require(definition.placementHole != null && definition.placementHole != definition.primaryHole &&
                                definition.placementHole.transform.IsChildOf(definition.transform), "Block bearing lost its independent mounting anchor: " + id);
                            var shaftAxis = definition.transform.InverseTransformDirection(definition.primaryHole.transform.forward);
                            var mountAxis = definition.transform.InverseTransformDirection(definition.placementHole.transform.forward);
                            Require(Mathf.Abs(Vector3.Dot(shaftAxis, mountAxis)) < .001f && Vector3.Dot(mountAxis, Vector3.up) > .999f,
                                "Block bearing placement still follows the single shaft bore: " + id);
                        }
                        var target = Part("PLTE-3-3", new Vector3(100, 100, 100), rotation);
                        var targetHole = target.GetComponentsInChildren<HoleCollider>()[0];
                        foreach (float side in new[] { 1f, -1f }) {
                            Aim(targetHole.transform.position, targetHole.transform.forward * side, targetHole.transform.up);
                            foreach (bool opposite in new[] { false, true }) {
                                flip.IsPressed = opposite;
                                var face = MoveTargeting.ForHole(targetHole.holeData, targetHole.transform.forward * side, opposite).face;
                                hover.gameObject.SetActive(true); hover.Set(targetHole.holeData, face.rotation * Vector3.forward);
                                var body = Commit(data);
                                CheckMount(body, definition, face, id);
                                if (id.StartsWith("BRNG")) {
                                    var anchor = standoff.GetRotationAnchor(Current());
                                    Near(anchor.position, face.position, "Flat-bearing rotation pivot is not its mounting face: " + id);
                                }
                                body.SetActive(false);
                            }
                        }
                        target.SetActive(false);
                    }

                    // A flat bearing must be a complete screw/nut connection at
                    // each bore, including the thicker shaft bore on the HS variant.
                    foreach (var id in Parts.Where(id => id.StartsWith("BRNG"))) {
                        var body = Part(id, new Vector3(120, 100, 100), rotation);
                        foreach (var bore in body.GetComponentsInChildren<HoleCollider>()) {
                            foreach (float side in new[] { 1f, -1f }) {
                                flip.IsPressed = false;
                                var normal = bore.transform.forward * side;
                                var front = MoveTargeting.HoleFace(bore.holeData, normal);
                                Aim(bore.transform.position, normal, bore.transform.up);
                                hover.gameObject.SetActive(true); hover.Set(bore.holeData, normal);
                                var screw = Commit(Data("SCRW-1.00in")).GetComponent<Screw>();
                                Near(screw.transform.position, front.position, "Screw did not seat on the flat bearing: " + id);
                                var exit = bore.transform.position - normal * bore.holeData.depth * .5f;
                                Near(screw.GetLowestPoint(), exit, "Screw endpoint does not follow the flat-bearing bore: " + id);
                                Aim(screw.transform.position - screw.transform.forward * .65f, screw.transform.right, screw.transform.up);
                                Require(cast.gameObject == screw.gameObject, "Nut placement did not hover its screw: " + id);
                                hover.direction = Vector3.zero; hover.gameObject.SetActive(false);
                                var nut = Commit(Data("NUT-Hex"));
                                var nutHole = nut.GetComponentInChildren<HoleCollider>();
                                Near(nutHole.transform.position + nutHole.transform.forward * nutHole.holeData.depth * .5f,
                                    exit, "Nut did not contact the far bearing face: " + id);
                                Physics.SyncTransforms(); screw.holeDetector.RefreshOverlaps(); screw.FormConnection();
                                Require(screw.holeDetector.holes.Contains(bore) && screw.holeDetector.holes.Contains(nutHole),
                                    "Screw did not detect both the bearing and nut: " + id);
                                Require(body.TryGetGroup(out List<GameObject> group) && group.Contains(screw.gameObject) && group.Contains(nut),
                                    "Flat bearing, screw and nut did not form a complete group: " + id);
                                nut.SetActive(false); screw.gameObject.SetActive(false);
                            }
                        }
                        body.SetActive(false);
                    }

                    // Add bearings to a screw that already passes through a plate.
                    var bolt = Part("SCRW-1.00in", new Vector3(140, 100, 100), rotation).GetComponent<Screw>();
                    var plate = Part("PLTE-3-3", bolt.transform.position, rotation);
                    var hole = plate.GetComponentsInChildren<HoleCollider>()[0];
                    plate.transform.position += bolt.transform.position - bolt.transform.forward * .3f - hole.transform.position;
                    foreach (var id in Parts.Where(id => id.StartsWith("BRNG"))) {
                        flip.IsPressed = false;
                        Aim(bolt.transform.position - bolt.transform.forward * .65f, bolt.transform.right, bolt.transform.up);
                        hover.direction = Vector3.zero; hover.gameObject.SetActive(false);
                        var target = MoveTargeting.ForScrew(bolt).face;
                        var data = Data(id); data.partGenerator.TryGetPartData(out var definition);
                        var bearing = Commit(data);
                        CheckMount(bearing, definition, target, "screw stack / " + id);
                        var bore = SourceHole(bearing, definition);
                        Near(bolt.GetLowestPoint(), bore.transform.position - bolt.transform.forward * bore.holeData.depth * .5f,
                            "Adding a flat bearing did not extend the screw stack endpoint: " + id);
                        bearing.SetActive(false);
                    }
                    plate.SetActive(false); bolt.gameObject.SetActive(false);
                }
            }
            finally {
                placement.CancelPlacement(); hover.direction = Vector3.zero; hover.gameObject.SetActive(false);
                flip.IsPressed = oldFlip;
                camera.transform.SetPositionAndRotation(oldCamera.position, oldCamera.rotation);
                foreach (var body in temporary.Where(body => body != null)) Object.DestroyImmediate(body);
            }

            PartPlacementData Data(string id) {
                var generator = PartsManager.GetPartType(id).GetComponent<PartGenerator>();
                PartParameterEditing.Load(generator, id);
                return new PartPlacementData(generator, generator.GetComponent<PartType>(), placement.transform);
            }
            GameObject Part(string id, Vector3 point, Quaternion rotation) {
                var body = PartsManager.GeneratePart(id, point, rotation); temporary.Add(body); return body;
            }
            void Aim(Vector3 point, Vector3 normal, Vector3 up) {
                camera.transform.SetPositionAndRotation(point + normal * 5, Quaternion.LookRotation(-normal, up));
                InputSystem.Update();
                InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point)); Physics.SyncTransforms();
                Require(cast.hit.collider != null, $"Pointer missed the hardware fixture: point={point}, ray={cast.ray}, screen={MouseInput.Position}.");
            }
            Protobot.Transformations.Displacement Current() => (Protobot.Transformations.Displacement)typeof(Placement)
                .GetField("currentDisplacement", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(placement);
            GameObject Commit(PartPlacementData data) {
                placement.StartPlacing(data); update.Invoke(placement, null); var preview = Current();
                Require((preview.translation.Position - camera.transform.position).sqrMagnitude < 400,
                    $"Placement did not resolve the nearby fixture: id={data.partGenerator.GetId()}, preview={preview.translation.Position}, ray={cast.ray}, hit={cast.hit.collider?.name}.");
                var previous = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None);
                place.Invoke(placement, null);
                var body = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None).Single(saved => !previous.Contains(saved)).gameObject;
                temporary.Add(body);
                Near(body.transform.position, preview.translation.Position, "Commit changed the preview: " + data.partGenerator.GetId());
                Require(Quaternion.Angle(body.transform.rotation, preview.rotation.Orientation) < .03f, "Commit rotated the preview.");
                placement.CancelPlacement(); return body;
            }
        }

        private static HoleCollider SourceHole(GameObject body, PartData definition) {
            var source = definition.placementHole != null ? definition.placementHole : definition.primaryHole;
            var local = definition.transform.InverseTransformPoint(source.transform.position);
            return body.GetComponentsInChildren<HoleCollider>().OrderBy(h => (h.transform.localPosition - local).sqrMagnitude).First();
        }
        private static void CheckMount(GameObject body, PartData definition, Pose target, string context) {
            var source = SourceHole(body, definition);
            if (definition.primaryHole != null) {
                var localPrimary = definition.transform.InverseTransformPoint(definition.primaryHole.transform.position);
                var mechanical = body.GetComponentsInChildren<HoleCollider>().OrderBy(h => (h.transform.localPosition - localPrimary).sqrMagnitude).First();
                Near(definition.PrimaryHoleDepth, mechanical.holeData.depth, "Catalog and generated mechanical-bore depths disagree: " + context);
            }
            var normal = target.rotation * Vector3.forward;
            float sign = definition.flipPlacement ? -1 : 1;
            Near(source.transform.position - source.transform.forward * sign * source.holeData.depth * .5f,
                target.position, "Mounting face missed its target: " + context);
            Require(Vector3.Dot(source.transform.forward * sign, normal) > .999f, "Mounting face is reversed: " + context);
            var center = body.transform.TransformPoint(body.GetComponent<MeshFilter>().sharedMesh.bounds.center);
            Require(Vector3.Dot(center - target.position, normal) > .01f, "Part body is inside its mounting surface: " + context);
            if (context.StartsWith("BLCK")) {
                var holes = body.GetComponentsInChildren<HoleCollider>().Where(h => Mathf.Abs(Vector3.Dot(h.transform.forward, normal)) > .999f).ToArray();
                Require(holes.Length == 2, "Block bearing did not mount on the paired side: " + context);
                foreach (var hole in holes) {
                    var face = hole.transform.position - hole.transform.forward * hole.holeData.depth * .5f;
                    Near(Vector3.Dot(face - target.position, normal), 0, "Block mounting holes are not coplanar: " + context);
                }
                Near(Vector3.Distance(holes[0].transform.position, holes[1].transform.position), 1, "Block mounting spacing changed.");
            }
        }
        private static void Near(float a, float b, string message) => Require(Mathf.Abs(a - b) < .003f, message + $" ({a} vs {b})");
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .003f, message + $" ({a:F5} vs {b:F5})");
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
