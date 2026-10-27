using System.Collections.Generic;
using UnityEngine;

namespace Protobot.Tools {
    /// <summary>Shared mouse-adjacent measurements for handle and keyboard transforms.</summary>
    public static class TransformFeedback {
        public static string Distance(Vector3 offset, Vector3 axis) =>
            $"{(axis == Vector3.zero ? offset.magnitude : Vector3.Dot(offset, axis)):0.###} in";

        public static string Angle(float degrees) => $"{TransformSnapping.WrapAngle(degrees):0.##}°";

        public static float ScaleForScreen(float width, float height) =>
            Mathf.Max(1f, Mathf.Min(width / 1600f, height / 900f));
        public static float Scale => ScaleForScreen(Screen.width, Screen.height);

        public static Rect KeepOnScreen(Rect rect, Vector2 screen) {
            float margin = Mathf.Min(8f, Mathf.Max(0f, Mathf.Min(screen.x, screen.y) / 2f));
            rect.width = Mathf.Clamp(rect.width, 0f, Mathf.Max(0f, screen.x - margin * 2f));
            rect.height = Mathf.Clamp(rect.height, 0f, Mathf.Max(0f, screen.y - margin * 2f));
            rect.x = Mathf.Clamp(rect.x, margin, Mathf.Max(margin, screen.x - margin - rect.width));
            rect.y = Mathf.Clamp(rect.y, margin, Mathf.Max(margin, screen.y - margin - rect.height));
            return rect;
        }

        private static Font feedbackFont;
        public static Font FeedbackFont => feedbackFont != null ? feedbackFont :
            feedbackFont = Resources.Load<Font>("Fonts/InteractionFont");

        private static GUIStyle BoxStyle(int fontSize, TextAnchor alignment, float scale) {
            return new GUIStyle {
                font = FeedbackFont, normal = { textColor = Color.white },
                fontSize = Mathf.RoundToInt(fontSize * scale), alignment = alignment,
                wordWrap = true, richText = false, clipping = TextClipping.Clip,
                padding = new RectOffset(Mathf.RoundToInt(14f * scale), Mathf.RoundToInt(14f * scale),
                    Mathf.RoundToInt(10f * scale), Mathf.RoundToInt(10f * scale))
            };
        }

        private static GUIStyle BoxStyle(int fontSize, TextAnchor alignment) => BoxStyle(fontSize, alignment, Scale);

        public static Vector2 MeasurementSize(string measurement, float scale) {
            var style = BoxStyle(22, TextAnchor.MiddleCenter, scale);
            style.wordWrap = false;
            var size = style.CalcSize(new GUIContent(measurement));
            return new Vector2(Mathf.Ceil(size.x) + 2f * scale, Mathf.Ceil(size.y));
        }

        private static Rect BesideMouse(float width, float height) {
            float scale = Scale;
            return BesideMouse(new Vector2(width, height) * scale, scale);
        }

        private static Rect BesideMouse(Vector2 size, float scale) {
            var mouse = MouseInput.Position;
            return KeepOnScreen(new Rect(mouse.x + 22f * scale, Screen.height - mouse.y - size.y - 18f * scale,
                size.x, size.y), new Vector2(Screen.width, Screen.height));
        }

        public static void Draw(string measurement) {
            var style = BoxStyle(22, TextAnchor.MiddleCenter);
            style.wordWrap = false;
            DrawPanel(BesideMouse(MeasurementSize(measurement, Scale), Scale), measurement, style);
        }

