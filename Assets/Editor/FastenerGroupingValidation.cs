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
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class FastenerGroupingValidation {
        public static void ValidateRelated() {
            KeyboardTransformValidation.Validate();
            TransformEntryValidation.Validate();
            InteractionUpdatesValidation.Validate();
        }

        public static void Check(KeyboardTransformTool tool, SelectionManager manager) {
            var objects = new List<GameObject>();
            var oldSelection = manager.current;
            var oldSimulation = Physics.simulationMode;
            try {
                var center = new Vector3(2000, 2000, 2000);
                var generator = PartsManager.GetPartType("SCRW").GetComponent<PartGenerator>();
                generator.param1.value = "1.00in";
                var screwObject = generator.Generate(center, Quaternion.identity); objects.Add(screwObject);
                var screw = screwObject.GetComponent<Screw>();
                var plate = Body(center + Vector3.back * .3f); objects.Add(plate);
                var nut = Body(center + Vector3.back * .7f); objects.Add(nut);
                var replacement = Body(center + Vector3.back * .3f); objects.Add(replacement);
                var outside = Body(center + Vector3.back * .9f); objects.Add(outside);
                var through = Hole(plate, HoleCollider.HoleType.Normal);
                var threaded = Hole(nut, HoleCollider.HoleType.Threaded);
                var newThrough = Hole(replacement, HoleCollider.HoleType.Normal);
                var beyondNut = Hole(outside, HoleCollider.HoleType.Normal);
                replacement.SetActive(false); outside.SetActive(false);
                Physics.simulationMode = SimulationMode.Script;
                Physics.SyncTransforms(); Physics.Simulate(.02f); Physics.Simulate(.02f);
                Require(screw.holeDetector.holes.Contains(through) && screw.holeDetector.holes.Contains(threaded),
                    "Physics did not detect screw through/threaded holes.");
                threaded.twoSided = true;
                for (int i = 0; i < 5; i++) screw.holeDetector.AddHole(threaded);
                Require(screw.holeDetector.TargetHoleCount == 1 && threaded.detectors.Count == 1,
                    "Repeated trigger hits inflated threaded-hole counts.");
                screw.FormConnection();
                Members(plate, screwObject, plate, nut);
                CheckIndividual(tool, manager, plate, nut);

                screw.holeDetector.RemoveHole(through);
                replacement.SetActive(true);
                screw.holeDetector.AddHole(newThrough);
                screw.FormConnection();
                Members(replacement, screwObject, replacement, nut);
                Require(!plate.TryGetGroup(out List<GameObject> _), "Equal-count attachment replacement retained the old plate.");
                outside.SetActive(true); screw.holeDetector.AddHole(beyondNut); screw.FormConnection();
                Require(!outside.TryGetGroup(out List<GameObject> _), "A screw attached a part beyond its last threaded hole.");
                screw.holeDetector.RemoveHole(threaded); screw.holeDetector.RemoveHole(threaded); screw.FormConnection();
                Require(screw.holeDetector.TargetHoleCount == 0 && !replacement.TryGetGroup(out List<GameObject> _),
                    "Removing the nut did not break the screw group.");
                screw.holeDetector.AddHole(threaded); screw.FormConnection();
                new ObjectElement(screwObject).ApplyExistence(false);
                Require(!nut.TryGetGroup(out List<GameObject> _) && threaded.detectors.Count == 0,
                    "Deleting a fastener retained a graph edge or detector ownership.");
                new ObjectElement(screwObject).ApplyExistence(true);
                plate.SetActive(false); outside.SetActive(false);
                Physics.SyncTransforms(); Physics.Simulate(.02f); Physics.Simulate(.02f); screw.FormConnection();
                Members(replacement, screwObject, replacement, nut);

                var shaftGen = PartsManager.GetPartType("SHFT").GetComponent<PartGenerator>();
                PartParameterEditing.Load(shaftGen, "SHFT-Normal-6");
                var shaftObject = shaftGen.Generate(center + Vector3.right * 10, Quaternion.identity); objects.Add(shaftObject);
                var shaft = shaftObject.GetComponent<Shaft>();
                var collarGen = PartsManager.GetPartType("CLMP").GetComponent<PartGenerator>();
                PartParameterEditing.Load(collarGen, "CLMP-Normal-Normal");
                var collarA = collarGen.Generate(shaftObject.transform.position - Vector3.forward, Quaternion.identity); objects.Add(collarA);
                var clampA = collarA.GetComponentsInChildren<HoleCollider>().First(h => h.holeType == HoleCollider.HoleType.Clamp);
                collarA.transform.position += shaftObject.transform.position - Vector3.forward - clampA.transform.position;
                PartParameterEditing.Load(collarGen, "CLMP-Clamping-Normal");
                var collarB = collarGen.Generate(shaftObject.transform.position + Vector3.forward, Quaternion.identity); objects.Add(collarB);
                var clampB = collarB.GetComponentsInChildren<HoleCollider>().First(h => h.holeType == HoleCollider.HoleType.Clamp);
                collarB.transform.position += shaftObject.transform.position + Vector3.forward - clampB.transform.position;
                var inside = Body(shaftObject.transform.position); objects.Add(inside);
                var insideHole = Hole(inside, HoleCollider.HoleType.Normal);
                var beyond = Body(shaftObject.transform.position + Vector3.forward * 2); objects.Add(beyond);
                var beyondHole = Hole(beyond, HoleCollider.HoleType.Normal);
                Physics.SyncTransforms(); Physics.Simulate(.02f); Physics.Simulate(.02f);
                Require(shaft.holeDetector.holes.Contains(clampA) && shaft.holeDetector.holes.Contains(clampB),
                    "Physics did not detect the two shaft collars.");
                shaft.holeDetector.RemoveAll();
                clampA.twoSided = true;
                shaft.holeDetector.AddHole(clampA); shaft.holeDetector.AddHole(clampA);
                Require(shaft.holeDetector.TargetHoleCount == 1 && !shaft.CanFormConnection, "One collar acted like two collars.");
                var extraClamp = Hole(collarA, HoleCollider.HoleType.Clamp);
                extraClamp.transform.localPosition += Vector3.forward * .1f;
                shaft.holeDetector.AddHole(extraClamp);
                Require(!shaft.CanFormConnection, "Two holes on the same collar formed a shaft group.");
                shaft.holeDetector.AddHole(clampB); shaft.holeDetector.AddHole(insideHole); shaft.holeDetector.AddHole(beyondHole);
                shaft.FormConnection();
                Members(inside, shaftObject, collarA, collarB, inside);
                Require(!beyond.TryGetGroup(out List<GameObject> _), "Shaft grouped a part outside the collars.");
                collarB.SetActive(false); shaft.FormConnection();
                Require(!inside.TryGetGroup(out List<GameObject> _), "Removing a collar retained the shaft group.");
                collarB.SetActive(true); shaft.holeDetector.AddHole(clampB); shaft.FormConnection();
                Members(inside, shaftObject, collarA, collarB, inside);

                var occupied = Hole(beyond, HoleCollider.HoleType.Clamp);
                screw.holeDetector.AddHole(occupied);
                shaft.holeDetector.AddHole(occupied);
                Require(!shaft.holeDetector.holes.Contains(occupied), "A detector claimed an occupied hole without ownership.");
                int removals = 0; screw.holeDetector.OnRemoveHole += () => removals++;
                screw.holeDetector.RemoveHole(occupied); screw.holeDetector.RemoveHole(occupied);
                Require(removals == 1, "Removing a hole twice emitted duplicate notifications.");
                var crossBody = Body(shaftObject.transform.position + Vector3.forward * 2.5f); objects.Add(crossBody);
                var crossHole = Hole(crossBody, HoleCollider.HoleType.Normal);
                crossHole.transform.rotation = Quaternion.Euler(0, 90, 0);
                Physics.SyncTransforms();
                Invoke(shaft.holeDetector, "OnTriggerStay", crossHole.GetComponent<Collider>());
                Require(!shaft.holeDetector.holes.Contains(crossHole), "A perpendicular hole formed a false shaft connection.");
                Debug.Log("Fastener grouping passed: real screw trigger detection, idempotent hits, equal-count replacement, nut/collar removal, disable/re-enable, distinct shaft collars, bounded groups, exclusive occupancy, and Command-click individual movement.");
            }
            finally {
                Invoke(tool, "Finish", false);
                manager.current = oldSelection;
                Physics.simulationMode = oldSimulation;
                foreach (var obj in objects.Where(obj => obj != null)) Object.DestroyImmediate(obj);
            }
        }

        private static void CheckIndividual(KeyboardTransformTool tool, SelectionManager manager, GameObject plate, GameObject nut) {
            var field = typeof(SnapBypassInput).GetField("input", BindingFlags.NonPublic | BindingFlags.Static);
            var frame = typeof(KeyboardTransformTool).GetField("finishedFrame", BindingFlags.NonPublic | BindingFlags.Static);
            var oldInput = field.GetValue(null);
            var probe = new GameObject("Command selection probe"); probe.SetActive(false);
            var input = probe.AddComponent<InputEvent>();
            input.defaultAction = new InputAction("Command selection", InputActionType.Button, "<Keyboard>/leftMeta");
            input.defaultAction.Enable(); input.rebindAction = new RebindAction("Group test " + Guid.NewGuid());
            var clickObject = new GameObject("Individual click probe"); clickObject.SetActive(false);
            var click = clickObject.AddComponent<ClickSelector>();
            Set(click, "mouseCast", Get<MouseCast>(tool, "mouseCast")); Set(click, "input", input);
            ISelection picked = null;
            click.setEvent += selection => picked = selection;
            var camera = Get<Camera>(tool, "refCamera");
            var oldPosition = camera.transform.position;
            var oldRotation = camera.transform.rotation;
            try {
                field.SetValue(null, input); frame.SetValue(null, -1);
                camera.transform.SetPositionAndRotation(plate.transform.position + Vector3.left * 10, Quaternion.LookRotation(Vector3.right));
                InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(plate.transform.position), InputUpdateType.Dynamic);
                InputState.Change(Keyboard.current, new KeyboardState(Key.LeftMeta), InputUpdateType.Dynamic);
                Physics.SyncTransforms(); Invoke(click, "OnPerformInput");
                Require(picked != null && picked.individual && picked.gameObject == plate, "Command-click did not select the individual part.");
                InputState.Change(Keyboard.current, new KeyboardState(), InputUpdateType.Dynamic);
                manager.current = picked; Invoke(tool, "BeginSelection", false);
                Require(Get<List<GameObject>>(tool, "bodies").SequenceEqual(new[] { plate }), "Individual selection expanded after releasing Command.");
                var movement = Object.FindFirstObjectByType<MovementManager>();
                Require(movement.GetMovingConnectedObjs().SequenceEqual(new[] { plate }), "Handle movement ignored individual selection.");
                var nutPosition = nut.transform.position;
                Invoke(tool, "SetTarget", Vector3.right, Quaternion.identity);
                Get<Tween>(tool, "smoothingTween")?.Complete();
                Require(Vector3.Distance(nut.transform.position, nutPosition) < .001f, "Moving an individual part moved its assembly.");
                Invoke(tool, "Finish", false);
                manager.current = new ObjectSelection { gameObject = plate };
                Invoke(tool, "BeginSelection", false);
                Require(Get<List<GameObject>>(tool, "bodies").Count == 3, "Ordinary selection no longer moves the assembly.");
                Invoke(tool, "Finish", false);
            }
            finally {
                camera.transform.position = oldPosition;
                camera.transform.rotation = oldRotation;
                field.SetValue(null, oldInput);
                input.defaultAction.Dispose(); input.rebindAction.action.Dispose();
                Object.DestroyImmediate(probe); Object.DestroyImmediate(clickObject);
            }
        }

        private static GameObject Body(Vector3 position) {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.transform.position = position; obj.transform.localScale = new Vector3(1, 1, .1f);
            obj.AddComponent<SavedObject>(); return obj;
        }
        private static HoleCollider Hole(GameObject body, HoleCollider.HoleType type) {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(obj.GetComponent<Collider>());
            obj.transform.SetParent(body.transform); obj.transform.position = body.transform.position;
            obj.transform.localScale = new Vector3(.2f, .2f, .5f);
            obj.layer = HoleCollider.HOLE_COLLISIONS_LAYER;
            obj.AddComponent<MeshCollider>().sharedMesh = obj.GetComponent<MeshFilter>().sharedMesh;
            var hole = obj.AddComponent<HoleCollider>(); hole.holeType = type; return hole;
        }
        private static void Members(GameObject part, params GameObject[] expected) => Require(
            part.TryGetGroup(out List<GameObject> group) && new HashSet<GameObject>(group).SetEquals(expected), "Incorrect fastener group membership.");
        private static object Invoke(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args);
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
