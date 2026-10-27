using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using Protobot;
using Protobot.InputEvents;
using Protobot.StateSystems;
using Protobot.Tools;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using InputUpdateType = UnityEngine.InputSystem.LowLevel.InputUpdateType;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class TransformEntryValidation {
        public static void ConfigureAndValidate() {
            InteractionUpdatesSetup.RepairShortcutLayout();
            Validate();
            InteractionUpdatesValidation.Validate();
            KeyboardTransformValidation.Validate();
        }

        [MenuItem("Tools/Protobot/Validate Numeric Transforms")]
        public static void Validate() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            ValidateLayout();
            ValidateValues();
            ValidateInputAndTransforms();
            Debug.Log("Numeric transform validation passed: relative/global movement and Euler rotation, XYZ coordinates, exact values, invalid-input recovery, slash cycling, input capture, resume without jumps, rigid spacing, undo/redo, scaled UI, and visible property shortcuts.");
        }

        private static void ValidateLayout() {
            var menu = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Single(rect => rect.name == "Selection Prefs Menu");
            var rows = menu.GetComponentsInChildren<RebindUI>(true);
            Require(rows.Length >= 13, "Selection shortcuts are missing.");
            var rects = rows.Select(row => (RectTransform)row.transform).ToArray();
            for (int i = 0; i < rects.Length; i++) {
                var rect = rects[i];
                Require(-rect.anchoredPosition.y + rect.sizeDelta.y <= menu.rect.height, "Shortcut extends below the panel: " + rect.name);
                for (int j = i + 1; j < rects.Length; j++)
                    Require(Mathf.Abs(rect.anchoredPosition.y - rects[j].anchoredPosition.y) >= 30f, "Shortcut rows overlap.");
            }
            foreach (var name in new[] { "Increase First Property", "Decrease First Property", "Increase Second Property", "Decrease Second Property" })
                Require(rows.Any(row => Get<InputEvent>(row, "inputEvent").name == name), "Missing property shortcut: " + name);
            Require(TransformFeedback.ScaleForScreen(3200f, 1800f) == 2f, "Transform UI must scale at higher resolution.");
            Require(TransformFeedback.ScaleForScreen(1280f, 720f) >= 1f, "Transform UI must remain readable in smaller windows.");
            Require(TransformFeedback.FeedbackFont != null && TransformFeedback.FeedbackFont.dynamic,
                "Feedback must include a portable dynamic font in player Resources.");
            Require(TransformFeedback.FeedbackFont.HasCharacter('°') && TransformFeedback.FeedbackFont.HasCharacter('·'),
                "Feedback font must include degree and separator glyphs.");
            foreach (float scale in new[] { 1f, 2f }) {
                var angle = TransformFeedback.MeasurementSize("15°", scale);
                var distance = TransformFeedback.MeasurementSize("1.5 in", scale);
                var large = TransformFeedback.MeasurementSize("-12345.678 in", scale);
                Require(angle.x < 100 * scale && distance.x < 120 * scale && large.x > distance.x,
                    "Measurements must fit their text instead of using a fixed-width box.");
            }
            for (int combination = 0; combination < 8; combination++) {
                var messages = new List<string>();
                InteractionFeedback.AddModifierMessages(messages, (combination & 1) != 0,
                    (combination & 2) != 0, (combination & 4) != 0);
                Require(messages.Count == ((combination & 1) != 0 ? 1 : 0) +
                    ((combination & 2) != 0 ? 1 : 0) + ((combination & 4) != 0 ? 1 : 0), "Modifier feedback lost a held action.");
                messages.Add("Grab controls");
                foreach (var screen in new[] { new Vector2(320, 180), new Vector2(1280, 720), new Vector2(1920, 1080), new Vector2(3840, 2160) }) {
                    float scale = TransformFeedback.ScaleForScreen(screen.x, screen.y);
                    var footer = new Rect(0, screen.y - 60 * scale, 240 * scale, 60 * scale);
                    var panels = TransformFeedback.LayoutControls(messages.Select((_, i) => (i == messages.Count - 1 ? 160 : 48) * scale).ToArray(),
                        770 * scale, screen, scale, new[] { footer });
                    for (int i = 0; i < panels.Length; i++) {
                        Require(!panels[i].Overlaps(footer) && panels[i].yMin >= 0 && panels[i].yMax <= screen.y + .001f &&
                            panels[i].xMin >= 0 && panels[i].xMax <= screen.x,
                            "Feedback overlapped a footer or left the screen.");
                        for (int j = i + 1; j < panels.Length; j++)
                            Require(!panels[i].Overlaps(panels[j]), "Modifier/help messages overlap.");
                    }
                }
            }
        }

        private static void ValidateValues() {
            var origin = new Vector3(2f, 3f, 4f);
            var initial = Quaternion.Euler(10f, 20f, 30f);
            foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward }) {
                Require(TransformNumericInput.TryEvaluate("-0.23", false, false, axis, origin, initial, out var offset, out _, out _), "Relative movement rejected.");
                Near(offset, axis * -.23f, "Exact relative movement");
                Require(TransformNumericInput.TryEvaluate("7.2", true, false, axis, origin, initial, out offset, out _, out _), "Global movement rejected.");
                Near(offset, axis * (7.2f - Vector3.Dot(origin, axis)), "Global component movement");
                Require(TransformNumericInput.TryEvaluate("-37.5", false, true, axis, origin, initial, out _, out var rotation, out _), "Relative rotation rejected.");
                Near(rotation * initial, Quaternion.AngleAxis(-37.5f, axis) * initial, "World-axis relative rotation");
                Require(TransformNumericInput.TryEvaluate("72", true, true, axis, origin, initial, out _, out rotation, out _), "Global rotation rejected.");
                var actual = (rotation * initial).eulerAngles;
                var expected = initial.eulerAngles;
                expected[axis == Vector3.right ? 0 : axis == Vector3.up ? 1 : 2] = 72f;
                Near(actual, expected, "Global rotation must preserve the other Euler components");
                foreach (var sample in new[] { ("375", 15f), ("-375", -15f), ("720", 0f), ("-720", 0f), ("-735.25", -15.25f) }) {
                    Require(TransformNumericInput.TryEvaluate(sample.Item1, false, true, axis, origin, initial, out _, out rotation, out float degrees), "Multi-turn relative rotation rejected.");
                    Require(Mathf.Abs(degrees - sample.Item2) < .001f, "Relative numeric rotation did not preserve signed wrapping.");
                    Near(rotation, Quaternion.AngleAxis(sample.Item2, axis), "Wrapped relative numeric orientation");
                    Require(TransformNumericInput.TryEvaluate(sample.Item1, true, true, axis, origin, initial, out _, out rotation, out _), "Multi-turn global rotation rejected.");
                    expected = initial.eulerAngles;
                    expected[axis == Vector3.right ? 0 : axis == Vector3.up ? 1 : 2] = sample.Item2;
                    Near(rotation * initial, Quaternion.Euler(expected), "Wrapped global numeric orientation");
                }
            }
            Require(TransformNumericInput.TryEvaluate(" 5, -2.25, 1e1 ", true, false, Vector3.zero, origin, initial, out var xyz, out _, out _), "Global XYZ rejected.");
            Near(origin + xyz, new Vector3(5f, -2.25f, 10f), "Global XYZ position");
            foreach (string invalid in new[] { "", "NaN", "Infinity", "1e9999", "5 in", "1 2", "1,2", "1,2,3,4", "1,NaN,2" })
                Require(!TransformNumericInput.TryEvaluate(invalid, true, false, Vector3.zero, origin, initial, out _, out _, out _), "Invalid coordinates accepted: " + invalid);
            foreach (string invalid in new[] { "", "NaN", "Infinity", "1e9999", "90deg", "1,2,3" })
                Require(!TransformNumericInput.TryEvaluate(invalid, false, true, Vector3.up, origin, initial, out _, out _, out _), "Invalid angle accepted: " + invalid);
        }

        private static void ValidateInputAndTransforms() {
            var controller = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var numeric = Get<TransformNumericInput>(controller, "numericInput");
            var inputManager = typeof(InputSystem).GetField("s_Manager", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var runInEdit = inputManager.GetType().GetProperty("runPlayerUpdatesInEditMode");
            var oldRunInEdit = runInEdit.GetValue(inputManager);
            runInEdit.SetValue(inputManager, true);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var mouse = InputSystem.AddDevice<Mouse>();
            var first = new GameObject("Numeric transform first part", typeof(SavedObject));
            var second = new GameObject("Numeric transform second part", typeof(SavedObject));
            var oldStates = StateSystem.states;
            int oldIndex = StateSystem.curIndex;
            var oldStateInstance = StateSystem.instance;
            var instanceField = typeof(KeyboardTransformTool).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
            var frameField = typeof(KeyboardTransformTool).GetField("finishedFrame", BindingFlags.Static | BindingFlags.NonPublic);
            var oldInstance = instanceField.GetValue(null);
            var oldFrame = frameField.GetValue(null);
            var camera = Get<Camera>(controller, "refCamera");
            var origin = new Vector3(2f, 3f, 4f);
            var initial = Quaternion.Euler(10f, 20f, 30f);
            try {
                instanceField.SetValue(null, controller);
                frameField.SetValue(null, -1);
                StateSystem.instance = Object.FindFirstObjectByType<StateSystem>();
                Reset(false, Vector3.right);
                numeric.Cycle(false);
                Require(numeric.Mode == TransformNumericInput.InputMode.Relative, "First slash must open relative entry.");
                Require(!KeyboardTransformTool.AllowsInput(Get<InputEvent>(controller, "rotateInput")), "Typing R must not start another transform.");
                numeric.Cycle(false);
                Require(numeric.Mode == TransformNumericInput.InputMode.Global && numeric.Text == "", "Second slash must open global entry.");
                numeric.Cycle(false);
                Require(!numeric.IsOpen, "Third slash must return to mouse.");
                numeric.Cycle(true);
                Require(numeric.Mode == TransformNumericInput.InputMode.Global, "Unconstrained grab must open XYZ entry directly.");
                numeric.Cycle(true);
                Require(!numeric.IsOpen, "Slash must close XYZ entry.");

                Invoke(controller, "SetTarget", Vector3.right * .5f, Quaternion.identity);
                Get<Tween>(controller, "smoothingTween").Complete();
                numeric.Cycle(false);
                Type("invalid");
                Require(!(bool)Invoke(controller, "SubmitNumeric"), "Invalid entry must be rejected.");
                Require(Get<bool>(controller, "active") && !Get<bool>(controller, "settling"), "Invalid entry must return to manual movement.");
                Near(first.transform.position, origin + Vector3.right * .5f, "Invalid entry moved the part");
                Invoke(controller, "Apply", (Vector2)MouseInput.Position);
                Near(Get<Vector3>(controller, "targetOffset"), Vector3.right * .5f, "Resuming at the same pointer position jumped");

                Reset(false, Vector3.right);
                var wrappedGesture = Get<TransformGesture>(controller, "gesture");
                var edge = (Vector2)MouseInput.Position + Vector2.right * 300;
                var wrapped = edge - Vector2.right * 600;
                wrappedGesture.RebasePointer(edge, wrapped);
                UnityEngine.InputSystem.LowLevel.InputState.Change(mouse.position, wrapped, InputUpdateType.Dynamic);
                Invoke(controller, "Apply", wrapped);
                var wrappedOffset = Get<Vector3>(controller, "targetOffset");
                Get<Tween>(controller, "smoothingTween")?.Complete();
                numeric.Cycle(false); Type("invalid");
                Require(!(bool)Invoke(controller, "SubmitNumeric"), "Invalid entry after wrapping was accepted.");
                Invoke(controller, "Apply", wrapped);
                Near(Get<Vector3>(controller, "targetOffset"), wrappedOffset, "Numeric-to-mouse after wrapping jumped");
                numeric.Cycle(false); Type(".23"); SubmitAndComplete();
                Near(first.transform.position, origin + Vector3.right * .23f, "Wrapped move changed the original relative numeric anchor");

                Reset(false, Vector3.right);
                numeric.Cycle(false); Type("0.23"); SubmitAndComplete();
                Near(first.transform.position, origin + Vector3.right * .23f, "Typed values must bypass quantization");
                StateSystem.states[0].Load(); DOTween.CompleteAll();
                Near(first.transform.position, origin, "Numeric move undo");
                StateSystem.states[1].Load(); DOTween.CompleteAll();
                Near(first.transform.position, origin + Vector3.right * .23f, "Numeric move redo");

                Reset(false, Vector3.up);
                numeric.Cycle(false); numeric.Cycle(false); Type("17.1"); SubmitAndComplete();
                Near(first.transform.position, new Vector3(origin.x, 17.1f, origin.z), "Absolute axis position");

                Reset(false, Vector3.zero);
                numeric.Cycle(true); Type("5, -2, 1"); SubmitAndComplete();
                Near(first.transform.position, new Vector3(5f, -2f, 1f), "Absolute XYZ position");
                Near(second.transform.position - first.transform.position, new Vector3(2f, 1f, 0f), "Numeric move changed rigid spacing");

                Reset(true, Vector3.forward);
                numeric.Cycle(false); Type("-45"); SubmitAndComplete();
                Near(first.transform.rotation, Quaternion.AngleAxis(-45f, Vector3.forward) * initial, "Relative numeric rotation");
                Near(second.transform.position - first.transform.position, Quaternion.AngleAxis(-45f, Vector3.forward) * new Vector3(2f, 1f, 0f), "Numeric rotation changed rigid spacing");

                Reset(true, Vector3.forward);
                numeric.Cycle(false); numeric.Cycle(false); Type("90"); SubmitAndComplete();
                Near(first.transform.rotation, Quaternion.Euler(10f, 20f, 90f), "Absolute world Z rotation");

                var gesture = new TransformGesture();
                var center = (Vector2)camera.WorldToScreenPoint(origin);
                gesture.Begin(camera, origin, center + Vector2.right * 100f);
                gesture.Resume(center + Vector2.up * 100f, Vector3.zero, 37.5f, Vector3.forward);
                Near(gesture.Rotation(center + Vector2.up * 100f, Vector3.forward, Quaternion.identity, false),
                    Quaternion.AngleAxis(37.5f, Vector3.forward), "Resuming rotation jumped");
            }
            finally {
                Invoke(controller, "Finish", false);
                numeric.Close();
                DOTween.KillAll();
                Object.DestroyImmediate(first); Object.DestroyImmediate(second);
                StateSystem.states = oldStates; StateSystem.curIndex = oldIndex; StateSystem.instance = oldStateInstance;
                instanceField.SetValue(null, oldInstance); frameField.SetValue(null, oldFrame);
                InputSystem.RemoveDevice(keyboard); InputSystem.RemoveDevice(mouse);
                runInEdit.SetValue(inputManager, oldRunInEdit);
            }

            void Type(string text) {
                foreach (char character in text) InputSystem.QueueTextEvent(keyboard, character);
                typeof(InputSystem).GetMethod("Update", BindingFlags.Static | BindingFlags.NonPublic, null,
                    new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
                Require(numeric.Text == text, "Text input did not reach numeric entry.");
            }
            void SubmitAndComplete() {
                Require((bool)Invoke(controller, "SubmitNumeric"), "Valid input rejected.");
                Get<Tween>(controller, "smoothingTween")?.Complete();
                Require(!Get<bool>(controller, "active") && !numeric.IsOpen, "Valid numeric confirmation did not finish.");
            }
            void Reset(bool rotating, Vector3 axis) {
                numeric.Close();
                Invoke(controller, "Finish", false);
                first.transform.SetPositionAndRotation(origin, initial);
                second.transform.SetPositionAndRotation(origin + new Vector3(2f, 1f, 0f), initial);
                var roots = Get<List<Transform>>(controller, "roots"); roots.Clear(); roots.Add(first.transform); roots.Add(second.transform);
                var positions = Get<List<Vector3>>(controller, "positions"); positions.Clear(); positions.Add(first.transform.position); positions.Add(second.transform.position);
                var rotations = Get<List<Quaternion>>(controller, "rotations"); rotations.Clear(); rotations.Add(initial); rotations.Add(initial);
                Set(controller, "bodies", new List<GameObject> { first, second });
                Set(controller, "before", new List<IElement> { new ObjectElement(first), new ObjectElement(second) });
                Set(controller, "origin", origin); Set(controller, "initialOrientation", initial); Set(controller, "axis", axis);
                Set(controller, "targetOffset", Vector3.zero); Set(controller, "displayedOffset", Vector3.zero);
                Set(controller, "targetRotation", Quaternion.identity); Set(controller, "displayedRotation", Quaternion.identity);
                Set(controller, "rotationDegrees", 0f); Set(controller, "active", true); Set(controller, "rotating", rotating); Set(controller, "settling", false);
                Get<TransformGesture>(controller, "gesture").Begin(camera, origin, MouseInput.Position);
                StateSystem.states = new List<State> { new State(new List<IElement>()) }; StateSystem.curIndex = 0;
            }
        }

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Near(Vector3 actual, Vector3 expected, string message) => Require(Vector3.Distance(actual, expected) < .001f, $"{message}: {actual} != {expected}");
        private static void Near(Quaternion actual, Quaternion expected, string message) => Require(Quaternion.Angle(actual, expected) < .1f, message);
        private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static object Invoke(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
    }
}