        private static void DrawPanel(Rect rect, string text, GUIStyle style) {
            var color = GUI.color;
            var contentColor = GUI.contentColor;
            GUI.color = new Color(.08f, .09f, .11f, .6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            GUI.Label(rect, text, style);
            GUI.contentColor = contentColor;
            GUI.color = color;
        }

        // Rects are returned in message order, bottom to top. Measure every row
        // before positioning so wrapping and simultaneous modifiers cannot overlap.
        public static Rect[] LayoutControls(IReadOnlyList<float> heights, float width, Vector2 screen,
            float scale, IReadOnlyList<Rect> occupied) {
            float gap = 8f * scale;
            float margin = Mathf.Min(8f, Mathf.Min(screen.x, screen.y) * .5f);
            float bottom = Mathf.Max(margin, screen.y - 20f * scale);
            float left = Mathf.Min(16f * scale, screen.x * .5f);
            width = Mathf.Clamp(width, 0, Mathf.Max(0, screen.x - left - margin));
            float total = gap * Mathf.Max(0, heights.Count - 1);
            foreach (float height in heights) total += height;
            // Raise the complete stack above any footer intersecting its width.
            // Revisit obstacles after raising the stack: its new position can
            // intersect a notification encountered earlier in the list.
            bool moved;
            do {
                moved = false;
                if (occupied != null) foreach (var rect in occupied)
                    if (rect.xMax > left && rect.xMin < left + width && rect.yMin < bottom && rect.yMax > bottom - total) {
                        bottom = Mathf.Max(margin, rect.yMin - gap);
                        moved = bottom > margin;
                    }
            } while (moved);
            var result = new Rect[heights.Count];
            // Keep boxes disjoint and on-screen even in a window too short to
            // fit all help; their text clips within its own box in that case.
            float available = Mathf.Max(0, bottom - margin);
            float fit = total > 0 ? Mathf.Min(1f, available / total) : 1f;
            float top = Mathf.Max(margin, bottom - total * fit);
            for (int i = heights.Count - 1; i >= 0; i--) {
                result[i] = new Rect(left, top, width, heights[i] * fit);
                top += (heights[i] + gap) * fit;
            }
            return result;
        }

        public static void DrawControls(string text) => DrawControls(new[] { text }, null);

        public static void DrawControls(IReadOnlyList<string> messages, IReadOnlyList<Rect> occupied) {
            float scale = Scale;
            var style = BoxStyle(18, TextAnchor.MiddleLeft);
            float maxWidth = Mathf.Max(1f, Mathf.Min(770f * scale, Screen.width - 32f * scale));
            float width = 1f;
            var heights = new float[messages.Count];
            var widths = new float[messages.Count];
            var wrap = new bool[messages.Count];
            for (int i = 0; i < messages.Count; i++) {
                var content = new GUIContent(messages[i]);
                // Leave a pixel-rounding allowance so a fitted label cannot
                // wrap its last letter onto an unintended clipped second line.
                float naturalWidth = Mathf.Ceil(style.CalcSize(content).x) + 2f * scale;
                widths[i] = Mathf.Min(maxWidth, naturalWidth);
                wrap[i] = naturalWidth > maxWidth;
                style.wordWrap = wrap[i];
                heights[i] = style.CalcHeight(content, widths[i]);
                width = Mathf.Max(width, widths[i]);
            }
            var rects = LayoutControls(heights, width, new Vector2(Screen.width, Screen.height), scale, occupied);
            for (int i = 0; i < messages.Count; i++) {
                rects[i].width = Mathf.Min(rects[i].width, widths[i]);
                style.wordWrap = wrap[i];
                DrawPanel(rects[i], messages[i], style);
            }
        }

        public static void DrawNumeric(string title, string text, string help) {
            float scale = Scale;
            var rect = BesideMouse(430f, 160f);
            GUI.BeginGroup(rect);
            DrawPanel(new Rect(0f, 0f, rect.width, rect.height), "", BoxStyle(18, TextAnchor.MiddleLeft));
            var label = BoxStyle(18, TextAnchor.MiddleLeft);
            GUI.Label(new Rect(8f * scale, 0f, rect.width - 16f * scale, 48f * scale), title, label);
            var field = new GUIStyle {
                font = FeedbackFont, normal = { textColor = Color.white },
                fontSize = Mathf.RoundToInt(23f * scale), alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(Mathf.RoundToInt(10f * scale), Mathf.RoundToInt(10f * scale), 0, 0)
            };
            // Text comes from Keyboard.onTextInput, including in player builds.
            DrawPanel(new Rect(16f * scale, 48f * scale, rect.width - 32f * scale, 44f * scale), text, field);
            GUI.Label(new Rect(8f * scale, 96f * scale, rect.width - 16f * scale, 64f * scale), help, label);
            GUI.EndGroup();
        }

        public static void DrawAxis(Camera camera, Vector3 origin, Vector3 axis) {
            var color = GUI.color;
            GUI.color = axis == Vector3.right ? new Color(1f, .25f, .25f) :
                axis == Vector3.up ? new Color(.3f, 1f, .3f) : new Color(.3f, .55f, 1f);
            // Clip the world line to the camera frustum before projecting. This
            // also handles perspective cameras looking almost along the axis.
            float min = -camera.farClipPlane * 2f, max = camera.farClipPlane * 2f;
            foreach (var plane in GeometryUtility.CalculateFrustumPlanes(camera)) {
                float distance = plane.GetDistanceToPoint(origin);
                float slope = Vector3.Dot(plane.normal, axis);
                if (Mathf.Abs(slope) < .000001f) {
                    if (distance < 0f) { GUI.color = color; return; }
                }
                else if (slope > 0f) min = Mathf.Max(min, -distance / slope);
                else max = Mathf.Min(max, -distance / slope);
            }
            if (min <= max) {
                var a = camera.WorldToScreenPoint(origin + axis * min);
                var b = camera.WorldToScreenPoint(origin + axis * max);
                var start = new Vector2(a.x, Screen.height - a.y);
                var end = new Vector2(b.x, Screen.height - b.y);
                var matrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(Vector2.SignedAngle(Vector2.right, end - start), start);
                GUI.DrawTexture(new Rect(start.x, start.y, Vector2.Distance(start, end), 2f), Texture2D.whiteTexture);
                GUI.matrix = matrix;
            }
            GUI.color = color;
        }
    }
}
