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
using Protobot.UI;
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
                inside.SetActive(false);
                CheckGearEdits(objects, manager, shaft, collarA, collarB);
                CheckOtherPartEdits(objects, manager);
                Debug.Log("Fastener grouping passed: real screw trigger detection, idempotent hits, equal-count replacement, nut/collar removal, disable/re-enable, distinct shaft collars, bounded groups, exclusive occupancy, and Command-click individual movement.");
            }
            finally {
                Invoke(tool, "Finish", false);
                manager.current = oldSelection;
                Physics.simulationMode = oldSimulation;
                foreach (var obj in objects.Where(obj => obj != null)) Object.DestroyImmediate(obj);
            }
        }

        private static void CheckOtherPartEdits(List<GameObject> objects, SelectionManager manager) {
            var ui = Object.FindFirstObjectByType<AddPropertiesUI>();
            var rotation = Quaternion.Euler(17, 32, 11);
            var axis = rotation * Vector3.forward;
            var center = new Vector3(3000, 3000, 3000);
            var shaft = Generate("SHFT-Normal-6", center);
            var collarA = Mount("CLMP-Normal-Normal", center - axis, HoleCollider.HoleType.Clamp);
            var collarB = Mount("CLMP-Clamping-Normal", center + axis, HoleCollider.HoleType.Clamp);
            var shaftMembers = new List<GameObject> { shaft, collarA, collarB };
            foreach (var sample in new[] {
                ("SPKT-Normal-24T", 1, "48T"),
                ("SPKT-High Strength-24T", 0, "Normal"),
                ("TWHL-V1-4.00in", 1, "5.00in"),
                ("TWHL-V1-4.00in", 0, "V2"),
                ("OMNI-V1-4.00in", 0, "V2"),
                ("FWHL-With Adapters-3.00in", 1, "4.00in")
            }) {
                Debug.Log("Checking regenerated shaft part: " + sample.Item1);
                var payload = Mount(sample.Item1, center);
                shaftMembers.Add(payload);
                Detect();
                shaft.GetComponent<Shaft>().FormConnection();
                if (!payload.TryGetGroup(out List<GameObject> _)) {
                    var detector = shaft.GetComponent<Shaft>().holeDetector;
                    foreach (var hole in payload.GetComponentsInChildren<HoleCollider>())
                        Debug.Log($"Unattached hole on {payload.name}: owner={hole.holeData.part.name}, occupied={hole.IsOccupied}, " +
                            $"intersecting={detector.IsHoleIntersecting(hole)}, aligned={Vector3.Dot(hole.transform.forward, detector.transform.up)}, " +
                            $"detected={detector.holes.Contains(hole)}, bounds={hole.GetComponent<Collider>().bounds}");
                }
                Members(payload, shaftMembers.ToArray());
                if (sample.Item1 == "SPKT-Normal-24T") {
                    var overlapping = Mount("SPKT-Normal-24T", center);
                    Detect(); shaft.GetComponent<Shaft>().FormConnection();
                    Require(!overlapping.TryGetGroup(out List<GameObject> _),
                        "Intersecting bores were allowed to share the shaft detector.");
                    Members(payload, shaftMembers.ToArray());
                    overlapping.SetActive(false);
                }
                payload = Edit(payload, sample.Item2, sample.Item3, shaftMembers, true);
                payload.SetActive(false);
                shaftMembers.Remove(payload);
            }
            var sprocket = Mount("SPKT-Normal-24T", center);
            shaftMembers.Add(sprocket); Detect(); shaft.GetComponent<Shaft>().FormConnection();
            collarA = Edit(collarA, 0, "Clamping", shaftMembers, true);
            collarA = Edit(collarA, 1, "High Strength", shaftMembers, true);
            collarB = Edit(collarB, 1, "High Strength", shaftMembers, true);
            sprocket = Edit(sprocket, 0, "High Strength", shaftMembers, true);
            shaft = Edit(shaft, 0, "High Strength", shaftMembers, false);
            shaft = Edit(shaft, 1, "8", shaftMembers, false);
            ShrinkOutOfReach(shaft, "1", shaftMembers);

            // The same replacement path must work on both ends of a screw
            // connection: through parts, threaded targets and the fastener itself.
            center += Vector3.right * 20;
            var screw = Generate("SCRW-1.00in", center);
            var plate = Mount("PLTE-3-3", center - axis * .3f);
            var nut = Mount("NUT-Hex", center - axis * .7f, HoleCollider.HoleType.Threaded);
            var screwMembers = new List<GameObject> { screw, plate, nut };
            Detect(); screw.GetComponent<Screw>().FormConnection();
            Members(plate, screwMembers.ToArray());
            plate = Edit(plate, 0, "4", screwMembers, true);
            plate = Edit(plate, 1, "4", screwMembers, true);
            nut = Edit(nut, 0, "Keps", screwMembers, true);
            screw = Edit(screw, 0, "1.25in", screwMembers, false);
            plate.SetActive(false); screwMembers.Remove(plate);
            var channel = Mount("CCHL-1x2-5", center - axis * .3f);
            screwMembers.Add(channel); Detect(); screw.GetComponent<Screw>().FormConnection();
            Members(channel, screwMembers.ToArray());
            channel = Edit(channel, 1, "6", screwMembers, true);
            channel = Edit(channel, 0, "1x3", screwMembers, true);
            ShrinkOutOfReach(screw, "1/2in", screwMembers);
            Debug.Log("General part regeneration passed: sprocket, traction/omni/flex wheel, collar, shaft, plate, nut, screw and channel edits in rotated assemblies; immediate membership, anchoring, undo/redo and out-of-reach connection removal.");

            GameObject Generate(string id, Vector3 position) {
                var generator = PartsManager.GetPartType(id.Split('-')[0]).GetComponent<PartGenerator>();
                PartParameterEditing.Load(generator, id);
                var part = generator.Generate(position, rotation);
                objects.Add(part);
                return part;
            }
            GameObject Mount(string id, Vector3 position, HoleCollider.HoleType? holeType = null) {
                var part = Generate(id, position);
                var hole = NearestHole(part, position, holeType);
                part.transform.rotation = Quaternion.FromToRotation(hole.transform.forward, axis) * part.transform.rotation;
                part.transform.position += position - hole.transform.position;
                return part;
            }
            void Detect() {
                Physics.SyncTransforms(); Physics.Simulate(.02f); Physics.Simulate(.02f);
            }
            void Select(GameObject part, bool hole) {
                if (hole) {
                    // Keep the actual mating hole anchored when a profile
                    // change adds/removes rows or moves a channel side wall.
                    var source = part.GetComponentsInChildren<HoleCollider>()
                        .OrderByDescending(candidate => candidate.detectors.Count > 0)
                        .ThenBy(candidate => (candidate.transform.position - part.transform.position).sqrMagnitude).First();
                    var marker = new GameObject("Regeneration hole selection", typeof(MeshFilter), typeof(HoleFace));
                    objects.Add(marker);
                    manager.SetCurrent(new HoleSelection {
                        gameObject = marker, holeFace = marker.GetComponent<HoleFace>(), holeData = source.holeData,
                        faceDirection = source.transform.forward, individual = true
                    }, true);
                }
                else manager.SetCurrent(new ObjectSelection { gameObject = part }, true);
                Invoke(ui, "ObserveSelection");
                StateSystem.states = new List<State> { new State(new List<IElement>()) };
                StateSystem.curIndex = 0;
            }
            GameObject EditedPart() => manager.current is HoleSelection hole ? hole.holeData.part : manager.current.gameObject;
            GameObject Edit(GameObject original, int parameter, string value, List<GameObject> members, bool hole) {
                Select(original, hole);
                var anchor = (manager.current as HoleSelection)?.holeData.position;
                int index = members.IndexOf(original);
                if (parameter == 0) ui.UpdateParam1(value); else ui.UpdateParam2(value);
                var edited = EditedPart(); objects.Add(edited);
                Require(edited != original && original.IsDeleted(), "Part was not regenerated: " + original.name);
                members[index] = edited;
                Members(edited, members.ToArray());
                Require(!original.TryGetGroup(out List<GameObject> _), "Regeneration kept deleted geometry in a group.");
                if (anchor.HasValue) Require(manager.current.individual &&
                    Vector3.Distance(((HoleSelection)manager.current).holeData.position, anchor.Value) < .001f,
                    "Regeneration lost selected-hole anchoring or individual scope: " + original.name);
                else Require(Vector3.Distance(edited.transform.position, original.transform.position) < .001f,
                    "Regeneration moved the fastener: " + original.name);
                StateSystem.instance.Undo();
                members[index] = original; Members(original, members.ToArray());
                Require(EditedPart() == original, "Undo lost the regenerated part's selection.");
                StateSystem.instance.Redo();
                members[index] = edited; Members(edited, members.ToArray());
                Require(EditedPart() == edited, "Redo lost the regenerated part's selection.");
                return edited;
            }
            void ShrinkOutOfReach(GameObject fastener, string length, List<GameObject> members) {
                Select(fastener, false);
                if (fastener.GetComponent<Shaft>() != null) ui.UpdateParam2(length); else ui.UpdateParam1(length);
                var shorter = EditedPart(); objects.Add(shorter);
                Require(shorter != fastener, "Fastener shortening did not replace geometry.");
                foreach (var part in members.Where(part => part != fastener).Append(shorter))
                    Require(!part.TryGetGroup(out List<GameObject> _), "Shortened fastener retained an out-of-reach connection.");
                StateSystem.instance.Undo(); Members(fastener, members.ToArray());
                StateSystem.instance.Redo();
                Require(!shorter.TryGetGroup(out List<GameObject> _), "Redo retained an out-of-reach connection.");
            }
        }

        private static HoleCollider NearestHole(GameObject part, Vector3 position, HoleCollider.HoleType? type = null) =>
            part.GetComponentsInChildren<HoleCollider>().Where(hole => !type.HasValue || hole.holeType == type.Value)
                .OrderBy(hole => (hole.transform.position - position).sqrMagnitude).First();

        private static void CheckGearEdits(List<GameObject> objects, SelectionManager manager, Shaft shaft,
            GameObject collarA, GameObject collarB) {
            var ui = Object.FindFirstObjectByType<AddPropertiesUI>();
            var generator = PartsManager.GetPartType("GEAR").GetComponent<PartGenerator>();
            PartParameterEditing.Load(generator, "GEAR-Normal-36T");
            var gear = generator.Generate(shaft.transform.position, shaft.transform.rotation);
            objects.Add(gear);
            var centerHole = gear.GetComponentsInChildren<HoleCollider>()
                .OrderBy(hole => (hole.transform.position - gear.transform.position).sqrMagnitude).First();
            gear.transform.position += shaft.transform.position - centerHole.transform.position;
            Physics.SyncTransforms(); Physics.Simulate(.02f); Physics.Simulate(.02f);
            shaft.FormConnection();
            Members(gear, shaft.gameObject, collarA, collarB, gear);
            manager.SetCurrent(new ObjectSelection { gameObject = gear }, true);
            Invoke(ui, "ObserveSelection");
            var shaftPose = shaft.transform.position;
            var gearPose = gear.transform.position;
            // No physics steps or incidental movement between editing and testing:
            // membership must already be correct when selection is restored.
            ui.UpdateParam2("60T");
            var resized = manager.current.gameObject; objects.Add(resized);
            Require(resized != gear && gear.IsDeleted(), "Gear size edit did not replace geometry.");
            Members(resized, shaft.gameObject, collarA, collarB, resized);
            Require(!gear.TryGetGroup(out List<GameObject> _), "Old gear kept its graph membership.");
            StateSystem.instance.Undo();
            Require(manager.current.gameObject == gear, "Gear edit undo lost selection.");
            Members(gear, shaft.gameObject, collarA, collarB, gear);
            StateSystem.instance.Redo();
            Members(resized, shaft.gameObject, collarA, collarB, resized);
            foreach (var variant in new[] { "High Strength", "High Strength v2", "Normal" }) {
                Invoke(ui, "ObserveSelection");
                ui.UpdateParam1(variant);
                var changed = manager.current.gameObject; objects.Add(changed);
                Members(changed, shaft.gameObject, collarA, collarB, changed);
                Require(Vector3.Distance(changed.transform.position, gearPose) < .001f &&
                    Vector3.Distance(shaft.transform.position, shaftPose) < .001f, "Gear edit moved its assembly.");
            }
            var current = manager.current.gameObject;
            var selectedHole = current.GetComponentsInChildren<HoleCollider>()
                .OrderBy(hole => (hole.transform.position - shaft.transform.position).sqrMagnitude).First();
            var marker = new GameObject("Gear hole selection", typeof(MeshFilter), typeof(HoleFace)); objects.Add(marker);
            manager.SetCurrent(new HoleSelection {
                gameObject = marker, holeFace = marker.GetComponent<HoleFace>(), holeData = selectedHole.holeData,
                faceDirection = selectedHole.transform.forward, individual = true
            }, true);
            var anchor = selectedHole.transform.position;
            Invoke(ui, "ObserveSelection"); ui.UpdateParam2("36T");
            var holeSelection = manager.current as HoleSelection;
            Require(holeSelection != null && holeSelection.individual &&
                Vector3.Distance(holeSelection.holeData.position, anchor) < .001f,
                "Gear resize lost the selected hole, individual scope or its anchor.");
            var holeEdited = holeSelection.holeData.part; objects.Add(holeEdited);
            Members(holeEdited, shaft.gameObject, collarA, collarB, holeEdited);
            StateSystem.instance.Undo();
            Members(current, shaft.gameObject, collarA, collarB, current);
            StateSystem.instance.Redo();
            Members(holeEdited, shaft.gameObject, collarA, collarB, holeEdited);
            // The normal center hole has no inserts; the high-strength hole
            // initializes inserts after synchronous occupancy is established.
            Invoke(ui, "ObserveSelection"); ui.UpdateParam1("High Strength");
            var insertGear = ((HoleSelection)manager.current).holeData.part; objects.Add(insertGear);
            foreach (var insert in insertGear.GetComponentsInChildren<HoleInsert>()) {
                Invoke(insert, "Start");
                var hole = insert.GetComponent<HoleCollider>();
                if (hole.IsOccupiedBy(shaft.holeDetector))
                    Require(Get<GameObject>(insert, "metalInserts").activeSelf, "Early reconnection lost shaft inserts.");
            }
            // Refresh must not blindly copy old graph edges when a clamp disappears.
            collarB.SetActive(false);
            Invoke(ui, "ObserveSelection"); ui.UpdateParam2("60T");
            var unclamped = ((HoleSelection)manager.current).holeData.part; objects.Add(unclamped);
            Require(!unclamped.TryGetGroup(out List<GameObject> _), "Gear edit retained a connection without two clamps.");
            Debug.Log("Gear regeneration passed: stationary size/type edits, immediate shaft membership, old-geometry removal, undo/redo, stable pose and missing-clamp rejection.");
        }

        private static void CheckIndividual(KeyboardTransformTool tool, SelectionManager manager, GameObject plate, GameObject nut) {
            var field = typeof(GroupingBypassInput).GetField("input", BindingFlags.NonPublic | BindingFlags.Static);
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
                InputState.Change(Keyboard.current, new KeyboardState(Key.LeftCtrl), InputUpdateType.Dynamic);
                Physics.SyncTransforms(); Invoke(click, "OnPerformInput");
                Require(picked != null && !picked.individual && picked.gameObject == plate,
                    "Control-click incorrectly bypassed grouping.");
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
        private static void Members(GameObject part, params GameObject[] expected) {
            bool grouped = part.TryGetGroup(out List<GameObject> group);
            Require(grouped && new HashSet<GameObject>(group).SetEquals(expected),
                $"Incorrect fastener group for {part.name}. Expected: {string.Join(", ", expected.Select(obj => obj.name))}; " +
                $"actual: {string.Join(", ", (group ?? new List<GameObject>()).Select(obj => obj.name))}");
        }
        private static object Invoke(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args);
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
