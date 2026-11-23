using System.Linq;
using Protobot.UI;
using Protobot.InputEvents;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    /// <summary>Upgrades Main's legacy camera sliders without scene serialization churn.</summary>
    public static class CameraNavigationPreferencesInstaller {
        private readonly struct Definition {
            public readonly string name, label;
            public readonly NumericPreferenceField.CameraValue value;
            public readonly float fallback, min, max;
            public Definition(string name, string label, NumericPreferenceField.CameraValue value, float fallback, float min, float max) {
                this.name = name; this.label = label; this.value = value; this.fallback = fallback; this.min = min; this.max = max;
            }
        }

        private static readonly Definition[] Definitions = {
            new("Orbit Sensitivity", "Orbit", NumericPreferenceField.CameraValue.OrbitSensitivity, .1f, .001f, 5f),
            new("Pan Sensitivity", "Pan", NumericPreferenceField.CameraValue.PanSensitivity, .05f, .0001f, 2f),
            new("Zoom Sensitivity", "Zoom", NumericPreferenceField.CameraValue.ZoomSensitivity, 1f, .01f, 10f),
            new("Free-fly Look Sensitivity", "Free-fly look", NumericPreferenceField.CameraValue.MouseLookSensitivity, .1f, .001f, 5f),
            new("Free-fly Movement Speed", "Free-fly speed", NumericPreferenceField.CameraValue.MovementSpeed, 15f, .1f, 500f)
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Install() {
            var camera = Object.FindFirstObjectByType<PivotCamera>(FindObjectsInactive.Include);
            var cameraMenu = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .SingleOrDefault(item => item.name == "Camera Settings Menu");
            if (camera == null || cameraMenu == null) return;
            var mouse = camera.GetComponent<MousePivotCameraInput>();
            var noclip = camera.GetComponent<NoclipCameraInput>();
            var left = cameraMenu.GetComponentsInChildren<RectTransform>(true).Single(item => item.name == "Left Column");
            var resetPrototype = Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(button => button.name == "Reset Rebinds Button");

            // Prevent saved legacy slider values from applying after these
            // fields have initialized. NumericPreferenceField migrates them.
            foreach (var setting in Object.FindObjectsByType<Setting>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (setting.name == "Orbit Sensitivity" || setting.name == "Pan Sensitivity" || setting.name == "Zoom Sensitivitiy")
                    setting.enabled = false;

            for (int i = 0; i < Definitions.Length; i++) ConfigureField(left, Definitions[i], i, mouse, noclip, resetPrototype);
            LayoutCategory((RectTransform)cameraMenu, left);

            var categories = (RectTransform)cameraMenu.parent;
            if (categories.GetComponent<RectMask2D>() == null) categories.gameObject.AddComponent<RectMask2D>();
            if (categories.GetComponent<PreferencesCategoryScroller>() == null) categories.gameObject.AddComponent<PreferencesCategoryScroller>();
        }

        private static void LayoutCategory(RectTransform category, RectTransform left) {
            // The authored columns were anchored to the viewport bottom while the
            // keybinds were anchored to its top. Give the category a content height
            // derived from its live rows, independent of the window/scroll viewport.
            float top = 45f;
            var rows = category.GetComponentsInChildren<RebindUI>(true)
                .Where(row => row.gameObject.activeSelf)
                .Select(row => (RectTransform)row.transform)
                .OrderByDescending(row => row.anchoredPosition.y).ToArray();
            foreach (var row in rows) {
                row.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, top, row.rect.height);
                top += row.rect.height + 5f;
            }
            top += 5f;
            float fieldHeight = 35f + (Definitions.Length - 1) * 29f + 25f;
            foreach (var column in new[] { left, (RectTransform)category.Find("Right Column") }) {
                column.anchorMin = new Vector2(0f, 1f);
                column.anchorMax = Vector2.one;
                column.pivot = new Vector2(.5f, 1f);
                column.anchoredPosition = new Vector2(column.anchoredPosition.x, -top);
                column.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, column == left ? fieldHeight : 120f);
            }
            category.anchorMin = new Vector2(0f, 1f);
            category.anchorMax = Vector2.one;
            category.pivot = new Vector2(.5f, 1f);
            category.anchoredPosition = Vector2.zero;
            category.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, top + fieldHeight + 12f);
        }

        private static void ConfigureField(RectTransform parent, Definition definition, int index,
            MousePivotCameraInput mouse, NoclipCameraInput noclip, Button resetPrototype) {
            var row = parent.Cast<Transform>().FirstOrDefault(item => item.name == definition.name ||
                definition.value == NumericPreferenceField.CameraValue.ZoomSensitivity && item.name == "Zoom Sensitivitiy") as RectTransform;
            if (row == null) {
                var obj = new GameObject(definition.name, typeof(RectTransform)); obj.layer = parent.gameObject.layer;
                row = obj.GetComponent<RectTransform>(); row.SetParent(parent, false);
            }
            row.name = definition.name;
            row.anchorMin = row.anchorMax = new Vector2(.5f, 1f); row.pivot = new Vector2(.5f, 1f);
            row.anchoredPosition = new Vector2(0f, -35f - index * 29f); row.sizeDelta = new Vector2(165f, 25f);

            foreach (var slider in row.GetComponentsInChildren<Slider>(true)) {
                slider.interactable = false;
                Object.Destroy(slider.gameObject);
            }
            var title = row.Cast<Transform>().FirstOrDefault(item => item.name == "Title")?.GetComponent<Text>();
            if (title == null) title = CreateText(row, "Title", TextAnchor.MiddleLeft);
            title.text = definition.label;
            title.resizeTextForBestFit = true; title.resizeTextMinSize = 9; title.resizeTextMaxSize = 13;
            var titleRect = (RectTransform)title.transform;
            titleRect.anchorMin = Vector2.zero; titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = Vector2.zero; titleRect.offsetMax = new Vector2(-90f, 0f);

            var fieldRect = row.Find("Numeric Field") as RectTransform;
            if (fieldRect == null) {
                var obj = new GameObject("Numeric Field", typeof(RectTransform), typeof(Image), typeof(InputField));
                obj.layer = row.gameObject.layer; fieldRect = obj.GetComponent<RectTransform>(); fieldRect.SetParent(row, false);
                var text = CreateText(fieldRect, "Text", TextAnchor.MiddleRight);
                var textRect = (RectTransform)text.transform; textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(5f, 1f); textRect.offsetMax = new Vector2(-5f, -1f);
                var placeholder = CreateText(fieldRect, "Placeholder", TextAnchor.MiddleRight);
                placeholder.text = "value"; placeholder.color = new Color(1f, 1f, 1f, .35f);
                var placeholderRect = (RectTransform)placeholder.transform;
                placeholderRect.anchorMin = Vector2.zero; placeholderRect.anchorMax = Vector2.one;
                placeholderRect.offsetMin = new Vector2(5f, 1f); placeholderRect.offsetMax = new Vector2(-5f, -1f);
                var input = obj.GetComponent<InputField>(); input.textComponent = text; input.placeholder = placeholder;
                input.targetGraphic = obj.GetComponent<Image>(); input.lineType = InputField.LineType.SingleLine;
            }
            fieldRect.anchorMin = fieldRect.anchorMax = new Vector2(1f, .5f); fieldRect.pivot = new Vector2(1f, .5f);
            fieldRect.anchoredPosition = new Vector2(-30f, 0f); fieldRect.sizeDelta = new Vector2(55f, 20f);
            fieldRect.GetComponent<Image>().color = new Color(.18f, .18f, .18f, 1f);
            var numeric = fieldRect.GetComponent<NumericPreferenceField>() ?? fieldRect.gameObject.AddComponent<NumericPreferenceField>();
            numeric.Configure(fieldRect.GetComponent<InputField>(), definition.value, mouse, noclip,
                definition.fallback, definition.min, definition.max);

            var defaultButton = ConfigureDefaultButton(row, resetPrototype);
            numeric.SetDefaultButton(defaultButton);
        }

        private static Button ConfigureDefaultButton(RectTransform row, Button prototype) {
            var rect = row.Find("Set Default Button") as RectTransform;
            if (rect == null) {
                var obj = new GameObject("Set Default Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                obj.layer = row.gameObject.layer; rect = obj.GetComponent<RectTransform>(); rect.SetParent(row, false);
            }
            rect.anchorMin = rect.anchorMax = new Vector2(1f, .5f); rect.pivot = new Vector2(1f, .5f);
            rect.anchoredPosition = new Vector2(-10f, 0f); rect.sizeDelta = new Vector2(15f, 15f);
            var image = rect.GetComponent<Image>();
            var button = rect.GetComponent<Button>();
            if (prototype != null) {
                var prototypeImage = prototype.targetGraphic as Image;
                if (prototypeImage != null) {
                    image.sprite = prototypeImage.sprite; image.type = prototypeImage.type;
                    image.preserveAspect = prototypeImage.preserveAspect; image.color = prototypeImage.color;
                }
                button.transition = prototype.transition; button.colors = prototype.colors; button.spriteState = prototype.spriteState;
            }
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            return button;
        }

        private static Text CreateText(Transform parent, string name, TextAnchor alignment) {
            var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            obj.layer = parent.gameObject.layer; obj.transform.SetParent(parent, false);
            var text = obj.GetComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 13; text.alignment = alignment; text.color = Color.white; text.supportRichText = false;
            return text;
        }
    }
}
