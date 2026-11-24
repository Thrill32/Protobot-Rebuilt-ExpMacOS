using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DG.Tweening;
using Protobot;
using Protobot.InputEvents;
using Protobot.Tools;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using InputEvent = Protobot.InputEvents.InputEvent;
using static ProtobotEditorTools.InteractionOverhaulValidation;

namespace ProtobotEditorTools {
    public static class CameraNavigationValidation {
        private const string SmokeKey = "Protobot.CameraNavigationSmoke";
        private static int frames;

        [MenuItem("Tools/Protobot/Validate Camera Navigation UX")]
        public static void Validate() {
            float distance = 10f;
            float oneTick = PivotCamera.CalculateZoomDistance(distance, 120f, 1f, false);
            float twoHalves = PivotCamera.CalculateZoomDistance(
                PivotCamera.CalculateZoomDistance(distance, 60f, 1f, false), 60f, 1f, false);
            Near(oneTick, twoHalves, "Zoom depends on input frame grouping.");
            Require(oneTick < distance, "Positive wheel input must zoom in.");
            Require(PivotCamera.CalculateZoomDistance(distance, 120f, 1f, true) > distance, "Inverted zoom sign is wrong.");
            Near(PivotCamera.CalculateZoomDistance(oneTick, -120f, 1f, false), distance, "Opposite zoom input did not round-trip.");
            Require(NumericPreferenceField.TryParse("0.125", .01f, 1f, out var parsed) && Mathf.Approximately(parsed, .125f),
                "Decimal sensitivity input was rejected.");
            foreach (var invalid in new[] { "", "text", "NaN", "Infinity", "-1", "1001" })
                Require(!NumericPreferenceField.TryParse(invalid, .01f, 1000f, out _), "Invalid numeric preference was accepted: " + invalid);

            const string key = "Protobot.CameraNavigationValidation";
            bool had = LocalPreferences.HasKey(key); string previous = LocalPreferences.GetString(key);
            try {
                LocalPreferences.SetFloat(key, 0.123456789f); LocalPreferences.Save();
                Require(float.Parse(NumericPreferenceField.Format(0.123456789f), System.Globalization.CultureInfo.InvariantCulture) == LocalPreferences.GetFloat(key), "Numeric display lost float precision.");
                Require(NumericPreferenceField.Format(.1f) == "0.1", "Default sensitivity exposes binary float noise.");
                Near(LocalPreferences.GetFloat(key), 0.123456789f, "Numeric preference did not round-trip.");
            }
            finally {
                if (had) LocalPreferences.SetString(key, previous); else LocalPreferences.DeleteKey(key);
                LocalPreferences.Save();
            }
            Debug.Log("Camera navigation validation passed: deterministic signed zoom, decimal validation, finite bounds and exact LocalPreferences persistence.");
        }

