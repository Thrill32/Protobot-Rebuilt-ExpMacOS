using System;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using Protobot;
using Protobot.Tools;
using Protobot.StateSystems;
using Protobot.Transformations;
using Protobot.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class MoveRotationValidation {
        [MenuItem("Tools/Protobot/Validate Placement Rotation")]
        public static void Validate() {
            var cameraObject = new GameObject("Rotation validation camera");
            try {
                var camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.pixelRect = new Rect(0, 0, 800, 600);
                var attachment = new Pose(new Vector3(2, 3, 4), Quaternion.Euler(25, 40, 15));
                var localHole = new Vector3(1.25f, -.5f, .2f);
                var orientation = Quaternion.Euler(15, 60, 20);
                var start = new Pose(attachment.position - orientation * localHole, orientation);
                camera.transform.position = start.position + Vector3.back * 20;
                foreach (bool snap in new[] { false, true }) {
                    var rotation = new MoveRotation();
                    var pointer = (Vector2)camera.WorldToScreenPoint(attachment.position);
                    rotation.Begin(camera, start, attachment, pointer);
                    rotation.Evaluate(pointer + Vector2.right * 100, snap);
                    Near(rotation.Evaluate(pointer + Vector2.right * 200, snap).rotation, start.rotation,
                        "Moving outward from the pivot must not rotate the part");
                    var arc = new Vector2(Mathf.Cos(23 * Mathf.Deg2Rad), Mathf.Sin(23 * Mathf.Deg2Rad)) * 200;
                    var result = rotation.Evaluate(pointer + arc, snap);
                    Near(result.position + result.rotation * localHole, attachment.position, "Off-center mounting hole moved during rotation");
                    var halfway = MoveRotation.Interpolate(start, result, attachment.position, .5f);
                    Near(halfway.position + halfway.rotation * localHole, attachment.position, "Off-center mounting hole moved during rotation easing");
                    Near(result.rotation * Quaternion.Inverse(start.rotation) * (attachment.rotation * Vector3.forward),
                        attachment.rotation * Vector3.forward, "Attachment normal changed during rotation");
                    Require(Mathf.Abs(Mathf.Abs(rotation.Degrees) - (snap ? 30 : 23)) < .01f, "Rotation snapping is incorrect.");
                    rotation.Pause(pointer + Vector2.up * 100);
                    var resumed = rotation.Evaluate(pointer + Vector2.down * 120, snap);
                    Near(resumed.position, result.position, "Camera navigation changed the rotation pivot");
                    Near(resumed.rotation, result.rotation, "Camera navigation caused a rotation jump");
                    rotation.End();
                }
                foreach (var forward in new[] { new Vector3(.8f, .1f, .5f), new Vector3(.1f, -.9f, .3f), Vector3.back }) {
                    camera.transform.rotation = Quaternion.LookRotation(forward);
                    var rotation = new MoveRotation();
                    rotation.Begin(camera, start, null, Vector2.zero);
                    Near(rotation.Axis, TransformGesture.PrimaryAxis(forward), "Free rotation did not use the closest camera axis");
                    Near(rotation.Evaluate(Vector2.right * 90, true).position, start.position, "Free rotation moved the pivot");
                    rotation.End();
                }
                ValidateNumeric(camera, start, attachment, localHole);
                Debug.Log("Placement rotation geometry passed: off-center attachment contact, arbitrary normals, snapping, camera pause and nearest-axis free rotation.");
            }
            finally { Object.DestroyImmediate(cameraObject); }
        }

        // Called from the placement Play Mode smoke with real generated parts.
        public static void CheckPlacement(Placement placement, Camera camera, MouseCast cast,
            PartPlacementData data, HoleCollider target) {
            var oldCamera = Get<Camera>(placement, "refCamera");
            var oldFocus = EventSystem.current.currentSelectedGameObject;
            try {
                typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
                Set(placement, "refCamera", camera);
                placement.StartPlacing(data);
                Point(camera, target.transform.position);
                Invoke(placement, "Update");
                var initial = Current(placement);
                var anchor = Get<Pose?>(placement, "rotationAnchor");
                Require(anchor.HasValue, "Initial hole placement did not expose its attachment axis.");
                Get<InputEvent>(placement, "toggleRotateInput").performed?.Invoke();
                Require(placement.rotating, "R did not rotate the initial placement.");
                TurnPointer(camera, Get<MoveRotation>(placement, "moveRotation"), () => Invoke(placement, "Update"));
                var rotated = Current(placement);
                Require(Quaternion.Angle(initial.rotation, rotated.rotation) > 80,
                    $"Placement did not rotate with the pointer: angle {Quaternion.Angle(initial.rotation, rotated.rotation)}, degrees {Get<MoveRotation>(placement, "moveRotation").Degrees}, pointer {MouseInput.Position}.");
                var localFace = data.attachmentPose.position - data.attachmentPose.rotation * Vector3.forward * data.attachmentDepth / 2;
                Near(rotated.position + rotated.rotation * localFace, anchor.Value.position, "Initial placement lost mating contact");
                Near(rotated.rotation * (Quaternion.Inverse(initial.rotation) * (anchor.Value.rotation * Vector3.forward)),
                    anchor.Value.rotation * Vector3.forward, "Initial placement tilted away from the mating axis");
                Get<InputEvent>(placement, "toggleRotateInput").performed?.Invoke();
                Require(!placement.rotating, "R did not resume positioning.");
                Point(camera, target.transform.position);
                Invoke(placement, "Update");
                Near(Current(placement).rotation, rotated.rotation, "Retargeting discarded the placement twist");
                Near(Current(placement).position, rotated.position, "Retargeting moved the rotated attachment");

                var existing = Object.FindObjectsByType<SavedObject>();
                int stateIndex = StateSystem.curIndex;
                Invoke(placement, "Place");
                var committed = Object.FindObjectsByType<SavedObject>().Single(saved => !existing.Contains(saved));
                Near(committed.transform.position, rotated.position, "Rotated placement commit changed position");
                Near(committed.transform.rotation, rotated.rotation, "Rotated placement commit changed orientation");
                Require(StateSystem.curIndex == stateIndex + 1, "Rotated placement must create one undo step.");
                StateSystem.instance.Undo();
                Require(committed.gameObject.IsDeleted(), "Undo did not remove the rotated placement.");
                StateSystem.instance.Redo();
                committed.transform.DOComplete();
                Near(committed.transform.rotation, rotated.rotation, "Redo lost the placement rotation");

                // A fresh placement resets rotation, while free rotation is retained
                // within the same placement when positioning resumes.
                placement.StartPlacing(data);
                InputState.Change(Mouse.current.position, new Vector2(700, 500), InputUpdateType.Dynamic);
                Physics.SyncTransforms();
                Invoke(placement, "Update");
                Require(!Get<Pose?>(placement, "rotationAnchor").HasValue, "Expected free placement away from geometry.");
                var free = Current(placement);
                Get<InputEvent>(placement, "toggleRotateInput").performed?.Invoke();
                var rotation = Get<MoveRotation>(placement, "moveRotation");
                Near(rotation.Axis, TransformGesture.PrimaryAxis(camera.transform.forward), "Free placement used the wrong axis");
                TurnPointer(camera, rotation, () => Invoke(placement, "Update"));
                var freeRotated = Current(placement);
                Near(freeRotated.position, free.position, "Free rotation moved the preview pivot");
                Get<InputEvent>(placement, "toggleRotateInput").performed?.Invoke();
                Invoke(placement, "Update");
                Near(Current(placement).rotation, freeRotated.rotation, "Free placement discarded its rotation");
                placement.transform.DOComplete();
                CheckPlacementNumeric(placement, camera, target);
                Debug.Log("Initial placement rotation passed: R toggle, attachment contact, retained twist, free pivot/axis and orientation continuity.");
            }
            finally {
                placement.StopPlacing();
                placement.placing = false;
                Set(placement, "refCamera", oldCamera);
                EventSystem.current.SetSelectedGameObject(oldFocus);
            }
        }

        private static Pose Current(Placement placement) {
            var value = Get<Displacement>(placement, "currentDisplacement");
            return new Pose(value.translation.Position, value.rotation.Orientation);
        }

        private static void ValidateNumeric(Camera camera, Pose start, Pose attachment, Vector3 localHole) {
            var rotation = new MoveRotation();
            try {
                foreach (bool global in new[] { false, true }) {
                    rotation.Begin(camera, start, attachment, Vector2.zero, initialTwist: 30);
                    rotation.NumericInput.Cycle(false);
                    if (global) rotation.NumericInput.Cycle(false);
                    SetText(rotation, "77.25");
                    Require(rotation.TrySubmitNumeric(out var pose), "Attachment numeric rotation rejected.");
                    var expected = MoveRotation.Twist(start, attachment, global ? 47.25f : 77.25f);
                    Near(pose.rotation, expected.rotation, "Numeric rotation used the wrong angle or reference frame");
                    Near(pose.position + pose.rotation * localHole, attachment.position, "Numeric entry lost mating contact");
                    Near(rotation.Evaluate(Vector2.one * 500, true).rotation, pose.rotation, "Confirmation snapped an exact numeric rotation");
                }
                rotation.Begin(camera, start, null, Vector2.zero);
                rotation.NumericInput.Cycle(false);
                rotation.NumericInput.Cycle(false);
                SetText(rotation, "22.5");
                Require(rotation.TrySubmitNumeric(out var free), "Global free numeric rotation rejected.");
                int component = rotation.Axis == Vector3.right ? 0 : rotation.Axis == Vector3.up ? 1 : 2;
                var euler = start.rotation.eulerAngles;
                euler[component] = 22.5f;
                Near(free.rotation, Quaternion.Euler(euler), "Global free rotation did not match ordinary R");
                rotation.Begin(camera, start, attachment, Vector2.zero);
                rotation.NumericInput.Cycle(false);
                SetText(rotation, "invalid");
                Require(!rotation.TrySubmitNumeric(out _), "Invalid numeric rotation was accepted.");
            }
            finally { rotation.End(); }
        }

        public static void TurnPointer(Camera camera, MoveRotation rotation, Action apply) {
            var center = (Vector2)camera.WorldToScreenPoint(rotation.Pivot);
            var vector = (Vector2)MouseInput.Position - center;
            if (vector.sqrMagnitude < 144) vector = Vector2.right * 100;
            InputState.Change(Mouse.current.position, center + vector, InputUpdateType.Dynamic);
            apply();
            InputState.Change(Mouse.current.position, center + new Vector2(-vector.y, vector.x), InputUpdateType.Dynamic);
            apply();
        }

        public static void SetText(MoveRotation rotation, string text) => typeof(TransformNumericInput).GetProperty("Text").SetValue(rotation.NumericInput, text);

        public static void Keys(params Key[] keys) {
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
            InputState.Change(Keyboard.current, new KeyboardState(keys), InputUpdateType.Dynamic);
        }

        public static void TypeText(string text) {
            foreach (char character in text) InputSystem.QueueTextEvent(Keyboard.current, character);
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        }

        private static void CheckPlacementNumeric(Placement placement, Camera camera, HoleCollider target) {
            Point(camera, target.transform.position);
            Invoke(placement, "Update");
            Keys();
            Get<InputEvent>(placement, "toggleRotateInput").performed?.Invoke();
            var rotation = Get<MoveRotation>(placement, "moveRotation");
            Require(rotation.Active && rotation.Attachment.HasValue, "Numeric probe did not lock its target.");
            var before = Current(placement);
            int stateIndex = StateSystem.curIndex;
            var module = EventSystem.current.GetComponent<InputSystemUIInputModule>();
            var row = Object.FindObjectsByType<PartDisplayUI>().First(display => display.GetComponent<Toggle>().IsInteractable());
            EventSystem.current.SetSelectedGameObject(row.gameObject);
            Slash();
            Require(placement.IsTypingRotation && KeyboardTransformTool.IsCapturingInput,
                "Slash did not open captured placement rotation entry.");
            Require(!KeyboardTransformTool.AllowsInput(Get<InputEvent>(placement, "toggleRotateInput")) &&
                KeyboardTransformTool.BlocksCameraNavigation, "Typing did not capture rotation shortcuts and the camera.");
            CheckUIEnterRouting(blocked: true);
            InputState.Change(Mouse.current.position, (Vector2)MouseInput.Position + Vector2.right * 140, InputUpdateType.Dynamic);
            Invoke(placement, "Update");
            Get<InputEvent>(placement, "placeInput").performed?.Invoke();
            Near(Current(placement).rotation, before.rotation, "Typing allowed the mouse to rotate the preview");
            Require(StateSystem.curIndex == stateIndex, "A click confirmed while typing.");
            TypeText("invalid");
            Require(rotation.NumericInput.Text == "invalid", "Placement entry did not receive text input.");
            Submit(Key.Enter);
            Require(!placement.IsTypingRotation && placement.rotating, "Invalid entry did not return to rotation.");
            Invoke(placement, "Update");
            Near(Current(placement).rotation, before.rotation, "Invalid entry caused a pointer jump");
            Slash(); Slash();
            Require(rotation.NumericInput.Mode == TransformNumericInput.InputMode.Global, "Second slash did not open global angle entry.");
            Slash();
            Require(!placement.IsTypingRotation, "Third slash did not return to mouse rotation.");
            Invoke(placement, "Update");
            Near(Current(placement).rotation, before.rotation, "Closing entry caused a pointer jump");
            Slash();
            TypeText("22.5");
            var expected = MoveRotation.Twist(before, rotation.Attachment.Value, 22.5f);
            var existing = Object.FindObjectsByType<SavedObject>();
            Submit(Key.NumpadEnter);
            var committed = Object.FindObjectsByType<SavedObject>().Single(saved => !existing.Contains(saved));
            Near(committed.transform.rotation, expected.rotation, "Numeric placement was quantized or used the wrong axis");
            Near(committed.transform.position, expected.position, "Numeric placement lost contact");
            Require(StateSystem.curIndex == stateIndex + 1, "Numeric placement did not commit as one undo step.");
            StateSystem.instance.Undo();
            Require(committed.gameObject.IsDeleted(), "Undo did not remove numeric placement.");
            StateSystem.instance.Redo(); committed.transform.DOComplete();
            Near(committed.transform.rotation, expected.rotation, "Redo lost the exact rotation");
            Debug.Log("Placement slash rotation passed: real text input, relative/global cycling, input capture, invalid recovery, pointer continuity, exact confirmation and undo/redo.");

            void Slash() {
                Keys(); Keys(Key.Slash); Invoke(placement, "LateUpdate"); Keys();
            }
            void Submit(Key key) {
                QueueKeys(); module.Process();
                QueueKeys(key);
                Require(key != Key.Enter || module.submit.action.WasPerformedThisFrame(), "UI submit did not receive the real Enter input.");
                bool on = row.GetComponent<Toggle>().isOn;
                module.Process();
                Require(placement.IsTypingRotation && row.GetComponent<Toggle>().isOn == on,
                    "UI Enter toggled the focused part row or restarted placement before numeric submission.");
                Invoke(placement, "LateUpdate");
                module.Process();
                Require(row.GetComponent<Toggle>().isOn == on, "Numeric submission leaked Enter to the focused part row.");
                QueueKeys(); module.Process();
            }
        }

        public static void QueueKeys(params Key[] keys) {
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys));
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        }

        public static void CheckUIEnterRouting(bool blocked) {
            var system = EventSystem.current;
            var module = system.GetComponent<InputSystemUIInputModule>();
            var previous = system.currentSelectedGameObject;
            var probe = new GameObject("UI Enter validation", typeof(RectTransform), typeof(Button));
            int clicks = 0;
            probe.GetComponent<Button>().onClick.AddListener(() => clicks++);
            try {
                system.SetSelectedGameObject(probe);
                foreach (var key in new[] { Key.Enter, Key.NumpadEnter }) {
                    QueueKeys(); module.Process();
                    QueueKeys(key);
                    int expectedClicks = blocked ? 0 : clicks + (module.submit.action.WasPerformedThisDynamicUpdate() ? 1 : 0);
                    module.Process();
                    Require(clicks == expectedClicks,
                        blocked ? "Enter activated another focused UI control while typing." :
                        $"Normal UI keyboard submission stopped working: key {key}, clicks {clicks}, capture {KeyboardTransformTool.BlocksUINavigation}, submit {module.submit.action.WasPerformedThisFrame()}, focus {system.currentSelectedGameObject?.name}, nav {system.sendNavigationEvents}.");
                    Require(system.sendNavigationEvents, "The UI navigation setting was not restored after dispatch.");
                }
                if (!blocked) {
                    Require(clicks > 0, "The UI submit action was not exercised.");
                    var row = Object.FindObjectsByType<PartDisplayUI>().First(display => display.GetComponent<Toggle>().IsInteractable());
                    var placement = Object.FindFirstObjectByType<Placement>();
                    bool on = row.GetComponent<Toggle>().isOn;
                    bool placing = placement.placing;
                    var data = placement.CurrentPartPlacement;
                    system.SetSelectedGameObject(row.gameObject);
                    QueueKeys(); module.Process();
                    QueueKeys(Key.Enter); module.Process();
                    Require(row.GetComponent<Toggle>().isOn == on && placement.placing == placing && placement.CurrentPartPlacement == data,
                        "Enter activated a focused part row outside rotation.");
                }
            }
            finally {
                QueueKeys(); module.Process();
                system.SetSelectedGameObject(previous);
                Object.DestroyImmediate(probe);
            }
        }
        private static void Point(Camera camera, Vector3 point) {
            InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point), InputUpdateType.Dynamic);
            Physics.SyncTransforms();
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .003f, $"{message}: {a:F5} != {b:F5}");
        private static void Near(Quaternion a, Quaternion b, string message) => Require(Quaternion.Angle(a, b) < .03f, message);
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static void Invoke(object obj, string name) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, null);
    }
}
