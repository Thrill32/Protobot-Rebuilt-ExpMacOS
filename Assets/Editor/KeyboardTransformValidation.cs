using System;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using KeyboardState = UnityEngine.InputSystem.LowLevel.KeyboardState;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    /// <summary>Run from the menu or with -executeMethod for deterministic transform checks.</summary>
    public static class KeyboardTransformValidation {
        [MenuItem("Tools/Protobot/Validate Keyboard Transforms")]
        public static void Validate() {
            ValidateScene();
            ValidatePlacementRotationDisabled();
            var cameraObject = new GameObject("Gesture validation camera");
            try {
                var camera = cameraObject.AddComponent<Camera>();
                camera.pixelRect = new Rect(0f, 0f, 800f, 600f);
                camera.orthographicSize = 10f;
                foreach (bool orthographic in new[] { true, false }) {
                    camera.orthographic = orthographic;
                    camera.transform.SetPositionAndRotation(new Vector3(8f, 6f, -20f), Quaternion.identity);
                    camera.transform.LookAt(Vector3.zero);
                    ValidateGesture(camera);
                }
                ValidateAxisEndOn(camera);
                ValidatePrimaryRotationAxes(camera);
                camera.transform.SetPositionAndRotation(new Vector3(0f, 0f, -20f), Quaternion.identity);
                ValidateRigidSelectionAndCancel(camera);
                ValidateInputCaptureAndBypass();
            }
            finally {
                Object.DestroyImmediate(cameraObject);
            }
            Debug.Log("Keyboard transform validation passed: scene bindings, disabled placement rotation, perspective/orthographic grab, primary rotation axes, XYZ constraints, snapping, smoothed rigid movement/rotation, pivot restoration, cancellation, input capture, and Ctrl/Command snap bypass.");
        }

        private static void ValidateScene() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var controller = Object.FindFirstObjectByType<KeyboardTransformTool>();
            Require(controller != null, "Main scene has no keyboard transform controller.");
            var serialized = new SerializedObject(controller);
            foreach (var property in new[] { "selectionManager", "placement", "refCamera", "grabInput", "rotateInput" })
                Require(serialized.FindProperty(property).objectReferenceValue != null, "Missing controller reference: " + property);

            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var binding in new[] {
                ("Toggle Grid", "<Keyboard>/h"), ("Grab Selection", "<Keyboard>/g"),
                ("Rotate Placement", "<Keyboard>/r"), ("Toggle Holes Only", "<Keyboard>/j")
            }) {
                var input = inputs.Single(candidate => candidate.name == binding.Item1);
                Require(input.defaultAction.bindings[0].effectivePath == binding.Item2, "Wrong default binding: " + binding.Item1);
            }
            var rebinds = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var action in new[] { "Grab Selection", "Rotate Placement" }) {
                Require(rebinds.Any(ui => {
                    var input = new SerializedObject(ui).FindProperty("inputEvent").objectReferenceValue as InputEvent;
                    return input != null && input.name == action;
                }), "Missing Preferences row: " + action);
            }
        }

        private static void ValidateGesture(Camera camera) {
            var origin = new Vector3(1.13f, 2.27f, 0.33f);
            var start = (Vector2)camera.WorldToScreenPoint(origin);
            var gesture = new TransformGesture();
            gesture.Begin(camera, origin, start);

            foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward }) {
                var mouse = (Vector2)camera.WorldToScreenPoint(origin + axis * 0.83f);
                Near(gesture.Position(mouse, axis, false), origin + axis * 0.83f, "Unsnapped axis movement");
                Near(gesture.Position(mouse, axis, true), origin + axis, "Axis snap relative to initial position");
            }

            var freeMouse = start + new Vector2(43f, -19f);
            var free = gesture.Position(freeMouse, Vector3.zero, false);
            Require(Vector3.Distance(free, origin) > 0.01f, "Free grab did not move.");
            Near(gesture.Position(freeMouse, Vector3.zero, true), TransformSnapping.SnapPosition(free, origin), "Free movement snapping");
            Near(gesture.Position(start, Vector3.up, true), origin, "Axis switch must retain original anchor");

            var initialRotation = Quaternion.Euler(12f, 27f, 31f);
            gesture.Begin(camera, origin, start + Vector2.right * 100f);
            var rotatedMouse = start + new Vector2(Mathf.Cos(23f * Mathf.Deg2Rad), Mathf.Sin(23f * Mathf.Deg2Rad)) * 100f;
            foreach (var axis in new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward }) {
                var effectiveAxis = axis == Vector3.zero ? gesture.DefaultRotationAxis : axis;
                float direction = Vector3.Dot(camera.transform.forward, effectiveAxis) < 0f ? -1f : 1f;
                Near(gesture.Rotation(rotatedMouse, axis, initialRotation, true),
                    Quaternion.AngleAxis(30f * direction, effectiveAxis) * initialRotation, "Snapped rotation and axis switching");
                Near(gesture.Rotation(rotatedMouse, axis, initialRotation, false),
                    Quaternion.AngleAxis(23f * direction, effectiveAxis) * initialRotation, "Unsnapped rotation");
            }
            gesture.Begin(camera, origin, start);
            Require(Quaternion.Angle(gesture.Rotation(start + Vector2.right * 60f, Vector3.up, Quaternion.identity, true), Quaternion.identity) > 29f,
                "Rotation started at the pivot must remain usable.");
        }

        private static void ValidatePrimaryRotationAxes(Camera camera) {
            foreach (var example in new[] {
                (new Vector3(0.9f, 0.3f, 0.2f), Vector3.right),
                (new Vector3(-0.9f, 0.3f, 0.2f), Vector3.right),
                (new Vector3(0.2f, 0.9f, 0.3f), Vector3.up),
                (new Vector3(0.2f, -0.9f, 0.3f), Vector3.up),
                (new Vector3(0.2f, 0.3f, 0.9f), Vector3.forward),
                (new Vector3(0.2f, 0.3f, -0.9f), Vector3.forward)
            }) {
                camera.transform.forward = example.Item1.normalized;
                var origin = camera.transform.position + camera.transform.forward * 20f;
                var center = (Vector2)camera.WorldToScreenPoint(origin);
                var gesture = new TransformGesture();
                gesture.Begin(camera, origin, center + Vector2.right * 100f);
                Near(gesture.DefaultRotationAxis, example.Item2, "Camera view selects closest primary axis");
                float direction = Vector3.Dot(camera.transform.forward, example.Item2) < 0f ? -1f : 1f;
                Near(gesture.Rotation(center + Vector2.up * 100f, Vector3.zero, Quaternion.identity, true),
                    Quaternion.AngleAxis(90f * direction, example.Item2), "Default rotation uses a primary axis");
            }
        }

        private static void ValidatePlacementRotationDisabled() {
            var placement = Object.FindFirstObjectByType<Placement>();
            var controller = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var place = Get<InputEvent>(placement, "placeInput");
            var stop = Get<InputEvent>(placement, "stopPlacingInput");
            var rotate = Get<InputEvent>(placement, "toggleRotateInput");
            var placeHandler = place.performed;
            var stopHandler = stop.performed;
            var rotateHandler = rotate.performed;
            var displacement = Get<Protobot.Transformations.Displacement>(placement, "currentDisplacement");
            bool wasPlacing = placement.placing;
            bool wasRotating = placement.rotating;
            try {
                Invoke(placement, "Awake");
                placement.placing = true;
                var position = placement.transform.position;
                var rotation = placement.transform.rotation;
                rotate.performed?.Invoke();
                Invoke(controller, "Rotate");
                Require(!placement.rotating && !Get<bool>(controller, "active"), "R activated rotation during placement.");
                // Reflection also covers old serialized UnityEvents: deprecated
                // entry points remain present but cannot change the preview.
                placement.GetType().GetMethod("InitRotation").Invoke(placement, null);
                placement.GetType().GetMethod("Rotate").Invoke(placement, null);
                Near(placement.transform.position, position, "Legacy placement rotation changed position");
                Near(placement.transform.rotation, rotation, "Legacy placement rotation remains disabled");
            }
            finally {
                place.performed = placeHandler;
                stop.performed = stopHandler;
                rotate.performed = rotateHandler;
                placement.placing = wasPlacing;
                placement.rotating = wasRotating;
                Set(placement, "currentDisplacement", displacement);
            }
        }

        private static void ValidateAxisEndOn(Camera camera) {
            camera.orthographic = true;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 0f, -20f), Quaternion.identity);
            var start = (Vector2)camera.WorldToScreenPoint(Vector3.zero);
            var gesture = new TransformGesture();
            gesture.Begin(camera, Vector3.zero, start);
            var end = gesture.Position(start + Vector2.up * 30f, Vector3.forward, true);
            Require(end.z > 0f && end.x == 0f && end.y == 0f, "End-on axis movement is degenerate.");
        }

        private static void ValidateRigidSelectionAndCancel(Camera camera) {
            var controller = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var pivot = new GameObject("Validation MultiPivot");
            var first = new GameObject("First validation part");
            var second = new GameObject("Second validation part");
            first.transform.SetParent(pivot.transform);
            second.transform.SetParent(pivot.transform);
            first.transform.position = Vector3.left;
            second.transform.position = Vector3.right;
            try {
                var start = (Vector2)camera.WorldToScreenPoint(Vector3.zero);
                Set(controller, "origin", Vector3.zero);
                Set(controller, "active", true);
                Set(controller, "rotating", true);
                Set(controller, "axis", Vector3.forward);
                Get<System.Collections.Generic.List<Transform>>(controller, "roots").Add(pivot.transform);
                Get<System.Collections.Generic.List<Vector3>>(controller, "positions").Add(Vector3.zero);
                Get<System.Collections.Generic.List<Quaternion>>(controller, "rotations").Add(Quaternion.identity);
                var gesture = Get<TransformGesture>(controller, "gesture");
                gesture.Begin(camera, Vector3.zero, start + Vector2.right * 100f);
                typeof(KeyboardTransformTool).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(controller, new object[] { start + Vector2.up * 100f });
                Near(pivot.transform.rotation, Quaternion.identity, "Rotation should ease rather than jump");
                var tween = Get<Tween>(controller, "smoothingTween");
                tween.SetUpdate(UpdateType.Manual);
                tween.Goto(0.125f);
                float halfwayAngle = Quaternion.Angle(pivot.transform.rotation, Quaternion.identity);
                Require(halfwayAngle > 0f && halfwayAngle < 90f, "Rotation did not pass through an intermediate pose.");
                Require(Mathf.Abs(Vector3.Distance(first.transform.position, second.transform.position) - 2f) < 0.002f,
                    "Smoothing changed the rigid selection's spacing.");
                tween.Complete();
                var rotation = Quaternion.AngleAxis(90f, Vector3.forward);
                Near(second.transform.position - first.transform.position, rotation * Vector3.right * 2f, "Rigid relative offsets");
                Require(first.transform.parent == pivot.transform && second.transform.parent == pivot.transform, "MultiPivot parenting changed.");
                var rootElementType = typeof(KeyboardTransformTool).GetNestedType("RootElement", BindingFlags.NonPublic);
                var rootState = (Protobot.StateSystems.IElement)Activator.CreateInstance(rootElementType, new object[] { pivot.transform });
                pivot.transform.SetPositionAndRotation(new Vector3(5f, 2f, 1f), Quaternion.Euler(20f, 30f, 40f));
                var firstBeforeRestore = first.transform.position;
                var secondBeforeRestore = second.transform.position;
                rootState.Load();
                Near(pivot.transform.position, Vector3.zero, "Undo restores pivot position");
                Near(pivot.transform.rotation, rotation, "Undo restores pivot rotation");
                Near(first.transform.position, firstBeforeRestore, "Restoring pivot must preserve first body's world pose");
                Near(second.transform.position, secondBeforeRestore, "Restoring pivot must preserve second body's world pose");
                // Return the bodies to the rotated pose before testing modal cancellation.
                first.transform.position = rotation * Vector3.left;
                second.transform.position = rotation * Vector3.right;
                first.transform.rotation = rotation;
                second.transform.rotation = rotation;
                typeof(KeyboardTransformTool).GetMethod("Finish", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
                Near(first.transform.position, Vector3.left, "Cancel first part");
                Near(second.transform.position, Vector3.right, "Cancel second part");
                Near(first.transform.rotation, Quaternion.identity, "Cancel first rotation");
                Near(second.transform.rotation, Quaternion.identity, "Cancel second rotation");

                Set(controller, "active", true);
                Set(controller, "rotating", false);
                Set(controller, "axis", Vector3.right);
                Set(controller, "displayedOffset", Vector3.zero);
                Set(controller, "targetOffset", Vector3.zero);
                Set(controller, "displayedRotation", Quaternion.identity);
                Set(controller, "targetRotation", Quaternion.identity);
                gesture.Begin(camera, Vector3.zero, start);
                Invoke(controller, "Apply", (Vector2)camera.WorldToScreenPoint(Vector3.right * 2.3f));
                Near(pivot.transform.position, Vector3.zero, "Movement should ease rather than jump");
                tween = Get<Tween>(controller, "smoothingTween");
                tween.SetUpdate(UpdateType.Manual);
                tween.Goto(0.125f);
                Require(pivot.transform.position.x > 0f && pivot.transform.position.x < 2.5f, "Movement did not pass through an intermediate pose.");
                Near(second.transform.position - first.transform.position, Vector3.right * 2f, "Smoothed movement preserves spacing");
                tween.Complete();
                Near(pivot.transform.position, Vector3.right * 2.5f, "Smoothing must land on the snapped target");

                Invoke(controller, "Apply", (Vector2)camera.WorldToScreenPoint(Vector3.right * 4.3f));
                tween = Get<Tween>(controller, "smoothingTween");
                tween.SetUpdate(UpdateType.Manual);
                tween.Goto(0.125f);
                Invoke(controller, "Finish", true);
                Require(Get<bool>(controller, "settling") && Get<bool>(controller, "active"),
                    "Confirmation must let smoothing finish before committing.");
                Invoke(controller, "Finish", false);
                Require(Get<Tween>(controller, "smoothingTween") == null, "Cancellation retained a smoothing tween.");
                DOTween.ManualUpdate(0.5f, 0.5f);
                Near(first.transform.position, Vector3.left, "Cancel during easing restores first part");
                Near(second.transform.position, Vector3.right, "Cancel during easing restores second part");
            }
            finally {
                Invoke(controller, "Finish", false);
                Object.DestroyImmediate(pivot);
            }
        }

        private static void ValidateInputCaptureAndBypass() {
            var controller = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var instanceField = typeof(KeyboardTransformTool).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static);
            var frameField = typeof(KeyboardTransformTool).GetField("finishedFrame", BindingFlags.NonPublic | BindingFlags.Static);
            var bypassField = typeof(SnapBypassInput).GetField("input", BindingFlags.NonPublic | BindingFlags.Static);
            var previousInstance = instanceField.GetValue(null);
            var previousFrame = frameField.GetValue(null);
            var previousBypass = bypassField.GetValue(null);
            bool previousSnapping = TransformSnapping.Enabled;
            var inputManager = typeof(InputSystem).GetField("s_Manager", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var runInEditMode = inputManager.GetType().GetProperty("runPlayerUpdatesInEditMode");
            var previousRunInEditMode = runInEditMode.GetValue(inputManager);
            runInEditMode.SetValue(inputManager, true);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var inputObject = new GameObject("Validation blocked input");
            var input = inputObject.AddComponent<InputEvent>();
            input.defaultAction = new InputAction("Validation", binding: keyboard.zKey.path);
            input.defaultAction.Enable();
            input.rebindAction = new RebindAction("Transform validation " + Guid.NewGuid());
            try {
                instanceField.SetValue(null, controller);
                frameField.SetValue(null, -1);
                Set(controller, "active", true);
                int presses = 0;
                input.performed += () => presses++;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Z));
                UpdateInput();
                Require(input.IsCurrentlyPressed, $"Simulated key did not reach action: key={keyboard.zKey.isPressed}, controls={input.defaultAction.controls.Count}, action={input.defaultAction.IsPressed()}");
                input.Update();
                Require(presses == 0 && !input.IsPressed, "Modal axis input leaked to unrelated action.");
                Set(controller, "active", false);
                input.Update();
                Require(presses == 0, "Held input fired after modal confirmation.");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                UpdateInput();
                input.Update();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Z));
                UpdateInput();
                input.Update();
                Require(presses == 1, "Captured input did not resume after release.");

                input.defaultAction.Disable();
                input.defaultAction.Dispose();
                input.defaultAction = new InputAction("Validation bypass", binding: keyboard.ctrlKey.path);
                input.defaultAction.AddBinding(keyboard.leftMetaKey.path);
                input.defaultAction.AddBinding(keyboard.rightMetaKey.path);
                input.defaultAction.Enable();
                bypassField.SetValue(null, input);
                TransformSnapping.Enabled = true;
                foreach (var key in new[] { Key.LeftCtrl, Key.LeftMeta, Key.RightMeta }) {
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
                    UpdateInput();
                    Require(!TransformSnapping.Active, "Snap bypass did not work for " + key);
                }
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                UpdateInput();
                Require(TransformSnapping.Active, "Snapping did not resume after bypass release.");
            }
            finally {
                Set(controller, "active", false);
                instanceField.SetValue(null, previousInstance);
                frameField.SetValue(null, previousFrame);
                bypassField.SetValue(null, previousBypass);
                TransformSnapping.Enabled = previousSnapping;
                input.rebindAction.action.Dispose();
                Object.DestroyImmediate(inputObject);
                input.defaultAction.Dispose();
                InputSystem.RemoveDevice(keyboard);
                runInEditMode.SetValue(inputManager, previousRunInEditMode);
            }
        }

        // Edit-mode updates do not evaluate InputActions. Use the runtime phase
        // explicitly when simulating a player frame in this batch validator.
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update",
            BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(UnityEngine.InputSystem.LowLevel.InputUpdateType) }, null)
            .Invoke(null, new object[] { UnityEngine.InputSystem.LowLevel.InputUpdateType.Dynamic });

        private static void Set(object obj, string field, object value) =>
            obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static void Invoke(object obj, string method, params object[] args) =>
            obj.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args);
        private static T Get<T>(object obj, string field) =>
            (T)obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Near(Vector3 actual, Vector3 expected, string message) =>
            Require(Vector3.Distance(actual, expected) < 0.002f, $"{message}: expected {expected}, got {actual}");
        private static void Near(Quaternion actual, Quaternion expected, string message) =>
            Require(Quaternion.Angle(actual, expected) < 0.1f, $"{message}: expected {expected.eulerAngles}, got {actual.eulerAngles}");
    }
}
