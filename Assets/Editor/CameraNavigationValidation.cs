using System;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using Protobot;
using Protobot.InputEvents;
using Protobot.Tools;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

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
                Require(LocalPreferences.GetString(key) == NumericPreferenceField.Format(0.123456789f), "Numeric preference lost its exact display value.");
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

        private static void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try { CheckRuntime(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static void CheckRuntime() {
            var fields = Object.FindObjectsByType<NumericPreferenceField>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Require(fields.Length == 5, "Runtime camera Preferences did not install exactly five numeric fields.");
            Require(fields.Select(field => field.PreferenceKey).Distinct().Count() == 5, "Camera fields do not use distinct preference keys.");
            foreach (var field in fields) {
                var input = field.GetComponent<InputField>();
                Require(input != null && input.navigation.mode == Navigation.Mode.Automatic, "A camera field is not keyboard navigable.");
                Require(input.text == NumericPreferenceField.Format(field.Current), "A camera field does not show its exact current value.");
            }
            var categories = fields[0].GetComponentInParent<PreferencesCategoryScroller>(true);
            Require(categories != null && categories.GetComponent<RectMask2D>() != null, "Preferences category scrolling/clipping was not installed.");
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
            }
            finally {
                if (had) LocalPreferences.SetString(test.PreferenceKey, previous); else LocalPreferences.DeleteKey(test.PreferenceKey);
                LocalPreferences.Save();
            }
            Debug.Log("Camera navigation smoke passed: immediate orbit/pan/zoom, numeric UI/persistence, scrolling, keyboard focus and modal ownership.");
        }

        private static void Near(float actual, float expected, string message) => Require(Mathf.Abs(actual - expected) < .00001f, message + $" ({actual} vs {expected})");
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
