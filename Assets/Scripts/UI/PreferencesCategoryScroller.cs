using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Protobot.UI {
    /// <summary>Scrolls the active Preferences category without reparenting its controls.</summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class PreferencesCategoryScroller : MonoBehaviour, IScrollHandler {
        [SerializeField] private float scrollSensitivity = 28f;
        private readonly Dictionary<RectTransform, Vector2> origins = new();
        private readonly Dictionary<RectTransform, float> offsets = new();
        private RectTransform viewport;

        private void Awake() {
            viewport = (RectTransform)transform;
            CaptureOrigins();
        }

        public void OnScroll(PointerEventData eventData) {
            var active = ActiveCategory();
            if (active == null) return;
            float offset = Offset(active) - eventData.scrollDelta.y * scrollSensitivity;
            SetOffset(active, offset);
            eventData.Use();
        }

        private void LateUpdate() {
            var active = ActiveCategory();
            if (active == null) return;
            SetOffset(active, Offset(active));
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == null || !selected.transform.IsChildOf(active)) return;
            var rect = selected.transform as RectTransform;
            if (rect == null) return;
            var selectedBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, rect);
            float correction = 0f;
            if (selectedBounds.min.y < viewport.rect.yMin + 8f)
                correction = viewport.rect.yMin + 8f - selectedBounds.min.y;
            else if (selectedBounds.max.y > viewport.rect.yMax - 8f)
                correction = viewport.rect.yMax - 8f - selectedBounds.max.y;
            if (Mathf.Abs(correction) > .01f) SetOffset(active, Offset(active) + correction);
        }

        public void CaptureOrigins() {
            if (viewport == null) viewport = (RectTransform)transform;
            foreach (Transform child in transform) if (child is RectTransform rect && !origins.ContainsKey(rect)) {
                origins.Add(rect, rect.anchoredPosition);
                offsets.Add(rect, 0f);
            }
        }

        private RectTransform ActiveCategory() {
            CaptureOrigins();
            foreach (Transform child in transform)
                if (child is RectTransform rect && rect.gameObject.activeInHierarchy) return rect;
            return null;
        }

        private float Offset(RectTransform category) => offsets.TryGetValue(category, out var value) ? value : 0f;

        private void SetOffset(RectTransform category, float requested) {
            var origin = origins[category];
            category.anchoredPosition = origin;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, category);
            float maximum = Mathf.Max(0f, viewport.rect.yMin - bounds.min.y);
            float offset = Mathf.Clamp(requested, 0f, maximum);
            offsets[category] = offset;
            category.anchoredPosition = origin + Vector2.up * offset;
        }

        private void OnRectTransformDimensionsChange() {
            if (isActiveAndEnabled) {
                var active = ActiveCategory();
                if (active != null) SetOffset(active, Offset(active));
            }
        }
    }
}
