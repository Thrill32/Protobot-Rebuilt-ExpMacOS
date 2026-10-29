using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using DG.Tweening;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class SimulationValidation {
        private const string Key = "Protobot.SimulationSmoke";
        private static int frames;
        private static bool checkedRender, captured;
        private static readonly List<GameObject> temporary = new();
        [InitializeOnLoadMethod] private static void Resume() {
            if (SessionState.GetBool(Key, false)) EditorApplication.update += Tick;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        public static void MechanismsSmoke() {
            SessionState.SetBool(Key + ".Mechanisms", true);
            Smoke();
        }
        public static void MechanismsRender() {
            SessionState.SetBool(Key + ".Mechanisms", true);
            Render();
        }
        public static void Render() {
            SessionState.SetBool(Key + ".Render", true);
            File.Delete("/tmp/protobot-simulation.png");
            Smoke();
        }
        private static void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            try {
                if (!checkedRender) {
                    Check();
                    if (SessionState.GetBool(Key + ".Mechanisms", false)) MechanismValidation.Check();
                    Debug.Log("Simulation smoke passed: generated motor/shafts/collars/gears, ratio propagation, stationary housings/bearings, reverse/large-time evaluation, reset/cancel, click commit, undo/redo, preview-safe saves, wrapped mouse scrubbing, timeline controls, unbound/rebound actions and placement/Move/GH gear snapping.");
                    if (!SessionState.GetBool(Key + ".Render", false)) { Finish(0); return; }
                    checkedRender = true; frames = 0;
                    GameObject.Find("Splash Screen")?.SetActive(false);
                    if (SessionState.GetBool(Key + ".Mechanisms", false)) {
                        var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
                        typeof(Protobot.Tools.KeyboardTransformTool).GetField("finishedFrame", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, -1);
                        MechanismValidation.PrepareRender();
                    }
                    else {
                    var shaft = temporary.First(o => o != null && o.GetComponent<Shaft>() != null);
                    var tool = Object.FindFirstObjectByType<SimulationTool>();
                    var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
                    // Headless input checks deliberately have no focused window; begin a fresh displayed session.
                    typeof(Protobot.Tools.KeyboardTransformTool).GetField("finishedFrame", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, -1);
                    tool.Activate(); tool.Begin(shaft); tool.TogglePlayback(); tool.Seek(.6);
                    PivotCamera.Main.transform.DOKill(); PivotCamera.Main.camera.transform.DOKill();
                    PivotCamera.Main.SetTransform(shaft.transform.position + shaft.transform.right * .5f, new Vector3(23, 37, 11), -8);
                    }
                    Canvas.ForceUpdateCanvases();
                }
                var game = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); game.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
                var displayed = Object.FindFirstObjectByType<SimulationTool>();
                if (displayed.Current == null) displayed.Begin(SessionState.GetBool(Key + ".Mechanisms", false) ? MechanismValidation.RenderDriver : temporary.First(o => o != null && o.GetComponent<Shaft>() != null));
                displayed.Seek(.6);
                if (!captured && frames > 25) { ScreenCapture.CaptureScreenshot("/tmp/protobot-simulation.png"); captured = true; }
                if (captured && File.Exists("/tmp/protobot-simulation.png")) { Debug.Log("Simulation render captured: /tmp/protobot-simulation.png"); Finish(0); }
                if (frames > 300) throw new TimeoutException("Simulation render did not complete");
            }
            catch (Exception ex) { Debug.LogException(ex); Finish(1); }
        }
        private static void Finish(int code) { EditorApplication.update -= Tick; SessionState.SetBool(Key, false); SessionState.SetBool(Key + ".Render", false); SessionState.SetBool(Key + ".Mechanisms", false); EditorApplication.Exit(code); }

        public static void Validate() {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            Require(tool != null, "Main scene has no simulation tool.");
            foreach (var binding in SimulationSetup.Bindings) {
                var input = Get<InputEvent>(tool, binding.field);
                Require(input != null && input.name == binding.name, "Missing simulation binding " + binding.name);
                Require(binding.path == null ? input.defaultAction.bindings.Count == 0 : input.defaultAction.bindings[0].path == binding.path,
                    "Unexpected default for " + binding.name);
                Require(Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(r =>
                    new SerializedObject(r).FindProperty("inputEvent").objectReferenceValue == input), "Missing Preferences row " + binding.name);
            }
            Require(tool.GetComponent<Toggle>().group == Object.FindFirstObjectByType<Protobot.Tools.ConnectTool>().GetComponent<Toggle>().group,
                "Simulation must use the existing toolbar ToggleGroup.");
            var placement = Object.FindFirstObjectByType<Placement>();
            var list = new SerializedObject(placement).FindProperty("placePositions");
            var rules = Enumerable.Range(0, list.arraySize).Select(i => list.GetArrayElementAtIndex(i).objectReferenceValue).ToList();
            Require(rules.FindIndex(r => r is GearPlaceDisplacement) > rules.FindIndex(r => r is PartHolePlaceDisplacement) &&
                rules.FindIndex(r => r is GearPlaceDisplacement) < rules.FindIndex(r => r is PlanePlaceDisplacement), "Gear rule must preserve hole priority.");
            foreach (var part in AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Part Prefabs/4Motion/Gears and Sprockets/Gear.prefab").GetComponentsInChildren<PartData>(true)) {
                var gear = part.GetComponent<SimulationPart>();
                Require(gear != null && gear.teeth >= 12 && gear.Radius > 0 && gear.Axis.sqrMagnitude > .99f, "Invalid gear metadata " + part.name);
            }
            Debug.Log("Simulation scene, toolbar, gear metadata, Preferences and unbound action validation passed.");
        }

        public static void ValidateRelated() {
            Validate();
            FastenerGroupingValidation.ValidateRelated();
            HolePlacementValidation.Validate();
        }

        private static GameObject Part(string id, Vector3 position, Quaternion rotation) {
            var part = PartsManager.GeneratePart(id, position, rotation); temporary.Add(part); return part;
        }
        private static void Check() {
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            var manager = Get<SelectionManager>(tool, "selectionManager");
            var center = new Vector3(3500, 2200, -1700);
            var rotation = Quaternion.Euler(23, 37, 11);
            var axis = rotation * Vector3.forward;
            var right = rotation * Vector3.right;
            var shaft = Part("SHFT-Normal-6", center, rotation);
            var gearA = Part("GEAR-Normal-12T", center, rotation).GetComponent<SimulationPart>();
            gearA.transform.position += center - gearA.Center;
            var gearB = Part("GEAR-High Strength v2-36T", center, rotation).GetComponent<SimulationPart>();
            var otherCenter = center + right * (gearA.Radius + gearB.Radius);
            gearB.transform.position += otherCenter - gearB.Center;
            var shaftB = Part("SHFT-Normal-6", otherCenter, rotation);
            var collar = Part("CLMP-Normal-Normal", center + axis, rotation);
            var clamp = collar.GetComponentsInChildren<HoleCollider>().First(h => h.holeType == HoleCollider.HoleType.Clamp);
            collar.transform.position += center + axis - clamp.transform.position;
            var bearing = Part("BRNG-Normal", center - axis, rotation);
            var motor = Part("MOTR-11W", center - axis * 2, rotation);
            motor.transform.rotation = Quaternion.FromToRotation(motor.GetComponent<Motor>().ShaftHole.transform.forward, axis) * motor.transform.rotation;
            motor.transform.position += center - axis * 2 - motor.GetComponent<Motor>().ShaftHole.transform.position;
            var housingStart = motor.transform.rotation;
            var bearingStart = bearing.transform.rotation;
            var initial = temporary.ToDictionary(o => o, o => new Pose(o.transform.position, o.transform.rotation));
            Physics.SyncTransforms();
            var simulation = MechanismSimulation.Create(motor);
            Require(simulation != null && !simulation.HasConflict && simulation.HasMotion, "Motor did not discover a drivetrain.");
            Require(simulation.tracks.Any(t => t.body == shaft) && simulation.tracks.Any(t => t.body == collar) && simulation.tracks.Any(t => t.body == shaftB), "Shaft/collar/gear propagation is incomplete.");
            var a = simulation.tracks.Single(t => t.body == gearA.gameObject);
            var b = simulation.tracks.Single(t => t.body == gearB.gameObject);
            Require(Math.Abs(b.ratio / a.ratio + 1.0 / 3) < .00001, "12:36 gears did not reverse at 1:3.");
            Require(Math.Abs(simulation.LoopDuration - 12) < .00001, "Compound loop duration is incorrect.");
            simulation.Evaluate(1);
            Near(gearA.transform.rotation, Quaternion.AngleAxis(90 * (float)a.ratio, a.axis) * a.start.rotation, "Input gear rotation");
            Near(gearB.transform.rotation, Quaternion.AngleAxis(90 * (float)b.ratio, b.axis) * b.start.rotation, "Driven gear rotation");
            Near(motor.transform.rotation, housingStart, "Motor housing moved");
            Near(bearing.transform.rotation, bearingStart, "Bearing moved");
            simulation.Evaluate(-1); simulation.Evaluate(12000001); simulation.Evaluate(0);
            foreach (var part in temporary) { Near(part.transform.position, initial[part].position, "Reverse/large-time positional drift"); Near(part.transform.rotation, initial[part].rotation, "Reverse/large-time angular drift"); }
            simulation.Restore();
            var inverse = MechanismSimulation.Create(gearB.gameObject);
            Require(Math.Abs(Math.Abs(inverse.tracks.Single(t => t.body == shaft).ratio) - 3) < .00001, "Driving from the output gear failed.");
            inverse.Restore();

            CheckGearPattern();
            CheckSnapping(gearA, gearB, tool);
            foreach (var pair in initial) pair.Key.transform.SetPositionAndRotation(pair.Value.position, pair.Value.rotation);
            Physics.SyncTransforms();
            EventSystem.current.SetSelectedGameObject(null);
            manager.SetCurrent(new ObjectSelection { gameObject = shaft, individual = true }, true);
            Point(new Vector2(400, 450));
            tool.Activate();
            Require(SimulationTool.IsActive && tool.Current != null, "Tool 5 did not use the current shaft selection.");
            Set(tool, "beganFrame", -1);
            Set(tool, "lastPointer", new Vector2(400, 450));
            var camera = Get<Camera>(tool, "refCamera"); var oldRect = camera.pixelRect;
            camera.pixelRect = new Rect(0, 0, 800, 600);
            Point(new Vector2(490, 450)); Invoke(tool, "ReadInput", Mouse.current, Keyboard.current);
            Require(Math.Abs(tool.Current.Time - 1) < .001, $"Horizontal pointer motion did not scrub: t={tool.Current.Time}, focused={Application.isFocused}, UI={EventSystem.current.IsPointerOverGameObject()}, panel={SimulationTool.OverPanel}, pointer={MouseInput.Position}, selected={EventSystem.current.currentSelectedGameObject}");
            Set(tool, "lastPointer", new Vector2(709, 450));
            Point(new Vector2(799, 450)); Invoke(tool, "ReadInput", Mouse.current, Keyboard.current);
            Require(Math.Abs(Mouse.current.position.ReadValue().x - 12) < .1, "Pointer did not wrap at viewport edge.");
            double before = tool.Current.Time;
            Point(new Vector2(57, 450)); Invoke(tool, "ReadInput", Mouse.current, Keyboard.current);
            Require(Math.Abs(tool.Current.Time - before - .5) < .001, "Wrapping introduced a discontinuity.");
            camera.pixelRect = oldRect;
            tool.Seek(1);
            var saved = SceneBuild.ToBuildData().parts.Single(p => p.partId == "GEAR-Normal-12T");
            Near(saved.GetPos(), initial[gearA.gameObject].position, "Save captured a transient position");
            Near(saved.GetRot(), initial[gearA.gameObject].rotation, "Save captured a transient rotation");
            tool.Activate(); Require(tool.AnimationVisible, "Second 5 did not open the animation controls.");
            tool.TogglePlayback(); Require(tool.IsPlaying, "Play failed");
            tool.TogglePlayback(); Require(!tool.IsPlaying, "Pause failed");
            tool.SetSpeed(2); Require(tool.Speed == 2, "Speed control failed");
            tool.ResetMotion(); Near(gearA.transform.rotation, initial[gearA.gameObject].rotation, "Reset failed");
            tool.Seek(1); tool.Cancel();
            foreach (var pair in initial) Near(pair.Key.transform.rotation, pair.Value.rotation, "Cancel did not restore all bodies");
            Require(tool.Begin(shaft), "Could not restart preview");
            tool.Seek(1);
            var selectedPose = shaft.transform.rotation;
            var stateIndex = Protobot.StateSystems.StateSystem.curIndex;
            // Click, rather than a direct Commit call, must be the only manual acceptance path.
            tool.ToggleAnimation();
            Set(tool, "beganFrame", -1);
            Point(new Vector2(490, 450)); Set(tool, "lastPointer", new Vector2(490, 450));
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = new Vector2(490, 450), buttons = 1 }); InputSystem.Update();
            Invoke(tool, "ReadInput", Mouse.current, Keyboard.current);
            Require(Protobot.StateSystems.StateSystem.curIndex == stateIndex + 1, "Commit must create exactly one undo state");
            Protobot.StateSystems.StateSystem.instance.Undo(); Complete();
            Near(shaft.transform.rotation, initial[shaft].rotation, "Undo failed");
            Protobot.StateSystems.StateSystem.instance.Redo(); Complete();
            Near(shaft.transform.rotation, selectedPose, "Redo failed");
            // Leaving via another edit restores the preview before that edit can observe a pose.
            tool.Activate(); tool.Seek(.7); SimulationTool.ExitForEdit();
            Near(shaft.transform.rotation, selectedPose, "Tool exit did not restore");
            Require(!SimulationTool.IsActive, "Tool exit left capture active");
            CheckReboundActions(tool, shaft);
            CheckNumericRotation(tool, shaft, motor, gearA, gearB, bearing);
            CheckKeyboardGear(gearA, gearB, manager);
            Debug.Log("Simulation graph and tool checks passed.");
        }

        private static void CheckNumericRotation(SimulationTool tool, GameObject shaft, GameObject motor,
            SimulationPart gearA, SimulationPart gearB, GameObject bearing) {
            Keys(); Point(new Vector2(490, 450));
            tool.Activate(); Require(tool.Begin(shaft), "Could not start numeric simulation");
            var model = tool.Current;
            var starts = model.tracks.ToDictionary(t => t.body, t => t.start);
            var inputTrack = model.tracks.Single(t => t.body == gearA.gameObject);
            var outputTrack = model.tracks.Single(t => t.body == gearB.gameObject);
            var index = Protobot.StateSystems.StateSystem.curIndex;
            var housing = motor.transform.rotation; var bearingRotation = bearing.transform.rotation;
            Press(UnityEngine.InputSystem.Key.Slash);
            Require(tool.IsTypingRotation && SimulationTool.BlocksCamera, "Slash did not capture simulation numeric input");
            foreach (var field in new[] { "activateInput", "playInput", "chainInput", "commitInput" })
                Require(!SimulationTool.AllowsInput(Get<InputEvent>(tool, field)), "Typing allowed " + field);
            Type("-450.5");
            tool.Commit(); tool.TogglePlayback(); tool.ResetMotion();
            Require(tool.IsTypingRotation && tool.Current == model && !tool.IsPlaying && model.Time == 0, "UI actions interrupted numeric entry");
            Press(UnityEngine.InputSystem.Key.Enter);
            Require(!tool.IsTypingRotation && tool.Current == null && Protobot.StateSystems.StateSystem.curIndex == index + 1,
                "Numeric Enter must commit exactly one undoable pose");
            Near(gearA.transform.rotation, Quaternion.AngleAxis(-450.5f * (float)inputTrack.ratio, inputTrack.axis) * inputTrack.start.rotation, "Exact relative driver rotation");
            var expectedOutput = Quaternion.AngleAxis(-450.5f * (float)outputTrack.ratio, outputTrack.axis) * outputTrack.start.rotation;
            Near(gearB.transform.rotation, expectedOutput, "Relative numeric input lost full turns or gear ratio");
            Near(motor.transform.rotation, housing, "Numeric entry rotated the motor housing");
            Near(bearing.transform.rotation, bearingRotation, "Numeric entry rotated the bearing");
            Protobot.StateSystems.StateSystem.instance.Undo(); Complete();
            foreach (var pair in starts) Near(pair.Key.transform.rotation, pair.Value.rotation, "Numeric undo missed a connected body");
            Protobot.StateSystems.StateSystem.instance.Redo(); Complete();
            Near(gearB.transform.rotation, expectedOutput, "Numeric redo missed the output gear");
            Protobot.StateSystems.StateSystem.instance.Undo(); Complete();

            Keys(); if (!SimulationTool.IsActive) tool.Activate();
            Require(tool.Begin(shaft), "Could not restart numeric simulation");
            model = tool.Current;
            tool.Seek(8.25); tool.TogglePlayback();
            Press(UnityEngine.InputSystem.Key.NumpadDivide);
            Require(!tool.IsPlaying && tool.AnimationVisible && SimulationTool.BlocksCamera, "Numeric input did not pause animation and capture the camera");
            Type("NaN"); Press(UnityEngine.InputSystem.Key.Enter);
            Require(tool.Current != null && !tool.IsTypingRotation && Math.Abs(tool.Current.Time - 8.25) < .00001,
                "Invalid input changed the preview or failed to resume");
            Press(UnityEngine.InputSystem.Key.Slash); Press(UnityEngine.InputSystem.Key.Slash);
            Require(Get<Protobot.Tools.TransformNumericInput>(tool, "numericInput").Mode == Protobot.Tools.TransformNumericInput.InputMode.Global,
                "Second slash did not enter global rotation");
            Type("30.125"); Press(UnityEngine.InputSystem.Key.NumpadEnter);
            var axis = model.DriverAxis;
            float global = Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, axis), Vector3.ProjectOnPlane(shaft.transform.up, axis), axis);
            Require(Mathf.Abs(Mathf.DeltaAngle(global, 30.125f)) < .01f, "Global rotation did not reach the exact world-referenced phase");
            Require(Math.Abs(model.DriverDegrees) > 360, "Global input unexpectedly rewound full driver turns");
            Near(gearB.transform.rotation, Quaternion.AngleAxis((float)(model.DriverDegrees * outputTrack.ratio), outputTrack.axis) * outputTrack.start.rotation,
                "Global input did not propagate the preserved gear cycle");
            Protobot.StateSystems.StateSystem.instance.Undo(); Complete();
            foreach (var pair in starts) Near(pair.Key.transform.rotation, pair.Value.rotation, "Global numeric undo missed a body");

            Keys(); if (!SimulationTool.IsActive) tool.Activate();
            Require(tool.Begin(shaft), "Could not start numeric resume check");
            tool.Seek(.75);
            Press(UnityEngine.InputSystem.Key.Slash); Type("123");
            Press(UnityEngine.InputSystem.Key.Slash); Press(UnityEngine.InputSystem.Key.Slash);
            Require(!tool.IsTypingRotation && Math.Abs(tool.Current.Time - .75) < .00001, "Third slash applied discarded text");
            if (tool.AnimationVisible) tool.ToggleAnimation();
            Set(tool, "beganFrame", -1);
            Keys(); Invoke(tool, "ReadInput", Mouse.current, Keyboard.current);
            Require(Math.Abs(tool.Current.Time - .75) < .00001, "Closing numeric input caused a mouse jump");
            Press(UnityEngine.InputSystem.Key.Slash); Type("120"); Press(UnityEngine.InputSystem.Key.Escape);
            Require(tool.Current == null && !tool.IsTypingRotation, "Escape did not close entry and revert the preview");
            foreach (var pair in starts) Near(pair.Key.transform.rotation, pair.Value.rotation, "Numeric cancellation missed a body");

            Keys(); Require(tool.Begin(motor), "Could not start motor numeric check");
            model = tool.Current;
            Require(model.TrySetDriverRotation("360", false), "Full-turn motor input rejected");
            Near(gearB.transform.rotation, Quaternion.AngleAxis(360 * (float)model.tracks.Single(t => t.body == gearB.gameObject).ratio, outputTrack.axis) * outputTrack.start.rotation,
                "Motor numeric input did not drive the output");
            Require(!model.TrySetDriverRotation("1e309", false) && !model.TrySetDriverRotation("bad", true), "Invalid numeric values accepted");
            Near(motor.transform.rotation, housing, "Motor numeric output moved the casing");
            Press(UnityEngine.InputSystem.Key.Slash); Invoke(tool, "OnApplicationFocus", false);
            Require(!tool.IsTypingRotation && tool.Current == null, "Focus loss left a numeric subscription or preview");
            Keys(); SimulationTool.ExitForEdit();
            Debug.Log("Simulation numeric rotation passed: real slash/text/Enter flow, relative full turns, global phase on an oblique axis, transmission, undo/redo, modal ownership, invalid recovery, pointer resume, motor output and cancellation.");

            void Press(UnityEngine.InputSystem.Key key) { Keys(); Keys(key); Invoke(tool, "ReadInput", Mouse.current, Keyboard.current); }
            void Type(string text) {
                foreach (char character in text) InputSystem.QueueTextEvent(Keyboard.current, character);
                InputSystem.Update();
                Require(Get<Protobot.Tools.TransformNumericInput>(tool, "numericInput").Text == text, "Simulation numeric text was not delivered");
            }
        }

        private static void CheckReboundActions(SimulationTool tool, GameObject shaft) {
            Point(new Vector2(490, 450));
            tool.Activate(); Require(tool.Begin(shaft), "Could not start rebound-action check");
            // This synchronous probe simulates a new interaction in the same
            // editor frame as the preceding commit/cancel checks.
            typeof(SimulationTool).GetField("consumedFrame", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, -1);
            var input = Get<InputEvent>(tool, "playInput");
            var original = input.rebindAction.action;
            using var rebound = new InputAction("Simulation rebound probe", InputActionType.Button, "<Keyboard>/f8");
            input.rebindAction.action = rebound; rebound.Enable();
            try {
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); InputSystem.Update(); input.Update();
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.F8)); InputSystem.Update(); input.Update();
                Require(tool.IsPlaying && tool.AnimationVisible, "Rebound Play/Pause input did not start playback");
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); InputSystem.Update(); input.Update();
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.F8)); InputSystem.Update(); input.Update();
                Require(!tool.IsPlaying, "Rebound Play/Pause input did not pause playback");
                tool.Seek(.5); Invoke(tool, "OnApplicationFocus", false);
                Require(tool.Current == null, "Focus loss retained a transient pose");
            }
            finally { input.rebindAction.action = original; SimulationTool.ExitForEdit(); }
        }

        private static void CheckSnapping(SimulationPart source, SimulationPart target, SimulationTool tool) {
            var pivot = new Pose(source.transform.position, source.transform.rotation);
            var pose = GearSnapping.Align(new GearSnapping.Source(source), target, -target.Tooth, pivot);
            source.transform.SetPositionAndRotation(pose.position, pose.rotation);
            Require(GearSnapping.AreMeshed(source, target), "Pitch circle alignment failed");
            Require(Vector3.Dot(source.Axis, target.Axis) > .999f, "Gear axes were not aligned");
            float phaseA = Vector3.SignedAngle(source.Tooth, target.Center - source.Center, target.Axis) * source.teeth / 360;
            float phaseB = Vector3.SignedAngle(target.Tooth, source.Center - target.Center, target.Axis) * target.teeth / 360;
            Require(Mathf.Abs(Mathf.Repeat(phaseA + phaseB, 1) - .5f) < .005f, "Teeth were not phased into gaps");
            var point = target.Center + target.Tooth * target.Radius;
            var ray = new Ray(point - target.Axis * 10, target.Axis);
            Physics.SyncTransforms();
            Require(GearSnapping.TrySnap(ray, new GearSnapping.Source(source), new Pose(source.transform.position, source.transform.rotation),
                t => t == source.transform || t.IsChildOf(source.transform), out var snap, out _), "Circumference vicinity ray failed");
            source.transform.SetPositionAndRotation(snap.position, snap.rotation);
            Require(GearSnapping.AreMeshed(source, target), "Vicinity snap did not produce a valid mesh");
            // Exercise the actual placement rule with its live pointer ray.
            var cameraObject = new GameObject("Gear snapping validation camera"); temporary.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 5;
            camera.transform.SetPositionAndRotation(point - target.Axis * 10, Quaternion.LookRotation(target.Axis, target.Tooth));
            camera.pixelRect = new Rect(0, 0, 800, 600);
            var cast = cameraObject.AddComponent<MouseCast>(); Set(cast, "camera", camera);
            var rule = Get<Placement>(tool, "placement").GetComponent<GearPlaceDisplacement>();
            var oldCast = Get<MouseCast>(rule, "mouseCast"); Set(rule, "mouseCast", cast);
            Point(camera.WorldToScreenPoint(point));
            var generator = PartsManager.GetPartType("GEAR").GetComponent<PartGenerator>();
            PartParameterEditing.Load(generator, "GEAR-Normal-12T");
            var partPlacement = new PartPlacementData(generator, generator.GetComponent<PartType>(), rule.transform);
            Require(rule.TryGetDisplacement(partPlacement, out var normalPlacement),
                $"New gear placement did not vicinity snap: pointer {MouseInput.Position}, ray {cast.ray}, target {target.Center}, aim {point}, metadata {generator.TryGetPartData(out var data)}/{data?.GetComponent<SimulationPart>() != null}");
            var moving = new GameObjectPlacementData(source.gameObject, rule.transform, true);
            Require(rule.TryGetDisplacement(moving, out var normalMove), "Move did not vicinity snap");
            var bypassInput = Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None).Single(i => i.name == "Snap Bypass");
            var oldDefault = bypassInput.defaultAction; var oldRebound = bypassInput.rebindAction.action;
            using var bypass = new InputAction("Gear bypass probe", InputActionType.Button, "<Keyboard>/f9");
            using var unbound = new InputAction("Unbound gear bypass default");
            bypassInput.defaultAction = unbound; bypassInput.rebindAction.action = bypass; bypass.Enable();
            try {
                Keys(UnityEngine.InputSystem.Key.F9);
                Require(SnapBypassInput.IsHeld, "Rebound gear Snap Bypass was not recognized");
                var freeSource = new GearSnapping.Source(source);
                Require(GearSnapping.TrySnap(cast.ray, freeSource, new Pose(source.transform.position, source.transform.rotation),
                    t => t == source.transform || t.IsChildOf(source.transform), out var expectedMove, out _, true), "Free Move target was unavailable");
                Require(rule.TryGetDisplacement(moving, out var bypassMove), "Snap Bypass disabled Move gear snapping");
                Near(bypassMove.translation.Position, expectedMove.position, "Move did not honor rebound Snap Bypass");
                Require(generator.TryGetPartData(out var variant), "Missing placement gear variant");
                var metadata = variant.GetComponent<SimulationPart>();
                var localSource = new GearSnapping.Source(metadata.localCenter, metadata.localAxis, metadata.localTooth, metadata.pitchRadius, metadata.teeth);
                Require(GearSnapping.TrySnap(cast.ray, localSource, Pose.identity, null, out var expectedPlacement, out _, true), "Free placement target was unavailable");
                Require(rule.TryGetDisplacement(partPlacement, out var bypassPlacement), "Snap Bypass disabled initial gear snapping");
                Near(bypassPlacement.translation.Position, expectedPlacement.position, "Initial placement did not honor rebound Snap Bypass");
                Keys();
                Require(rule.TryGetDisplacement(partPlacement, out var returnedPlacement), "Releasing bypass lost initial gear snapping");
                Require(rule.TryGetDisplacement(moving, out var returnedMove), "Releasing bypass lost Move gear snapping");
                Near(returnedPlacement.translation.Position, normalPlacement.translation.Position, "Initial placement did not return to the pattern");
                Near(returnedMove.translation.Position, normalMove.translation.Position, "Move did not return to the pattern");
            }
            finally { Keys(); bypassInput.defaultAction = oldDefault; bypassInput.rebindAction.action = oldRebound; }
            Set(rule, "mouseCast", oldCast);
            cameraObject.SetActive(false);
            Debug.Log("Gear geometry and actual placement rule checks passed.");
        }

        private static void CheckGearPattern() {
            var target = Part("GEAR-High Strength v2-84T", new Vector3(3550, 2200, -1700), Quaternion.identity).GetComponent<SimulationPart>();
            var source = new GearSnapping.Source(Vector3.zero, Vector3.forward, Vector3.up, .75f, 36);
            float angle = 58 * Mathf.Deg2Rad;
            var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
            Ray Aim(Vector3 radial) => new Ray(target.Center + radial * target.Radius - target.Axis * 10, target.Axis);
            Physics.SyncTransforms();
            Require(GearSnapping.TrySnap(Aim(direction), source, Pose.identity, null, out var snapped, out var anchor), "Virtual gear pattern required an actual hole");
            Near(snapped.position, target.Center + new Vector3(1.5f, 2, 0), "Nearest valid diagonal half-inch position was not chosen");
            Near(anchor.position, snapped.position, "Rotation anchor did not follow the pattern center");
            Require(GearSnapping.TrySnap(Aim(direction), source, Pose.identity, null, out var free, out _, true), "Bypass did not allow continuous gear placement");
            Near(free.position, target.Center + direction * (source.radius + target.Radius), "Bypass was still quantized to the pattern");
            Require(Vector3.Distance(free.position, snapped.position) > .1f, "Bypass did not change a non-pattern pointer angle");
            target.transform.RotateAround(target.Center, target.Axis, 31);
            Physics.SyncTransforms();
            Require(GearSnapping.TrySnap(Aim(direction), source, Pose.identity, null, out var afterRotation, out _), "Rotating target gear lost its pattern");
            Near(afterRotation.position, snapped.position, "Virtual pattern rotated with the target gear's teeth");

            var rotation = Quaternion.Euler(23, 37, 11);
            target.transform.rotation = rotation;
            var bearing = Part("BRNG-Normal", target.Center - target.Axis, rotation);
            var bore = bearing.GetComponentsInChildren<HoleCollider>().OrderBy(h => h.transform.localPosition.sqrMagnitude).First();
            bearing.transform.rotation = Quaternion.FromToRotation(bore.transform.forward, target.Axis) * bearing.transform.rotation;
            bearing.transform.position += target.Center - target.Axis - bore.transform.position;
            var x = Vector3.ProjectOnPlane(bore.transform.right, target.Axis).normalized;
            var y = Vector3.Cross(target.Axis, x).normalized;
            direction = x * Mathf.Cos(angle) + y * Mathf.Sin(angle);
            Physics.SyncTransforms();
            Require(GearSnapping.TrySnap(Aim(direction), source, Pose.identity, null, out var rotated, out _), "Rotated mounting frame did not snap");
            Near(rotated.position, target.Center + x * 1.5f + y * 2, "Virtual pattern did not follow the stationary mounting frame");
            target.transform.RotateAround(target.Center, target.Axis, 29);
            Physics.SyncTransforms();
            Require(GearSnapping.TrySnap(Aim(direction), source, Pose.identity, null, out var mountedRotation, out _), "Mounted gear rotation lost its pattern");
            Near(mountedRotation.position, rotated.position, "Mounted pattern followed the spinning gear");

            var incompatible = Part("GEAR-High Strength v2-24T", new Vector3(3570, 2200, -1700), Quaternion.identity).GetComponent<SimulationPart>();
            var small = new GearSnapping.Source(Vector3.zero, Vector3.forward, Vector3.up, .25f, 12);
            var ray = new Ray(incompatible.Center + Vector3.up * incompatible.Radius - Vector3.forward * 10, Vector3.forward);
            Physics.SyncTransforms();
            Require(!GearSnapping.TrySnap(ray, small, Pose.identity, null, out _, out _), "A gear pair without a compatible half-inch offset snapped freely");
            Require(GearSnapping.TrySnap(ray, small, Pose.identity, null, out _, out _, true), "Bypass did not allow a nonstandard center spacing");
            target.gameObject.SetActive(false); bearing.SetActive(false); incompatible.gameObject.SetActive(false);
            Debug.Log("Virtual VEX gear pattern passed: absent holes, nearest diagonal spacing, exact lattice centers, free bypass, stable tooth rotation, rotated mounting frames and incompatible-spacing rejection.");
        }

        private static void CheckKeyboardGear(SimulationPart source, SimulationPart target, SelectionManager manager) {
            var tool = Object.FindFirstObjectByType<Protobot.Tools.KeyboardTransformTool>();
            var oldCamera = Get<Camera>(tool, "refCamera"); var oldCast = Get<MouseCast>(tool, "mouseCast");
            var start = new Pose(source.transform.position, source.transform.rotation);
            source.transform.position -= target.Tooth * 5;
            var movedStart = new Pose(source.transform.position, source.transform.rotation);
            var point = target.Center + target.Tooth * target.Radius;
            var obj = new GameObject("G/H gear camera"); temporary.Add(obj);
            var camera = obj.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 5;
            camera.transform.SetPositionAndRotation(point - target.Axis * 10, Quaternion.LookRotation(target.Axis, target.Tooth));
            var cast = obj.AddComponent<MouseCast>(); Set(cast, "camera", camera);
            Set(tool, "refCamera", camera); Set(tool, "mouseCast", cast);
            manager.SetCurrent(new ObjectSelection { gameObject = source.gameObject, individual = true }, true);
            Physics.SyncTransforms(); Point(camera.WorldToScreenPoint(point));
            Invoke(tool, "BeginSelection", false); Invoke(tool, "ToggleHoleSnap");
            Require(Get<bool>(tool, "hasSourceHole"), "G/H did not automatically choose the gear center");
            Invoke(tool, "Apply", (Vector2)MouseInput.Position);
            Get<Tween>(tool, "smoothingTween")?.Complete();
            Require(Get<bool>(tool, "hasMoveTarget") && GearSnapping.AreMeshed(source, target), "G/H did not mesh the gear near a circumference");
            Invoke(tool, "Finish", false);
            Near(source.transform.position, movedStart.position, "G/H gear cancellation lost the starting position");
            Near(source.transform.rotation, movedStart.rotation, "G/H gear cancellation lost the starting rotation");
            source.transform.SetPositionAndRotation(start.position, start.rotation);
            Set(tool, "refCamera", oldCamera); Set(tool, "mouseCast", oldCast); obj.SetActive(false);
        }

        private static void Complete() { foreach (var obj in temporary.Where(o => o != null)) obj.transform.DOComplete(); }
        private static void Point(Vector2 position) { InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); InputSystem.Update(); }
        private static void Keys(params UnityEngine.InputSystem.Key[] keys) { InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys)); InputSystem.Update(); }
        private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static object Invoke(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .005f, message + $": {a} vs {b}");
        private static void Near(Quaternion a, Quaternion b, string message) => Require(Quaternion.Angle(a, b) < .05f, message + $": {a.eulerAngles} vs {b.eulerAngles}");
    }
}
