using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot.Simulation {
    /// <summary>Small runtime canvas; all controls participate in normal UI pointer arbitration.</summary>
    internal sealed class SimulationPanel : IDisposable {
        private readonly SimulationTool tool;
        private readonly GameObject canvasObject;
        private readonly CanvasGroup inputGroup;
        private readonly RectTransform panel, controls, content;
        private readonly Text status, help, clock, playLabel, speedLabel;
        private readonly Slider timeline, speed;
        private readonly Button play, reset, commit;
        private readonly List<(MechanismSimulation.Track track, Text text, RectTransform marker)> rows = new();
        private bool updating;
        private readonly Color accent = new(.18f, .67f, 1f, 1);
        public bool ContainsPointer => canvasObject != null && canvasObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(panel, MouseInput.Position);

        public SimulationPanel(SimulationTool tool) {
            this.tool = tool;
            canvasObject = new GameObject("Simulation Controls", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            inputGroup = canvasObject.AddComponent<CanvasGroup>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 110;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.matchWidthOrHeight = .5f;
            panel = Rect("Simulation Panel", canvasObject.transform, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 64), new Vector2(730, 70));
            Background(panel, new Color(.055f, .065f, .08f, .97f));
            status = Label(panel, "Status", "", 14, new Vector2(14, -8), new Vector2(-28, 24));
            help = Label(panel, "Help", "", 12, new Vector2(14, -33), new Vector2(-28, 22));
            controls = Rect("Animation", panel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -62), new Vector2(730, 210));
            play = Button(controls, "Play Pause", "Play", 14, 0, 66, tool.TogglePlayback);
            playLabel = play.GetComponentInChildren<Text>();
            reset = Button(controls, "Reset", "Reset", 86, 0, 64, tool.ResetMotion);
            commit = Button(controls, "Set Pose", "Set pose", 156, 0, 78, tool.Commit);
            Button(controls, "Revert", "Revert", 240, 0, 68, tool.Cancel);
            speedLabel = Label(controls, "Speed Label", "1×", 13, new Vector2(322, 0), new Vector2(54, 28), false);
            speed = Slider(controls, "Speed", new Vector2(380, -9), new Vector2(150, 12), .1f, 4);
            speed.onValueChanged.AddListener(value => { if (!updating) tool.SetSpeed(value); });
            clock = Label(controls, "Clock", "", 12, new Vector2(14, -34), new Vector2(-28, 24));
            timeline = Slider(controls, "Timeline", new Vector2(14, -66), new Vector2(700, 10), 0, 1);
            timeline.onValueChanged.AddListener(value => { if (!updating && tool.Current != null) tool.Seek(value * tool.Current.LoopDuration); });

            var viewport = Rect("Tracks", controls, Vector2.up, Vector2.up, new Vector2(14, -86), new Vector2(700, 112));
            Background(viewport, new Color(0, 0, 0, .08f));
            viewport.gameObject.AddComponent<RectMask2D>();
            content = Rect("Track Content", viewport, Vector2.up, Vector2.up, Vector2.zero, new Vector2(700, 0));
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
            canvasObject.SetActive(false);
        }

        public void RebuildTracks() {
            if (canvasObject == null) return;
            rows.Clear();
            foreach (Transform child in content) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); }
            var model = tool.Current;
            if (model == null) { content.sizeDelta = new Vector2(700, 0); Refresh(); return; }
            int index = 0;
            foreach (var track in model.tracks.Where(t => t.rotates && t.showInTimeline)) {
                var row = Rect("Movement " + index, content, Vector2.up, Vector2.up, new Vector2(0, -index * 29), new Vector2(700, 27));
                var text = Label(row, "Part and Period", "", 11, new Vector2(0, 0), new Vector2(235, 26), false);
                var line = Rect("Loop Track", row, Vector2.up, Vector2.up, new Vector2(244, -12), new Vector2(450, 2));
                Background(line, new Color(.35f, .4f, .46f, 1));
                int loops = track.linear ? 0 : Mathf.RoundToInt((float)(model.LoopDuration / track.Period));
                int stride = Mathf.Max(1, Mathf.CeilToInt(loops / 64f));
                for (int tick = 0; tick <= loops; tick += stride) {
                    var mark = Rect("Loop Boundary", line, Vector2.zero, Vector2.zero,
                        new Vector2(450f * tick / Mathf.Max(1, loops), 4), new Vector2(1, 10));
                    Background(mark, new Color(.4f, .48f, .58f, 1));
                }
                var marker = Rect("Playhead", line, Vector2.zero, Vector2.zero, new Vector2(0, 7), new Vector2(2, 16));
                Background(marker, accent);
                rows.Add((track, text, marker));
                index++;
            }
            content.sizeDelta = new Vector2(700, index * 29);
            content.anchoredPosition = Vector2.zero;
            Refresh();
        }

        public void Refresh() {
            if (canvasObject == null) return;
            canvasObject.SetActive(SimulationTool.IsActive);
            if (!canvasObject.activeSelf) return;
            inputGroup.interactable = !tool.IsTypingRotation;
            float width = Mathf.Clamp(((RectTransform)canvasObject.transform).rect.width - 150, 260, 730);
            panel.sizeDelta = new Vector2(width, tool.AnimationVisible ? 280 : 64);
            // Scale just the animation interior on narrower windows; the status remains readable.
            controls.localScale = Vector3.one * Mathf.Min(1, width / 730);
            controls.gameObject.SetActive(tool.AnimationVisible);
            status.text = "Simulation · " + tool.Status + (tool.Current?.DriverRotates == true
                ? $" · {tool.Current.DriverDegrees:0.##}° relative / {tool.Current.GlobalDriverDegrees:0.##}° global" : "");
            help.text = tool.ControlsText;
            var model = tool.Current;
            play.interactable = reset.interactable = commit.interactable = model != null;
            playLabel.text = tool.IsPlaying ? "Pause" : "Play";
            speedLabel.text = $"{tool.Speed:0.##}×";
            double duration = model?.LoopDuration ?? 4;
            double time = model?.Time ?? 0;
            double phase = ((time % duration) + duration) % duration;
            clock.text = model == null ? "Select a motor, gear or shaft to animate" :
                $"{time / tool.Speed:0.00}s   /   {duration / tool.Speed:0.##}s {(model.HasFiniteLoop ? "loop" : "window")}    ·    ticks mark complete turns";
            updating = true;
            timeline.SetValueWithoutNotify((float)(phase / duration));
            speed.SetValueWithoutNotify(tool.Speed);
            updating = false;
            foreach (var row in rows) {
                row.text.text = row.track.linear ? $"{row.track.Label} · {row.track.LinearSpeed * tool.Speed:0.##} in/s" : $"{row.track.Label} · {row.track.Period / tool.Speed:0.##}s / turn";
                row.marker.anchoredPosition = new Vector2((float)(phase / duration) * 450, 7);
            }
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size) {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = anchor; rect.pivot = pivot;
            rect.anchoredPosition = pos; rect.sizeDelta = size;
            return rect;
        }
        private static Image Background(RectTransform rect, Color color) { var image = rect.gameObject.AddComponent<Image>(); image.color = color; return image; }
        private static Text Label(Transform parent, string name, string text, int fontSize, Vector2 pos, Vector2 size, bool stretch = true) {
            var rect = Rect(name, parent, Vector2.up, Vector2.up, pos, size);
            if (stretch) rect.anchorMax = Vector2.one;
            var label = rect.gameObject.AddComponent<Text>(); label.font = TransformFeedback.FeedbackFont; label.fontSize = fontSize;
            label.color = Color.white; label.alignment = TextAnchor.MiddleLeft; label.text = text; label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow; label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }
        private static Button Button(Transform parent, string name, string text, float x, float y, float width, UnityEngine.Events.UnityAction action) {
            var rect = Rect(name, parent, Vector2.up, Vector2.up, new Vector2(x, y), new Vector2(width, 28));
            Background(rect, new Color(.15f, .19f, .24f));
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = rect.GetComponent<Image>();
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(action);
            var label = Label(rect, "Label", text, 12, Vector2.zero, Vector2.zero); label.alignment = TextAnchor.MiddleCenter;
            ((RectTransform)label.transform).anchorMin = Vector2.zero;
            return button;
        }
        private Slider Slider(Transform parent, string name, Vector2 pos, Vector2 size, float min, float max) {
            var rect = Rect(name, parent, Vector2.up, Vector2.up, pos, size);
            Background(rect, new Color(.25f, .29f, .34f));
            var slider = rect.gameObject.AddComponent<Slider>();
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.minValue = min; slider.maxValue = max;
            var handle = Rect("Handle", rect, new Vector2(0, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(9, 18));
            Background(handle, accent);
            slider.handleRect = handle; slider.targetGraphic = handle.GetComponent<Image>();
            return slider;
        }
        public void Dispose() { if (canvasObject != null) UnityEngine.Object.Destroy(canvasObject); }
    }
}
