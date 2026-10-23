using UnityEngine;

namespace Protobot.Tools {
    /// <summary>Shared mouse-adjacent measurements for handle and keyboard transforms.</summary>
    public static class TransformFeedback {
        public static string Distance(Vector3 offset, Vector3 axis) =>
            $"{(axis == Vector3.zero ? offset.magnitude : Vector3.Dot(offset, axis)):0.###} in";

        public static void Draw(string measurement) {
            var mouse = MouseInput.Position;
            const float width = 150f, height = 30f;
            float x = Mathf.Clamp(mouse.x + 20f, 0f, Mathf.Max(0f, Screen.width - width));
            float y = Mathf.Clamp(Screen.height - mouse.y - 42f, 0f, Mathf.Max(0f, Screen.height - height));
            GUI.Box(new Rect(x, y, width, height), measurement);
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
