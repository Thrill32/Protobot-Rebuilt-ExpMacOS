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

        private static GUIStyle BoxStyle(int fontSize, TextAnchor alignment) {
            float scale = Scale;
            return new GUIStyle(GUI.skin.box) {
                fontSize = Mathf.RoundToInt(fontSize * scale), alignment = alignment,
                wordWrap = true, richText = false, clipping = TextClipping.Clip,
                padding = new RectOffset(Mathf.RoundToInt(14f * scale), Mathf.RoundToInt(14f * scale),
                    Mathf.RoundToInt(10f * scale), Mathf.RoundToInt(10f * scale))
            };
        }

        private static Rect BesideMouse(float width, float height) {
            float scale = Scale;
            var mouse = MouseInput.Position;
            width *= scale;
            height *= scale;
            return KeepOnScreen(new Rect(mouse.x + 22f * scale, Screen.height - mouse.y - height - 18f * scale,
                width, height), new Vector2(Screen.width, Screen.height));
        }

        public static void Draw(string measurement) {
            GUI.Box(BesideMouse(220f, 48f), measurement, BoxStyle(22, TextAnchor.MiddleCenter));
        }

        public static void DrawControls(string text) {
            float scale = Scale;
            var style = BoxStyle(18, TextAnchor.MiddleLeft);
            float width = Mathf.Max(1f, Mathf.Min(770f * scale, Screen.width - 32f * scale));
            float height = style.CalcHeight(new GUIContent(text), width);
            GUI.Box(KeepOnScreen(new Rect(16f * scale, Screen.height - height - 20f * scale, width, height),
                new Vector2(Screen.width, Screen.height)), text, style);
        }

        public static void DrawNumeric(string title, string text, string help) {
            float scale = Scale;
            var rect = BesideMouse(430f, 160f);
            GUI.BeginGroup(rect);
            GUI.Box(new Rect(0f, 0f, rect.width, rect.height), GUIContent.none);
            var label = BoxStyle(18, TextAnchor.MiddleLeft);
            GUI.Label(new Rect(8f * scale, 0f, rect.width - 16f * scale, 48f * scale), title, label);
            var field = new GUIStyle(GUI.skin.textField) {
                fontSize = Mathf.RoundToInt(23f * scale), alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(Mathf.RoundToInt(10f * scale), Mathf.RoundToInt(10f * scale), 0, 0)
            };
            // Text comes from Keyboard.onTextInput, including in player builds.
            GUI.Box(new Rect(16f * scale, 48f * scale, rect.width - 32f * scale, 44f * scale), text, field);
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
