using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Protobot;
using Protobot.Transformations;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class NutPlacementValidation {
        public static void Check(Placement placement, MouseCast cast, Protobot.InputEvents.InputEvent flip) {
            placement.CancelPlacement();
            var camera = cast.GetComponent<Camera>();
            var cameraPose = new Pose(camera.transform.position, camera.transform.rotation);
            var generator = PartsManager.GetPartType("NUT").GetComponent<PartGenerator>();
            var oldVariant = generator.param1.value;
            bool oldFlip = flip.IsPressed;
            var temporary = new List<GameObject>();
            var fallback = placement.GetComponent<PartHolePlaceDisplacement>();
            var screwRule = placement.GetComponent<NutPlaceDisplacement>();
            var shaftRule = placement.GetComponent<ShaftAxisPlaceDisplacement>();
            var variants = generator.GetParam1Options();
            Require(new HashSet<string>(variants).SetEquals(new[] { "Lock", "Keps", "Hex", "Low Profile" }), "Nut variant coverage changed.");
            try {
                foreach (var rotation in new[] { Quaternion.identity, Quaternion.Euler(23, 47, 61) }) {
                    var plate = Part("PLTE-3-3", new Vector3(1200, 1200, 1200), rotation);
                    var hole = plate.GetComponentsInChildren<HoleCollider>()[0];
                    foreach (var variant in variants) {
                        generator.param1.value = variant;
                        var data = Data();
                        Require(Quaternion.Angle(data.initialRotation, Quaternion.identity) < .001f,
                            "The nut mounting correction changed its free-placement pose.");
                        foreach (float viewSide in new[] { 1f, -1f }) {
                            Aim(hole.transform.position, hole.transform.forward * viewSide, hole.transform.up);
                            Require(cast.hit.collider?.gameObject == hole.gameObject, "Nut test did not hit the structural hole.");
                            foreach (bool opposite in new[] { false, true }) {
                                flip.IsPressed = opposite;
                                var face = MoveTargeting.ForHole(hole.holeData, hole.transform.forward * viewSide, opposite).face;
                                Require(fallback.TryGetDisplacement(data, out var pose), "Nut did not resolve the hole.");
                                var nut = Commit(data, pose);
                                CheckSeat(nut, face, "hole / " + variant);
                                Remove(nut);
                            }
                        }
                    }
                    plate.SetActive(false);

                    // Keep a real structural bore under the screw so its endpoint
                    // must come from the existing stacked-hardware resolver.
                    var screw = Part("SCRW-1.00in", new Vector3(1220, 1200, 1200), rotation).GetComponent<Screw>();
                    var screwPlate = Part("PLTE-3-3", screw.transform.position, rotation);
                    var screwHole = screwPlate.GetComponentsInChildren<HoleCollider>()[0];
                    screwPlate.transform.position += screw.transform.position - screw.transform.forward * .6f - screwHole.transform.position;
                    Aim(screw.transform.position - screw.transform.forward * .2f, screw.transform.right, screw.transform.up);
                    Require(cast.gameObject == screw.gameObject, "Nut regression did not hit the screw.");
                    foreach (var variant in variants) {
                        generator.param1.value = variant;
                        foreach (bool opposite in new[] { false, true }) {
                            flip.IsPressed = opposite;
                            var expectedPosition = screw.GetLowestPoint();
                            Require(Vector3.Distance(expectedPosition, screw.transform.position) > .1f, "Screw regression did not exercise its occupied-bore endpoint.");
                            Require(screwRule.TryGetDisplacement(Data(), out var pose), "Nut lost the dedicated screw rule.");
                            var nut = Commit(Data(), pose);
                            Near(nut.transform.position, expectedPosition, "Nut screw endpoint changed: " + variant);
                            Require(Vector3.Dot(nut.transform.forward, screw.transform.forward) > .999f,
                                "Nut screw orientation changed: " + variant);
                            Remove(nut);
                        }
                    }
                    screw.gameObject.SetActive(false); screwPlate.SetActive(false);

                    var shaft = Part("SHFT-Normal-6", new Vector3(1240, 1200, 1200), rotation);
                    var shaftPoint = shaft.transform.position + shaft.transform.forward * .5f;
                    var mountingPlate = Part("PLTE-3-3", shaftPoint, rotation);
                    var mountingHole = mountingPlate.GetComponentsInChildren<HoleCollider>()[0];
                    mountingPlate.SetActive(false);
                    Aim(shaftPoint, shaft.transform.right, shaft.transform.up);
                    Require(cast.gameObject == shaft, "Nut regression did not hit the shaft.");
                    foreach (var variant in variants) {
                        generator.param1.value = variant;
                        foreach (bool opposite in new[] { false, true }) {
                            flip.IsPressed = opposite;
                            Require(shaftRule.TryGetDisplacement(Data(), out var pose), "Nut did not resolve a bare shaft.");
                            var nut = Commit(Data(), pose);
                            Require(Vector3.Dot(nut.transform.forward, shaft.transform.forward) * (opposite ? 1 : -1) > .999f,
                                "Nut shaft orientation is reversed: " + variant);
                            Near(nut.transform.position, shaftPoint, "Nut shifted along the bare shaft.");
                            Remove(nut);
                        }
                        foreach (float side in new[] { 1f, -1f }) {
                            mountingPlate.SetActive(true);
                            mountingPlate.transform.position += shaftPoint + shaft.transform.forward * side * .25f - mountingHole.transform.position;
                            Aim(shaftPoint, shaft.transform.right, shaft.transform.up);
                            foreach (bool opposite in new[] { false, true }) {
                                flip.IsPressed = opposite;
                                var face = MoveTargeting.ForHole(mountingHole.holeData, -shaft.transform.forward * side, opposite).face;
                                Require(shaftRule.TryGetDisplacement(Data(), out var pose), "Nut lost a shaft-adjacent mounting face.");
                                var nut = Commit(Data(), pose);
                                CheckSeat(nut, face, "shaft mounting face / " + variant);
                                Remove(nut);
                            }
                            mountingPlate.SetActive(false);
                        }
                    }
                    // The centered-part convention still applies to other shaft hardware.
                    var washerGenerator = PartsManager.GetPartType("WSHR").GetComponent<PartGenerator>();
                    var washer = new PartPlacementData(washerGenerator, washerGenerator.GetComponent<PartType>(), placement.transform);
                    flip.IsPressed = false;
                    Require(shaftRule.TryGetDisplacement(washer, out var washerPose) &&
                        Vector3.Dot(washerPose.rotation.Forward, shaft.transform.forward) > .999f,
                        "The nut correction changed washer shaft orientation.");
                    shaft.SetActive(false);
                }
                Debug.Log("Nut placement passed: Lock/Keps/Hex/Low Profile, oblique and opposite hole faces, exact bearing-face contact, preview/commit parity, unchanged screw orientation/endpoints, bare shafts and both shaft-adjacent faces with opposite-face input, unchanged washer shaft orientation and free-placement defaults.");
            }
            finally {
                placement.CancelPlacement();
                generator.param1.value = oldVariant;
                flip.IsPressed = oldFlip;
                camera.transform.SetPositionAndRotation(cameraPose.position, cameraPose.rotation);
                foreach (var obj in temporary.Where(obj => obj != null)) Object.DestroyImmediate(obj);
            }

            PartPlacementData Data() => new(generator, generator.GetComponent<PartType>(), placement.transform);
            GameObject Part(string id, Vector3 position, Quaternion rotation) {
                var part = PartsManager.GeneratePart(id, position, rotation); temporary.Add(part); return part;
            }
            void Aim(Vector3 point, Vector3 normal, Vector3 up) {
                camera.transform.SetPositionAndRotation(point + normal * 5, Quaternion.LookRotation(-normal, up));
                InputSystem.Update();
                InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point));
                Physics.SyncTransforms();
            }
            GameObject Commit(PartPlacementData data, Displacement expected) {
                placement.StartPlacing(data);
                Invoke(placement, "Update");
                var preview = (Displacement)typeof(Placement).GetField("currentDisplacement", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(placement);
                Near(preview.translation.Position, expected.translation.Position, "Preview selected a different placement rule.");
                Require(Quaternion.Angle(preview.rotation.Orientation, expected.rotation.Orientation) < .03f,
                    "Preview changed the placement rule's orientation.");
                var previous = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None);
                Invoke(placement, "Place");
                var placed = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None).Single(saved => !previous.Contains(saved)).gameObject;
                temporary.Add(placed);
                Near(placed.transform.position, preview.translation.Position, "Commit changed the preview position.");
                Require(Quaternion.Angle(placed.transform.rotation, preview.rotation.Orientation) < .03f, "Commit changed the preview rotation.");
                placement.CancelPlacement();
                return placed;
            }
            void Remove(GameObject obj) { obj.SetActive(false); }
        }

        private static void CheckSeat(GameObject nut, Pose target, string context) {
            var hole = nut.GetComponentInChildren<HoleCollider>();
            var normal = target.rotation * Vector3.forward;
            var bearingFace = hole.transform.position + hole.transform.forward * hole.transform.TransformVector(Vector3.forward).magnitude * .5f;
            Near(bearingFace, target.position, "Nut bearing face missed the mounting surface: " + context);
            Require(Vector3.Dot(nut.transform.forward, normal) < -.999f, "Nut faces away from its bearing surface: " + context);
            Require(Vector3.Dot(nut.GetComponent<Renderer>().bounds.center - target.position, normal) > .01f,
                "Nut body is inside the mounting surface: " + context);
        }
        private static void Invoke(object obj, string method) => obj.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, null);
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .003f, message + $": {a} vs {b}");
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
