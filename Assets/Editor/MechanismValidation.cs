using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using DG.Tweening;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.StateSystems;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class MechanismValidation {
        private static readonly List<GameObject> parts = new();
        public static void Check() {
            typeof(Protobot.Tools.KeyboardTransformTool).GetField("finishedFrame", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, -1);
            typeof(SimulationTool).GetField("consumedFrame", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, -1);
            CheckRightAngle("MCMS-Bevel Gear-16T", "MCMS-Bevel Gear-32T", new Vector3(5000, 1000, 1000), .5);
            CheckRightAngle("MCMS-Worm and Wheel-Worm", "MCMS-Worm and Wheel-Gear", new Vector3(5020, 1000, 1000), 1.0 / 24);
            CheckRightAngle("MCMS-Worm and Wheel-Gear", "MCMS-Worm and Wheel-Worm", new Vector3(5040, 1000, 1000), 24);
            CheckRack(new Vector3(5060, 1000, 1000));
            CheckRigidHardware(new Vector3(5080, 1000, 1000));
            CheckChains(new Vector3(5100, 1000, 1000));
            Debug.Log("Expanded mechanisms passed: 90-degree bevel and worm snapping in both directions, ratios, rack travel, rigid lock-bar platforms, collar screws, chain command/geometry/animation, undo/redo, replacement identities and binary save/load.");
        }

        private static GameObject Part(string id, Vector3 center, Quaternion? rotation = null) {
            var body = PartsManager.GeneratePart(id, center, rotation ?? Quaternion.identity);
            parts.Add(body);
            if (body.TryGetComponent<SimulationPart>(out var mechanical)) body.transform.position += center - mechanical.Center;
            return body;
        }
        private static void CheckRightAngle(string sourceId, string targetId, Vector3 center, double expectedRatio) {
            var target = Part(targetId, center).GetComponent<SimulationPart>();
            var source = Part(sourceId, center + Vector3.right * 6).GetComponent<SimulationPart>();
            var ray = new Ray(target.ContactCenter + Vector3.right * target.Radius - target.Axis * 10, target.Axis);
            Physics.SyncTransforms();
            var original = new Pose(source.transform.position, source.transform.rotation);
            Require(GearSnapping.TrySnap(ray, new GearSnapping.Source(source), original, t => t.IsChildOf(source.transform) || t == source.transform,
                out var pose, out var anchor), "No right-angle snap for " + sourceId);
            source.transform.SetPositionAndRotation(pose.position, pose.rotation);
            Require(Mathf.Abs(Vector3.Dot(source.Axis, target.Axis)) < .001f, "Shafts did not snap at 90 degrees for " + sourceId);
            Require(GearSnapping.TryTransmission(source, target, out var factor), "Snapped right-angle pair did not transmit motion for " + sourceId);
            Require(Math.Abs(Math.Abs(factor) - expectedRatio) < .0001, "Incorrect right-angle ratio for " + sourceId);
            var baseline = new Pose(target.transform.position, target.transform.rotation);
            var simulation = MechanismSimulation.Create(source.gameObject);
            Require(simulation != null && !simulation.HasConflict && simulation.tracks.Any(t => t.body == target.gameObject), "Right-angle graph was disconnected");
            var track = simulation.tracks.Single(t => t.body == target.gameObject);
            simulation.Evaluate(1);
            Near(target.transform.rotation, Quaternion.AngleAxis(90 * (float)track.ratio, track.axis) * baseline.rotation, "Right-angle animation failed");
            simulation.Evaluate(-1); simulation.Restore();
            Near(target.transform.rotation, baseline.rotation, "Right-angle cancellation failed");
            // Both authored geometry and initial placement use the same source frame.
            var rule = Object.FindFirstObjectByType<GearPlaceDisplacement>();
            var oldCast = Get<MouseCast>(rule, "mouseCast");
            var cameraObject = new GameObject("Mechanism ray camera");
            var camera = cameraObject.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 3;
            var point = target.ContactCenter + Vector3.right * target.Radius;
            camera.transform.SetPositionAndRotation(point - target.Axis * 10, Quaternion.LookRotation(target.Axis, target.Tooth));
            var cast = cameraObject.AddComponent<MouseCast>(); Set(cast, "camera", camera); Set(rule, "mouseCast", cast);
            source.transform.SetPositionAndRotation(original.position, original.rotation); Physics.SyncTransforms();
            Point(camera.WorldToScreenPoint(point));
            var generator = PartsManager.GetPartType("MCMS").GetComponent<PartGenerator>(); PartParameterEditing.Load(generator, sourceId);
            Require(rule.TryGetDisplacement(new PartPlacementData(generator, generator.GetComponent<PartType>(), rule.transform), out var placed), "Initial mechanism placement did not snap");
            source.transform.SetPositionAndRotation(placed.translation.Position, placed.rotation.Orientation);
            Require(GearSnapping.AreMeshed(source, target), "Initial mechanism placement lost contact geometry");
            Set(rule, "mouseCast", oldCast); cameraObject.SetActive(false); Object.Destroy(cameraObject);
            source.gameObject.SetActive(false); target.gameObject.SetActive(false);
        }

        private static void CheckRack(Vector3 center) {
            var gear = Part("GEAR-Normal-36T", center).GetComponent<SimulationPart>();
            var rack = Part("MCMS-Rack", center + Vector3.right * 6).GetComponent<SimulationPart>();
            var ray = new Ray(gear.Center + Vector3.up * gear.Radius - gear.Axis * 10, gear.Axis);
            Physics.SyncTransforms();
            Require(GearSnapping.TrySnap(ray, new GearSnapping.Source(rack), new Pose(rack.transform.position, rack.transform.rotation),
                t => t == rack.transform || t.IsChildOf(rack.transform), out var pose, out _), "Rack did not snap to a pinion");
            rack.transform.SetPositionAndRotation(pose.position, pose.rotation);
            Require(GearSnapping.TryTransmission(gear, rack, out var rate), "Rack/pinion contact did not connect");
            var start = rack.transform.position;
            var model = MechanismSimulation.Create(gear.gameObject);
            Require(!model.HasConflict && model.tracks.Any(t => t.body == rack.gameObject && t.linear), "Rack has no linear motion track");
            model.Evaluate(1);
            Near(rack.transform.position, start + rack.Travel * (float)(90 * rate), "Rack did not travel by the pinion pitch circumference");
            Require(!model.HasFiniteLoop, "A translating rack was shown as a repeating rotation loop");
            model.Restore(); Near(rack.transform.position, start, "Rack reset failed");
            gear.gameObject.SetActive(false); rack.gameObject.SetActive(false);
        }

        private static void CheckRigidHardware(Vector3 center) {
            var rotation = Quaternion.Euler(19, 34, 12); var axis = rotation * Vector3.forward;
            var shaft = Part("SHFT-Normal-6", center, rotation);
            var bar = Part("LKBR-Metal", center + axis * .3f, rotation);
            var outer = bar.GetComponentsInChildren<HoleCollider>().Where(h => Vector3.ProjectOnPlane(h.transform.position - bar.GetComponent<SimulationPart>().Center, axis).magnitude > .2f).First();
            var plate = Part("PLTE-3-3", outer.transform.position - axis * .12f, rotation);
            var plateHole = plate.GetComponentsInChildren<HoleCollider>().OrderBy(h => (h.transform.position - plate.transform.position).sqrMagnitude).First();
            AlignHole(plate, plateHole, outer.transform.position - axis * .12f, axis);
            var nut = Part("NUT-Hex", outer.transform.position - axis * .3f, rotation);
            AlignHole(nut, nut.GetComponentsInChildren<HoleCollider>().First(h => h.holeType == HoleCollider.HoleType.Threaded), outer.transform.position - axis * .3f, axis);
            var bolt = Part("SCRW-1.00in", outer.transform.position + axis * .09f, rotation);
            bolt.transform.forward = axis;
            var collar = Part("CLMP-Normal-Normal", center - axis * .5f, rotation);
            var threaded = collar.GetComponentsInChildren<HoleCollider>().First(h => h.holeType == HoleCollider.HoleType.Threaded);
            var setScrew = Part("SCRW-1/2in", threaded.transform.position + threaded.transform.forward * .12f);
            setScrew.transform.forward = threaded.transform.forward;
            var bearing = Part("BRNG-Normal", center - axis, rotation);
            AlignHole(bearing, bearing.GetComponentsInChildren<HoleCollider>().OrderBy(h => h.transform.localPosition.sqrMagnitude).First(), center - axis, axis);
            Physics.SyncTransforms();
            var model = MechanismSimulation.Create(shaft);
            Require(model != null && !model.HasConflict, "Rigid drivetrain failed to build");
            var moving = new[] { shaft, bar, plate, nut, bolt, collar, setScrew };
            foreach (var body in moving) Require(model.tracks.Any(t => t.body == body), "Rigid follower missing: " + body.name);
            Require(!model.tracks.Any(t => t.body == bearing), "Bearing housing became rigidly attached to the shaft");
            var before = moving.ToDictionary(b => b, b => new Pose(b.transform.position, b.transform.rotation));
            var spin = Quaternion.AngleAxis(90, axis);
            model.Evaluate(1);
            foreach (var body in moving) {
                Near(body.transform.position, center + spin * (before[body].position - center), "A platform or screw did not follow its shaft rigidly");
                Near(body.transform.rotation, spin * before[body].rotation, "A rigid attachment used its own screw axis");
            }
            model.Restore();
            foreach (var body in moving) Near(body.transform.position, before[body].position, "Rigid reset lost an attached part");
            foreach (var body in moving.Append(bearing)) body.SetActive(false);
            Debug.Log("Rigid attachments passed: lock bar, bolted platform/nut, collar set screw and independent bearing housing.");
        }

        private static void CheckChains(Vector3 center) {
            var first = Part("SPKT-Normal-10T", center).GetComponent<SimulationPart>();
            var second = Part("SPKT-Normal-40T", center + Vector3.right * 4).GetComponent<SimulationPart>();
            var shaft = Part("SHFT-Normal-6", center);
            var shaftB = Part("SHFT-Normal-6", center + Vector3.right * 4);
            var tool = Object.FindFirstObjectByType<SimulationTool>(); var manager = Get<SelectionManager>(tool, "selectionManager");
            var multi = new MultiSelection(null, new List<GameObject> { first.gameObject, second.gameObject }) { individual = true };
            manager.SetCurrent(multi, true);
            var input = Get<InputEvent>(tool, "chainInput");
            Require(input != null && input.defaultAction.bindings[0].path == "<Keyboard>/c", "Chain command must default to C");
            Require(ChainLoop.Compatible(new[] { first, second }, out var reason), "Chain fixture is incompatible: " + reason);
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); InputSystem.Update(); input.Update();
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.C)); InputSystem.Update(); input.Update();
            var loops = Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None);
            Require(loops.Length == 1, $"C did not create a chain: pressed={input.IsPressed}/{input.IsCurrentlyPressed}, enabled={input.defaultAction.enabled}, simulation={SimulationTool.IsActive}, keyboard={Protobot.Tools.KeyboardTransformTool.IsCapturingInput}, placing={Get<Placement>(tool, "placement").placing}, selection={manager.current?.GetType().Name}, feedback={typeof(Protobot.UI.InteractionFeedback).GetField("notification", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)}");
            var chain = loops.Single();
            chain.RefreshVisual();
            Require(chain.GetComponent<LineRenderer>().positionCount > 20, "Chain has no wrapped rope contour");
            var model = MechanismSimulation.Create(shaft);
            Require(!model.HasConflict && model.tracks.Any(t => t.body == shaftB), "Chain did not connect the second shaft");
            var track = model.tracks.Single(t => t.body == second.gameObject);
            Require(Math.Abs(track.ratio - .25) < .0001, "Open-chain tooth ratio or direction is wrong");
            model.Evaluate(1);
            Require(chain.GetComponent<LineRenderer>().sharedMaterial.mainTextureOffset.x != 0, "Chain graphic did not animate");
            model.Restore();
            StateSystem.instance.Undo(); Complete(); Require(!chain.gameObject.activeSelf, "Undo did not remove the chain");
            StateSystem.instance.Redo(); Complete(); Require(chain.gameObject.activeSelf, "Redo did not restore the chain");
            var id = first.GetComponent<SavedObject>().ConnectionId;
            var generator = PartsManager.GetPartType("SPKT").GetComponent<PartGenerator>(); PartParameterEditing.Load(generator, "SPKT-Normal-24T");
            var replacement = PartParameterEditing.Replace(first.gameObject, generator); parts.Add(replacement); first.gameObject.SetActive(false);
            Require(replacement.GetComponent<SavedObject>().ConnectionId == id && chain.ResolveParts().Contains(replacement.GetComponent<SimulationPart>()), "Regeneration broke chain identity");
            replacement.SetActive(false); first.gameObject.SetActive(true);
            Require(chain.ResolveParts().Contains(first), "Undoing replacement did not reconnect the original sprocket");
            first.gameObject.SetActive(false); chain.RefreshVisual(); Require(!chain.GetComponent<LineRenderer>().enabled && ChainLoop.SaveAll().Length == 0, "Deleting a sprocket left an active/dangling chain");
            first.gameObject.SetActive(true); chain.RefreshVisual(); Require(chain.GetComponent<LineRenderer>().enabled, "Undoing deletion did not restore the chain");
            var third = Part("SPKT-Normal-15T", center + new Vector3(2, 3, 0)).GetComponent<SimulationPart>();
            var triangle = ChainLoop.Create(new[] { first, second, third });
            Require(triangle != null, "A clear three-sprocket loop was rejected");
            triangle.RefreshVisual();
            Require(triangle.GetComponent<LineRenderer>().positionCount > 20, "Three-sprocket chain contour was absent");
            var multiple = MechanismSimulation.Create(shaft);
            Require(!multiple.HasConflict && Math.Abs(multiple.tracks.Single(t => t.body == third.gameObject).ratio - 2.0 / 3) < .0001,
                "Interconnected chain loops did not transmit consistent ratios");
            triangle.gameObject.SetActive(false); third.gameObject.SetActive(false);
            var copies = Object.FindFirstObjectByType<ObjectDuplicator>().CreateCopies(new[] { first.gameObject, second.gameObject });
            Require(copies.All(c => c.GetComponent<SavedObject>().ConnectionId != id && c.GetComponent<SavedObject>().ConnectionId != second.GetComponent<SavedObject>().ConnectionId),
                "Duplicated parts reused chain endpoint identities");
            ObjectDuplicator.DiscardCopies(copies);
            manager.ClearCurrent(false, true, true);
            var snapshot = SceneBuild.ToBuildData();
            Require(snapshot.chains.Length == 1, "Chain was absent from the build snapshot");
            using var memory = new MemoryStream(); var formatter = new BinaryFormatter(); formatter.Serialize(memory, snapshot); memory.Position = 0;
            var restored = (BuildData)formatter.Deserialize(memory);
            Require(snapshot.CompareData(restored), "Chain binary round trip changed the build");
            SceneBuild.GenerateBuild(restored);
            var loaded = Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).Single();
            Require(loaded.ResolveParts().All(p => p != null) && loaded.MemberIds.Contains(id), "Loaded chain did not resolve persistent endpoints");
            loaded.RefreshVisual(); Require(loaded.GetComponent<LineRenderer>().positionCount > 20, "Loaded chain graphic was missing");
            var loadedShaft = Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None).Single(s => s.id == "SHFT-Normal-6" && Vector3.Distance(s.transform.position, center) < .01f);
            var loadedModel = MechanismSimulation.Create(loadedShaft.gameObject);
            Require(loadedModel.tracks.Count(t => t.body.GetComponent<SimulationPart>()?.kind == DriveKind.Sprocket) == 2, "Loaded chain no longer transmitted motion");
        }

        public static GameObject RenderDriver { get; private set; }
        public static void PrepareRender() {
            var manager = Get<SelectionManager>(Object.FindFirstObjectByType<SimulationTool>(), "selectionManager");
            manager.ClearCurrent(false, true, true); ChainLoop.ClearAll();
            foreach (var body in PartsManager.FindLoadedObjects()) body.SetActive(false);
            GameObject Pair(string a, string b, Vector3 position) {
                var target = Part(b, position).GetComponent<SimulationPart>();
                var source = Part(a, position + Vector3.back * 5).GetComponent<SimulationPart>();
                Physics.SyncTransforms();
                var ray = new Ray(target.ContactCenter + Vector3.right * target.Radius - target.Axis * 10, target.Axis);
                Require(GearSnapping.TrySnap(ray, new GearSnapping.Source(source), new Pose(source.transform.position, source.transform.rotation),
                    t => t == source.transform || t.IsChildOf(source.transform), out var pose, out _), "Render pairing failed");
                source.transform.SetPositionAndRotation(pose.position, pose.rotation); return source.gameObject;
            }
            RenderDriver = Pair("MCMS-Bevel Gear-16T", "MCMS-Bevel Gear-32T", new Vector3(-3, 1, 0));
            Pair("MCMS-Worm and Wheel-Worm", "MCMS-Worm and Wheel-Gear", new Vector3(-.7f, 1, 0));
            var a = Part("SPKT-Normal-24T", new Vector3(1.3f, 1, 0)).GetComponent<SimulationPart>();
            var b = Part("SPKT-Normal-40T", new Vector3(3.7f, 1, 0)).GetComponent<SimulationPart>();
            ChainLoop.Create(new[] { a, b }).RefreshVisual();
            var light = new GameObject("Mechanism render light").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 2;
            light.transform.rotation = Quaternion.Euler(35, -25, 0);
            RenderSettings.ambientLight = new Color(.5f, .5f, .5f);
            PivotCamera.Main.transform.DOKill(); PivotCamera.Main.camera.transform.DOKill();
            PivotCamera.Main.SetTransform(new Vector3(.5f, 1, 0), new Vector3(15, -15, 0), -11);
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            tool.SetActive(true); tool.Begin(RenderDriver); if (!tool.AnimationVisible) tool.ToggleAnimation(); tool.Seek(.7);
        }
        private static void AlignHole(GameObject body, HoleCollider hole, Vector3 point, Vector3 axis) {
            body.transform.rotation = Quaternion.FromToRotation(hole.transform.forward, axis) * body.transform.rotation;
            body.transform.position += point - hole.transform.position;
        }
        private static void Complete() { foreach (var body in parts.Where(b => b != null)) body.transform.DOComplete(); }
        private static void Point(Vector2 point) { InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = point }); InputSystem.Update(); }
        private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .006f, message + $": {a} vs {b}");
        private static void Near(Quaternion a, Quaternion b, string message) => Require(Quaternion.Angle(a, b) < .05f, message);
    }
}
