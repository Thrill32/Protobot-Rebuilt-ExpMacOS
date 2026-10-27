using System.Collections.Generic;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot.UI {
    /// <summary>One bottom-left stack for held modifiers and modal transform help.</summary>
    public sealed class InteractionFeedback : MonoBehaviour {
        private ObjectDuplicator duplicator;
        private MultiSelector multiSelector;
        private Canvas[] canvases;
        private readonly List<string> messages = new();
        private readonly List<Rect> occupied = new();
        private readonly Vector3[] corners = new Vector3[4];

        public void Initialize(ObjectDuplicator source) => duplicator = source;

        private void Start() {
            multiSelector = FindFirstObjectByType<MultiSelector>();
            canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        public static void AddModifierMessages(List<string> result, bool duplicate, bool multiple, bool bypass) {
            if (duplicate) result.Add("Duplicating");
            if (multiple) result.Add("Selecting multiple");
            if (bypass) result.Add("Bypassing grouping · Snap off");
        }

        private void OnGUI() {
            if (RebindAction.Rebinding) return;
            messages.Clear();
            AddModifierMessages(messages, duplicator != null && duplicator.IsDuplicateFeedbackHeld,
                multiSelector != null && multiSelector.IsMultiSelectHeld, SnapBypassInput.IsHeld);
            if (KeyboardTransformTool.ControlsText != null) messages.Add(KeyboardTransformTool.ControlsText);
            if (messages.Count == 0) return;

            occupied.Clear();
            if (canvases != null) foreach (var canvas in canvases) {
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                // The registry includes notifications/buttons created after startup.
                // Unity's IndexedSet implements indexing but its enumerator
                // throws NotImplementedException. Do not foreach this registry.
                var graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
                for (int i = 0; i < graphics.Count; i++) {
                    var graphic = graphics[i];
                    if (graphic == null || !graphic.isActiveAndEnabled || graphic.canvas == null ||
                        graphic.color.a <= 0 || graphic.canvasRenderer.GetInheritedAlpha() <= .01f) continue;
                    var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    graphic.rectTransform.GetWorldCorners(corners);
                    var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                    var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                    foreach (var corner in corners) {
                        var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                        point.y = Screen.height - point.y;
                        min = Vector2.Min(min, point); max = Vector2.Max(max, point);
                    }
                    // Reserve visible footer text, buttons and notifications. Large
                    // backgrounds/panels do not represent a bottom-left message.
                    var rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                    if (rect.yMax > Screen.height * .75f && rect.yMin < Screen.height &&
                        rect.height < Screen.height * .25f && rect.width > 0 && rect.height > 0)
                        occupied.Add(rect);
                }
            }
            TransformFeedback.DrawControls(messages, occupied);
        }
    }
}
