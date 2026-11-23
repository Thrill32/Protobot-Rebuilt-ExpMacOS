using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Protobot.UI {
    /// <summary>Scrolls the active Preferences category without reparenting its controls.</summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class PreferencesCategoryScroller : MonoBehaviour, IScrollHandler {
        [SerializeField] private float scrollSensitivity = 28f;
        private readonly Dictionary<RectTransform, Vector2> origins = new();
        private readonly Dictionary<RectTransform, float> offsets = new();
        private RectTransform viewport;
        private GameObject lastSelected;
        private bool revealFocus;
        private RectTransform dialog;
        private float preferredHeight;
        private Vector2 windowSize;

        private void Awake() {
            viewport = (RectTransform)transform;
            dialog = transform.parent as RectTransform;
            preferredHeight = dialog.rect.height;
            // Scroll dispatch starts at the raycast hit and walks its ancestors.
            // Main's authored container Image is disabled, leaving blank areas
            // to hit the Preferences background outside this scroller hierarchy.
            var surface = GetComponent<Image>() ?? gameObject.AddComponent<Image>();
            surface.color = Color.clear;
            surface.raycastTarget = true;
            surface.enabled = true;
            CaptureOrigins();
        }

        public void OnScroll(PointerEventData eventData) {
            var active = ActiveCategory();
            if (active == null) return;
            float offset = Offset(active) - eventData.scrollDelta.y * scrollSensitivity;
            SetOffset(active, offset);
            revealFocus = false;
            eventData.Use();
        }

        private void LateUpdate() {
            FitWindow();
            var active = ActiveCategory();
            if (active == null) return;
            SetOffset(active, Offset(active));
            var selected = EventSystem.current?.currentSelectedGameObject;
            bool changed = selected != lastSelected;
            lastSelected = selected;
            // Once revealed, focused fields retain their caret while the user
            // scrolls away. Re-revealing every frame undoes wheel scrolling.
            if (!changed && !revealFocus) return;
            revealFocus = false;
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

        private void FitWindow() {
            if (dialog == null || dialog.parent is not RectTransform window) return;
            if (windowSize == window.rect.size) return;
            windowSize = window.rect.size;
            var header = dialog.Find("Upper Tab") as RectTransform;
            float headerHeight = header != null ? header.rect.height : 0f;
            float available = Mathf.Max(100f, window.rect.height - headerHeight - 20f);
            dialog.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Min(preferredHeight, available));
            // Center the complete dialog (header plus body) in its canvas.
            dialog.anchoredPosition = new Vector2(dialog.anchoredPosition.x, -headerHeight * .5f);
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
            revealFocus = true;
            if (isActiveAndEnabled) {
                var active = ActiveCategory();
                if (active != null) SetOffset(active, Offset(active));
            }
        }
    }
}
