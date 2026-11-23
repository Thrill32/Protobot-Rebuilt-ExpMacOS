using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    public sealed class NumericPreferenceField : MonoBehaviour {
        public enum CameraValue { OrbitSensitivity, PanSensitivity, ZoomSensitivity, MouseLookSensitivity, MovementSpeed }

        [SerializeField] private InputField field;
        [SerializeField] private CameraValue value;
        [SerializeField] private MousePivotCameraInput mouseInput;
        [SerializeField] private NoclipCameraInput noclipInput;
        [SerializeField] private float defaultValue;
        [SerializeField] private float minimum;
        [SerializeField] private float maximum;
        [SerializeField] private Button defaultButton;

        private float current;
        private bool bound;
        public float Current => current;
        public float DefaultValue => defaultValue;
        public bool IsDefault => current == defaultValue;
        public string PreferenceKey => Key(value);

        private void Awake() {
            if (field == null) field = GetComponent<InputField>();
            if (field != null) Bind();
        }

        private void Bind() {
            if (bound || field == null) return;
            bound = true;
            field.contentType = InputField.ContentType.Standard;
            field.characterLimit = 16;
            field.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            field.onEndEdit.AddListener(CommitFromUI);
        }

        public void Configure(InputField input, CameraValue cameraValue, MousePivotCameraInput mouse,
            NoclipCameraInput noclip, float fallback, float min, float max) {
            field = input;
            value = cameraValue;
            mouseInput = mouse;
            noclipInput = noclip;
            defaultValue = fallback;
            minimum = min;
            maximum = max;
            Bind();
            Initialize();
        }

        public void Initialize() {
            float loaded = LoadValue();
            current = InBounds(loaded) ? loaded : defaultValue;
            Apply(current);
            Display(current);
            UpdateDefaultButton();
        }

        private void CommitFromUI(string text) => TryCommit(text);

        public bool TryCommit(string text) {
            if (!TryParse(text, minimum, maximum, out var parsed)) {
                Display(current);
                return false;
            }
            current = parsed;
            Apply(current);
            LocalPreferences.SetFloat(PreferenceKey, current);
            LocalPreferences.Save();
            Display(current);
            UpdateDefaultButton();
            return true;
        }

        public void SetDefaultButton(Button button) {
            if (defaultButton != null) defaultButton.onClick.RemoveListener(SetToDefault);
            defaultButton = button;
            if (defaultButton != null) {
                defaultButton.onClick.RemoveAllListeners();
                defaultButton.onClick.AddListener(SetToDefault);
            }
            UpdateDefaultButton();
        }

        public void SetToDefault() {
            current = defaultValue;
            Apply(current);
            // Persist the default explicitly so an older slider preference is
            // not migrated back on the next launch.
            LocalPreferences.SetFloat(PreferenceKey, current);
            LocalPreferences.Save();
            Display(current);
            UpdateDefaultButton();
        }

        private float LoadValue() {
            if (LocalPreferences.HasKey(PreferenceKey))
                return LocalPreferences.GetFloat(PreferenceKey, defaultValue);
            string legacy = LegacyKey(value);
            if (!string.IsNullOrEmpty(legacy) && LocalPreferences.HasKey(legacy)) {
                float migrated = LocalPreferences.GetFloat(legacy, defaultValue);
                if (value == CameraValue.ZoomSensitivity) migrated *= 400f;
                if (InBounds(migrated)) {
                    LocalPreferences.SetFloat(PreferenceKey, migrated);
                    LocalPreferences.Save();
                    return migrated;
                }
            }
            return defaultValue;
        }

        private void Apply(float number) {
            switch (value) {
                case CameraValue.OrbitSensitivity: mouseInput.SetOrbitSensitivity(number); break;
                case CameraValue.PanSensitivity: mouseInput.SetPanSensitivity(number); break;
                case CameraValue.ZoomSensitivity: mouseInput.SetZoomSensitivity(number); break;
                case CameraValue.MouseLookSensitivity: noclipInput.SetMouseSensitivity(number); break;
                case CameraValue.MovementSpeed: noclipInput.SetMovementSpeed(number); break;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        private bool InBounds(float number) => LocalPreferences.IsFinite(number) && number >= minimum && number <= maximum;
        private void Display(float number) { if (field != null) field.SetTextWithoutNotify(Format(number)); }
        private void UpdateDefaultButton() { if (defaultButton != null) defaultButton.interactable = !IsDefault; }
        public static string Format(float number) => number.ToString("R", CultureInfo.InvariantCulture);
        public static bool TryParse(string text, float min, float max, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            LocalPreferences.IsFinite(value) && value >= min && value <= max;

        public static string Key(CameraValue value) => "Camera." + value;
        private static string LegacyKey(CameraValue value) => value switch {
            CameraValue.OrbitSensitivity => "Orbit Sensitivity",
            CameraValue.PanSensitivity => "Pan Sensitivity",
            CameraValue.ZoomSensitivity => "Zoom Sensitivitiy",
            _ => null
        };
    }
}
