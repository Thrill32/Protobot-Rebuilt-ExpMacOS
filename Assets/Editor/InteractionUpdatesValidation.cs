using System;
using System.Linq;
using System.Reflection;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.Tools;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class InteractionUpdatesValidation {
        private const string SmokeKey = "Protobot.InteractionSmoke";
        private static int smokeStart;

        [InitializeOnLoadMethod]
        private static void ResumeSmoke() {
            if (SessionState.GetBool(SmokeKey, false)) {
                EnsureSmokeDevices();
                smokeStart = 0;
                EditorApplication.update += SmokeTick;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnsureSmokeDevices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (UnityEngine.InputSystem.Mouse.current == null)
                UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Mouse>();
            if (UnityEngine.InputSystem.Keyboard.current == null)
                UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Keyboard>();
        }

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }

        private static void SmokeTick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            if (++smokeStart < 15) return;
            EditorApplication.update -= SmokeTick;
            SessionState.SetBool(SmokeKey, false);
            try {
                var ui = Object.FindFirstObjectByType<AddPropertiesUI>();
                var manager = Get<SelectionManager>(ui, "selectionManager");
                var placement = Object.FindFirstObjectByType<Placement>();
                var generator = PartsManager.GetPartType("SHFT").GetComponent<PartGenerator>();
                PartParameterEditing.Load(generator, "SHFT-Normal-6");
                var shaft = generator.Generate(new Vector3(4, 3, 2), Quaternion.Euler(10, 20, 30));
                manager.SetCurrent(new ObjectSelection { gameObject = shaft }, true);
                Invoke(ui, "ObserveSelection");
                CheckArrowFocus(ui);
                ui.UpdateParam2("7.5");
                var resized = manager.current.gameObject;
                Require(shaft.IsDeleted() && Mathf.Abs(resized.GetComponent<Shaft>().ShaftLength - 7.5f) < .001f, "Runtime shaft resize failed.");
                Near(resized.transform.position, shaft.transform.position, "Runtime shaft base moved.");
                var detector = resized.GetComponent<Shaft>().holeDetector;
                var capsule = detector != null ? detector.GetComponent<CapsuleCollider>() : null;
                Require(capsule != null && Mathf.Abs(capsule.transform.TransformVector(Vector3.up * capsule.height).magnitude - 7.5f) < .001f,
                    "Runtime shaft detector does not match the new length.");
                StateSystem.instance.Undo();
                Require(manager.current.gameObject == shaft && !shaft.IsDeleted(), "Runtime resize undo failed.");
                StateSystem.instance.Redo();
                Require(manager.current.gameObject == resized && !resized.IsDeleted(), "Runtime resize redo failed.");
                Invoke(ui, "ObserveSelection");
                Get<CanvasGroup>(ui, "upperAddMenu").gameObject.SetActive(false);
                Require(ui.isActiveAndEnabled, "Hiding the panel disabled shortcuts.");
                Invoke(ui, "ObserveSelection");
                Require(Get<CanvasGroup>(ui, "upperAddMenu").gameObject.activeInHierarchy, "Selected properties did not reopen the panel.");
                // Batch mode has no focused application window; exercise the
                // command after its UI/focus guards. Binding wiring is checked above.
                Invoke(ui, "PlacePickedPart");
                Require(placement.CurrentPartPlacement != null && placement.CurrentPartPlacement.partGenerator.GetId() == "SHFT-Normal-7.5", "Pick Part did not start ordinary placement with selected properties.");
                CheckArrowFocus(ui);
                var position = placement.transform.position;
                ui.UpdateParam2("8");
                Near(placement.transform.position, position, "Changing preview properties reset placement position.");
                Require(placement.CurrentPartPlacement.partGenerator.GetId() == "SHFT-Normal-8", "Preview property edit failed.");
                Debug.Log("Interaction runtime smoke passed: shaft geometry/detector resize, undo/redo, persistent property shortcuts, pick-part placement, and live preview properties.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void CheckArrowFocus(AddPropertiesUI ui) {
            var eventSystem = EventSystem.current;
            var module = eventSystem.GetComponent<InputSystemUIInputModule>();
            Require(module.move == null, "UI navigation must not consume attribute arrows.");
            Require(module.point != null && module.leftClick != null && module.scrollWheel != null &&
                module.submit != null && module.cancel != null, "Disabling arrow navigation removed other UI input.");
            var browser = Object.FindFirstObjectByType<AddPartsUI>();
            var rows = browser.partUIsContainer.GetComponentsInChildren<PartDisplayUI>();
            Require(rows.Length > 2, "The live part browser is empty.");
            var row = rows[rows.Length / 2].gameObject;
            var oldFocus = eventSystem.currentSelectedGameObject;
            bool searchWasOn = browser.searchToggle.isOn;
            var oldBackground = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            var generator = Get<PartGenerator>(ui, "generator");
            string originalId = generator.GetId();
            int originalState = StateSystem.curIndex;
            var inputs = new[] { "increaseFirstInput", "decreaseFirstInput", "increaseSecondInput", "decreaseSecondInput" }
                .Select(field => Get<InputEvent>(ui, field)).ToArray();
            var enabled = inputs.Select(input => (input.defaultAction.enabled, input.rebindAction.action.enabled)).ToArray();
            try {
                foreach (var input in inputs) { input.rebindAction.action.Disable(); input.defaultAction.Enable(); }
                eventSystem.SetSelectedGameObject(row);
                int repeats = Mathf.Min(rows.Length + 2, 32);
                for (int i = 0; i < repeats; i++) Press(Key.UpArrow);
                Require(generator.param1.value == "High Strength", "Up did not change the selected shaft's first attribute.");
                for (int i = 0; i < repeats; i++) Press(Key.DownArrow);
                Require(generator.GetId() == originalId, "Down did not restore the first attribute.");
                string beforeLength = generator.param2.value;
                Press(Key.RightArrow);
                Require(generator.param2.value != beforeLength, "Right did not change the second attribute.");
                Press(Key.LeftArrow);
                Require(generator.GetId() == originalId, "Left did not restore the second attribute.");
                Require(browser.searchToggle.isOn == searchWasOn, "Attribute arrows toggled part search.");

                // Text fields handle caret keys themselves, independently of
                // the UI module's spatial navigation action.
                var text = browser.searchText.GetComponentInParent<InputField>();
                Require(text != null, "Part search input is missing.");
                string previousText = text.text;
                text.text = "arrow"; text.caretPosition = 3;
                text.ProcessEvent(Event.KeyboardEvent("left"));
                Require(text.caretPosition == 2, "Disabling navigation broke text caret movement.");
                text.text = previousText;
                Debug.Log("Attribute arrow focus passed: repeated Up/Down and Left/Right change attributes without navigating into search; pointer/scroll/submit/cancel input and text caret movement remain available.");
            }
            finally {
                SetKeys();
                while (StateSystem.curIndex > originalState) StateSystem.instance.Undo();
                if (!Get<Placement>(ui, "placement").placing) Invoke(ui, "ObserveSelection");
                eventSystem.SetSelectedGameObject(oldFocus);
                InputSystem.settings.backgroundBehavior = oldBackground;
                for (int i = 0; i < inputs.Length; i++) {
                    if (!enabled[i].Item1) inputs[i].defaultAction.Disable();
                    if (enabled[i].Item2) inputs[i].rebindAction.action.Enable();
                }
            }

            void SetKeys(params Key[] keys) {
                InputSystem.QueueStateEvent(Keyboard.current, new UnityEngine.InputSystem.LowLevel.KeyboardState(keys));
                typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(UnityEngine.InputSystem.LowLevel.InputUpdateType) }, null).Invoke(null,
                    new object[] { UnityEngine.InputSystem.LowLevel.InputUpdateType.Dynamic });
                foreach (var input in inputs) input.Update();
            }
            void Press(Key key) {
                SetKeys(key);
                Require(inputs.Any(input => input.IsPressed), $"Attribute arrow was not received: {key}; " +
                    string.Join(", ", inputs.Select(input => $"{input.name}={input.defaultAction.IsPressed()}/{input.defaultAction.controls.Count}")));
                module.Process();
                Require(eventSystem.currentSelectedGameObject == row, "Arrow input moved focus away from the clicked part row.");
                // Headless Unity has no focused window; exercise the real held
                // shortcut handler after that unrelated application-focus guard.
                Invoke(ui, "UpdateHeldPropertyShortcut");
                SetKeys(); Invoke(ui, "UpdateHeldPropertyShortcut");
            }
        }

        [MenuItem("Tools/Protobot/Validate Interaction Updates")]
        public static void Validate() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            Require(Object.FindFirstObjectByType<ToolUIInputModule>() != null, "Main must use the tool-aware UI input module.");
            var ui = Object.FindFirstObjectByType<AddPropertiesUI>(FindObjectsInactive.Include);
            foreach (var field in new[] { "selectionManager", "pickPartInput", "increaseFirstInput", "decreaseFirstInput", "increaseSecondInput", "decreaseSecondInput" })
                Require(new SerializedObject(ui).FindProperty(field).objectReferenceValue != null, "Missing reference: " + field);
            var input = Get<InputEvent>(ui, "pickPartInput");
            Require(input.defaultAction.bindings[0].path == "<Keyboard>/p", "Pick Part must default to P.");
            Require(Object.FindFirstObjectByType<KeyboardTransformTool>().showConstraintAxis, "Axis guide should default on.");
            PartsManager.LoadPartTypes();
            ValidateParameters();
            ValidateHoleTracking();
            ValidateReplacement(ui);
            Debug.Log("Interaction updates validation passed: scene wiring, P binding, dependent options, numeric limits, live hole/pivot tracking, property replacement with color and undo/redo, and generator isolation.");
        }

        private static void ValidateParameters() {
            var shaft = PartsManager.partTypes.Select(t => t.GetComponent<PartGenerator>()).OfType<ShaftPartGenerator>().Single();
            PartParameterEditing.Load(shaft, "SHFT-Normal-6");
            Require(PartParameterEditing.Step(shaft, 1, 1) && shaft.param2.value == "6.5", "Shaft must increase by half an inch.");
            PartParameterEditing.Step(shaft, 0, 1);
            Require(shaft.param1.value == "High Strength", "First property must step through types.");
            shaft.param2.value = "24";
            PartParameterEditing.Step(shaft, 0, -1);
            Require(shaft.param2.value == "12", "Shaft type must clamp dependent length.");
            Require(!PartParameterEditing.Step(shaft, 1, 1), "Shaft length must respect maximum.");
            shaft.param2.value = "1";
            Require(!PartParameterEditing.Step(shaft, 1, -1), "Shaft length must respect minimum.");
            var plate = PartsManager.partTypes.Select(t => t.GetComponent<PartGenerator>()).OfType<PlatePartGenerator>().Single();
            plate.InitParamValues();
            int length = int.Parse(plate.param1.value);
            PartParameterEditing.Step(plate, 0, 1);
            Require(int.Parse(plate.param1.value) == length + 1, "Hole count must step by one.");
            foreach (var type in PartsManager.partTypes) {
                var generator = type.GetComponent<PartGenerator>();
                generator.InitParamValues();
                if (!generator.UsesParams) continue;
                // Every dependent option must remain valid after a type change.
                PartParameterEditing.Step(generator, 0, 1);
                if (generator.UsesTwoParams && !generator.param2.custom)
                    Require(generator.GetParam2Options().Contains(generator.param2.value), "Invalid dependent option: " + type.name);
            }
        }

        private static void ValidateHoleTracking() {
            var body = new GameObject("Hole tracking body");
            var source = new GameObject("Live hole");
            var marker = new GameObject("Hole marker", typeof(MeshFilter));
            source.transform.SetParent(body.transform);
            source.transform.localPosition = new Vector3(2, 1, 0);
            var data = new HoleData { source = source.transform, part = body, depth = .1f, size = Vector2.one };
            var face = marker.AddComponent<HoleFace>();
            try {
                face.Set(data, Vector3.forward);
                var selection = new HoleSelection { gameObject = marker, holeFace = face, holeData = data, faceDirection = Vector3.forward };
                selection.Select();
                body.transform.SetPositionAndRotation(new Vector3(4, 3, 2), Quaternion.Euler(20, 70, 30));
                face.Refresh();
                var normal = body.transform.forward;
                Near(face.position, source.transform.position + normal * .05f, "Marker must follow translation and rotation.");
                Near(face.direction, normal, "Marker direction must follow body rotation.");
                Near(data.position, source.transform.position, "Hole data must be live without waiting for Update.");
                selection.Deselect();
                selection.Select();
                Near(face.direction, normal, "Reselect/undo must preserve the selected local face.");
                face.Set(data, -normal);
                Near(face.position, source.transform.position - normal * .05f, "Opposite face is across the same hole.");
            }
            finally { Object.DestroyImmediate(body); Object.DestroyImmediate(marker); }
        }

        private static void ValidateReplacement(AddPropertiesUI ui) {
            var gen = PartsManager.partTypes.Select(t => t.GetComponent<PartGenerator>()).OfType<PlatePartGenerator>().Single();
            gen.InitParamValues();
            var original = gen.Generate(new Vector3(3, 4, 5), Quaternion.Euler(10, 20, 30));
            var managerObject = new GameObject("Property validation selection");
            managerObject.AddComponent<TagSelectionCondition>().targetTags = "NeverSelectByPointer";
            var manager = managerObject.AddComponent<SelectionManager>();
            Invoke(manager, "Awake");
            var oldStates = StateSystem.states;
            var oldIndex = StateSystem.curIndex;
            var oldInstance = StateSystem.instance;
            StateSystem.instance = Object.FindFirstObjectByType<StateSystem>();
            StateSystem.states = new System.Collections.Generic.List<State> { new State(new()) };
            StateSystem.curIndex = 0;
            GameObject replacement = null;
            try {
                original.GetComponent<Renderer>().sharedMaterial = new Material(original.GetComponent<Renderer>().sharedMaterial) { color = Color.magenta };
                manager.SetCurrent(new ObjectSelection { gameObject = original }, true);
                Set(ui, "selectionManager", manager);
                Set(ui, "generator", gen);
                Set(ui, "editingObject", original);
                PartParameterEditing.Step(gen, 0, 1);
                Invoke(ui, "ApplyParameters");
                replacement = manager.current.gameObject;
                Require(original != replacement && original.IsDeleted() && !replacement.IsDeleted(), "Property edit must replace geometry.");
                Require(replacement.GetComponent<SavedObject>().id == gen.GetId(), "Replacement saved ID must match parameters.");
                Near(replacement.transform.position, original.transform.position, "Replacement must preserve position.");
                Require(Quaternion.Angle(replacement.transform.rotation, original.transform.rotation) < .001f, "Replacement must preserve rotation.");
                Require(replacement.GetComponent<Renderer>().sharedMaterial.color == Color.magenta, "Replacement must preserve color.");
                Require(replacement.GetComponentsInChildren<HoleCollider>().Length > original.GetComponentsInChildren<HoleCollider>().Length, "Property edit must regenerate holes.");
                StateSystem.states[0].Load();
                Require(!original.IsDeleted() && replacement.IsDeleted() && manager.current.gameObject == original, "Undo must restore old part and selection.");
                StateSystem.states[1].Load();
                Require(original.IsDeleted() && !replacement.IsDeleted() && manager.current.gameObject == replacement, "Redo must restore edited part and selection.");
                string p1 = gen.param1.value, p2 = gen.param2.value;
                var loaded = PartsManager.GeneratePart(original.GetComponent<SavedObject>().id, Vector3.zero, Quaternion.identity);
                Object.DestroyImmediate(loaded);
                Require(gen.param1.value == p1 && gen.param2.value == p2, "Generating another saved part must not change placement properties.");
            }
            finally {
                StateSystem.states = oldStates; StateSystem.curIndex = oldIndex; StateSystem.instance = oldInstance;
                Object.DestroyImmediate(original);
                if (replacement != null) Object.DestroyImmediate(replacement);
                Object.DestroyImmediate(managerObject);
            }
        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .001f, message);
        private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