        [MenuItem("Tools/Protobot/Smoke Camera Navigation UX")]
        public static void Smoke() {
            Validate();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0; EditorApplication.update += Tick;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try { await CheckRuntime(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static async Task CheckRuntime() {
            var fields = Object.FindObjectsByType<NumericPreferenceField>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Require(fields.Length == 5, "Runtime camera Preferences did not install exactly five numeric fields.");
            Require(fields.Select(field => field.PreferenceKey).Distinct().Count() == 5, "Camera fields do not use distinct preference keys.");
            foreach (var field in fields) {
                var input = field.GetComponent<InputField>();
                Require(input != null && input.navigation.mode == Navigation.Mode.Automatic, "A camera field is not keyboard navigable.");
                Require(input.text == NumericPreferenceField.Format(field.Current), "A camera field does not show its exact current value.");
                var fieldRect = (RectTransform)field.transform;
                Require(fieldRect.sizeDelta.x <= 55f && fieldRect.sizeDelta.y <= 20f,
                    "A camera numeric field is larger than the compact Preferences layout.");
                var row = (RectTransform)field.transform.parent;
                Require(row.sizeDelta.x <= 165f && row.sizeDelta.y <= 25f,
                    "A camera preference row exceeds the existing column scale.");
                var defaultButton = row.Find("Set Default Button")?.GetComponent<Button>();
                Require(defaultButton != null && defaultButton.navigation.mode == Navigation.Mode.Automatic,
                    "A camera field is missing its keyboard-focusable set-to-default button.");
                Require(((RectTransform)defaultButton.transform).sizeDelta == new Vector2(15f, 15f) && defaultButton.image?.sprite != null,
                    "A camera default button does not match the keybind reset control scale/style.");
            }
            var categories = fields[0].GetComponentInParent<PreferencesCategoryScroller>(true);
            Require(categories != null && categories.GetComponent<RectMask2D>() != null, "Preferences category scrolling/clipping was not installed.");
            await CheckPreferencesScrolling(categories, fields[0]);
            Require(fields[0].transform.parent.parent.GetComponentsInChildren<Slider>(true).Length == 0,
                "A legacy camera sensitivity slider remains in the Camera category.");

            var pivot = Object.FindFirstObjectByType<PivotCamera>();
            pivot.SetTransform(Vector3.zero, new Vector3(10f, 20f, 0f), -10f);
            var beforeOrbit = pivot.transform.rotation;
            pivot.OrbitControl(new Vector2(2f, 3f));
            Require(Quaternion.Angle(beforeOrbit, pivot.transform.rotation) > .1f && !DOTween.IsTweening(pivot.transform),
                "Direct orbit is delayed or still tweened.");
            var beforePan = pivot.transform.position;
            pivot.PanControl(new Vector2(1f, -1f));
            Require(Vector3.Distance(beforePan, pivot.transform.position) > .01f && !DOTween.IsTweening(pivot.transform),
                "Direct pan is delayed or still tweened.");
            float beforeZoom = -pivot.camera.transform.localPosition.z;
            pivot.ZoomControl(120f);
            Require(-pivot.camera.transform.localPosition.z < beforeZoom && !DOTween.IsTweening(pivot.camera.transform),
                "Direct zoom is delayed, wrongly signed or still tweened.");

            var tool = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var pan = inputs.Single(input => input.name == "Pan");
            var orbit = inputs.Single(input => input.name == "Orbit");
            var noclip = inputs.Single(input => input.name == "Noclip Camera");
            var active = typeof(KeyboardTransformTool).GetField("active", BindingFlags.NonPublic | BindingFlags.Instance);
            var settling = typeof(KeyboardTransformTool).GetField("settling", BindingFlags.NonPublic | BindingFlags.Instance);
            active.SetValue(tool, true); settling.SetValue(tool, false);
            try {
                Require(KeyboardTransformTool.AllowsCameraZoom, "Transform preview blocks non-conflicting wheel zoom.");
                Require(KeyboardTransformTool.AllowsCameraNavigation(pan), "Transform preview blocks non-conflicting pan.");
                Require(KeyboardTransformTool.AllowsNoclipCamera(noclip), "Transform preview blocks pausable free-fly navigation.");
                Require(!KeyboardTransformTool.AllowsCameraNavigation(orbit), "Right-click orbit stole transform cancellation.");
                settling.SetValue(tool, true);
                Require(!KeyboardTransformTool.AllowsCameraZoom && !KeyboardTransformTool.AllowsCameraNavigation(pan),
                    "Confirmation settling did not own camera input.");
            }
            finally { active.SetValue(tool, false); settling.SetValue(tool, false); }

            var test = fields.Single(field => field.PreferenceKey == NumericPreferenceField.Key(NumericPreferenceField.CameraValue.OrbitSensitivity));
            bool had = LocalPreferences.HasKey(test.PreferenceKey); string previous = LocalPreferences.GetString(test.PreferenceKey);
            try {
                Require(test.TryCommit("0.375") && test.GetComponent<InputField>().text == "0.375", "Typed camera value did not commit exactly.");
                Require(!test.TryCommit("NaN") && test.GetComponent<InputField>().text == "0.375", "Invalid camera value replaced the last valid value.");
                var defaultButton = test.transform.parent.Find("Set Default Button").GetComponent<Button>();
                Require(defaultButton.interactable, "A changed camera preference did not enable its default button.");
                Require(test.TryCommit("0.10000001") && !test.IsDefault && defaultButton.interactable, "A near-default exact value disabled the reset button.");
                defaultButton.onClick.Invoke();
                Require(test.IsDefault && test.GetComponent<InputField>().text == NumericPreferenceField.Format(test.DefaultValue),
                    "The camera default button did not restore and display the exact default.");
                Near(LocalPreferences.GetFloat(test.PreferenceKey), test.DefaultValue, "The restored camera default was not persisted.");
                Require(!defaultButton.interactable, "The camera default button stayed enabled at the default value.");
            }
            finally {
                if (had) LocalPreferences.SetString(test.PreferenceKey, previous); else LocalPreferences.DeleteKey(test.PreferenceKey);
                LocalPreferences.Save();
            }
            Debug.Log("Camera navigation smoke passed: immediate orbit/pan/zoom, compact numeric UI, per-field defaults, persistence, scrolling, keyboard focus and modal ownership.");
        }

        private static async Task CheckPreferencesScrolling(PreferencesCategoryScroller scroller, NumericPreferenceField field) {
            var category = field.transform.parent.parent.parent as RectTransform;
            var selected = EventSystem.current.currentSelectedGameObject;
            var ancestors = scroller.GetComponentsInParent<Transform>(true)
                .Select(item => (item.gameObject, item.gameObject.activeSelf)).ToArray();
            var categoryStates = scroller.transform.Cast<Transform>()
                .Select(item => (item.gameObject, item.gameObject.activeSelf)).ToArray();
            GameObject.Find("Launch Canvas")?.SetActive(false);
            foreach (var state in ancestors) state.gameObject.SetActive(true);
            foreach (var state in categoryStates) state.gameObject.SetActive(state.gameObject == category.gameObject);
            try {
                await InteractionOverhaulValidation.CheckSettingsScroll(scroller, category, field);
            }
            finally {
                EventSystem.current.SetSelectedGameObject(selected);
                foreach (var state in categoryStates) state.gameObject.SetActive(state.activeSelf);
                foreach (var state in ancestors) state.gameObject.SetActive(state.activeSelf);
            }
        }

        internal static EditorWindow FocusRouteGameView() {
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            view.Show();
            view.Focus();
            return view;
        }

        internal static async Task RouteMouse(Vector2 point, MouseButton? button = null, Vector2 delta = default, float wheel = 0) {
            var state = new MouseState { position = point, delta = delta, scroll = new Vector2(0, wheel) };
            InputSystem.QueueStateEvent(Mouse.current, button.HasValue ? state.WithButton(button.Value) : state);
            await Frames();
        }

        internal static async Task RouteClick(Vector2 point) {
            await RouteMouse(point);
            await RouteMouse(point, MouseButton.Left);
            await RouteMouse(point);
        }

        internal static Task RouteClick(Component control) => RouteClick(Point((RectTransform)control.transform, Vector2.one * .5f));

        internal static async Task CheckRouteNavigation(GameObject canvas, Action checkRoute) {
            var pivot = PivotCamera.Main;
            var backdrop = canvas.transform.Find("Input capture").gameObject;
            var point = new Vector2(Screen.width * .24f, Screen.height * .55f);
            var delta = new Vector2(12, 6);
            Require(Hit(point) == backdrop, "The navigation test point is outside the exposed routing viewport.");
            await RouteMouse(point);
            Require(MouseInput.overUI && Application.isFocused, "The routing backdrop must shield the scene in a focused Game View.");

            var rotation = pivot.transform.rotation;
            await RouteMouse(point, MouseButton.Right);
            await RouteMouse(point + delta, MouseButton.Right, delta);
            await RouteMouse(point + delta);
            Require(Quaternion.Angle(rotation, pivot.transform.rotation) > .1f, "Viewport orbit did not respond in " + canvas.name);
            checkRoute();

            var position = pivot.transform.position;
            await RouteMouse(point, MouseButton.Middle);
            await RouteMouse(point + delta, MouseButton.Middle, delta);
            await RouteMouse(point + delta);
            Require(Vector3.Distance(position, pivot.transform.position) > .01f, "Viewport pan did not respond in " + canvas.name);
            checkRoute();

            float distance = pivot.focusDistance;
            await RouteMouse(point, wheel: 120);
            Require(!Mathf.Approximately(distance, pivot.focusDistance), "Viewport wheel did not zoom in " + canvas.name);
            checkRoute();

            position = pivot.transform.position;
            rotation = pivot.transform.rotation;
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.LeftShift, Key.W));
            await Frames();
            await RouteMouse(point + delta, delta: delta);
            Require(InputEvent.IsNoclipMovementCaptured && Vector3.Distance(position, pivot.transform.position) > .01f &&
                Quaternion.Angle(rotation, pivot.transform.rotation) > .1f, "Viewport free-fly did not move and look in " + canvas.name);
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
            await Frames();
            Require(!InputEvent.IsNoclipMovementCaptured, "Free-fly release retained input capture.");
            checkRoute();

            var scroll = canvas.GetComponentInChildren<ScrollRect>();
            // The geometry fixtures use short routes. Make their existing rows overflow.
            float height = scroll.viewport.rect.height;
            scroll.viewport.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 80);
            try {
                await Frames();
                var scrollPoint = Point(scroll.viewport, new Vector2(.98f, .5f));
                distance = pivot.focusDistance;
                float start = scroll.content.anchoredPosition.y;
                await RouteMouse(scrollPoint, wheel: -1200);
                Require(scroll.content.anchoredPosition.y > start && Mathf.Approximately(distance, pivot.focusDistance),
                    "Panel wheel failed to scroll independently of the camera in " + canvas.name);
                rotation = pivot.transform.rotation;
                await RouteMouse(scrollPoint, MouseButton.Right);
                await RouteMouse(point, MouseButton.Right, point - scrollPoint);
                await RouteMouse(point);
                Require(Quaternion.Angle(rotation, pivot.transform.rotation) < .01f,
                    "A press that began on the route panel leaked into viewport orbit in " + canvas.name);
                checkRoute();
            }
            finally {
                scroll.viewport.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                scroll.verticalNormalizedPosition = 1;
            }
        }

        internal static void CheckRouteMarker(GameObject canvas, int number, Vector3 world) {
            var marker = (RectTransform)canvas.transform.Find("Route marker " + number);
            var screen = (Vector2)PivotCamera.Main.camera.WorldToScreenPoint(world);
            Require(marker.gameObject.activeSelf && Vector2.Distance(Point(marker, Vector2.one * .5f), screen) < 1,
                "A numbered route marker did not follow its projected part in " + canvas.name);
        }

        private static void Near(float actual, float expected, string message) => Require(Mathf.Abs(actual - expected) < .00001f, message + $" ({actual} vs {expected})");
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
