using UnityEngine;
using UnityEngine.InputSystem;

namespace Protobot.Tools {
    /// <summary>Mouse transforms measured from the start of a modal operation.</summary>
    public sealed class TransformGesture {
        private Camera camera;
        private Vector3 origin;
        private Vector2 startMouse;
        private Vector2 previousVector;
        private bool useLinearRotation;
        private float angle;
        private float linearAngleBase;
        private Vector3 resumedOffset;
        private Vector3 wrappedAxisOffset;
        private Vector3 wrappedFreeOffset;
        public float RotationDegrees { get; private set; }
        public float UnwrappedRotationDegrees { get; private set; }
        public Vector3 DefaultRotationAxis { get; private set; }
        public Vector3 Origin => origin;

        public void Begin(Camera camera, Vector3 origin, Vector2 mouse) {
            this.camera = camera;
            this.origin = origin;
            startMouse = mouse;
            previousVector = mouse - (Vector2)camera.WorldToScreenPoint(origin);
            useLinearRotation = previousVector.sqrMagnitude < 144f;
            angle = linearAngleBase = RotationDegrees = UnwrappedRotationDegrees = 0f;
            resumedOffset = Vector3.zero;
            wrappedAxisOffset = wrappedFreeOffset = Vector3.zero;
            DefaultRotationAxis = PrimaryAxis(camera.transform.forward);
        }

        public void Resume(Vector2 mouse, Vector3 offset, float degrees, Vector3 axis) {
            startMouse = mouse;
            previousVector = mouse - (Vector2)camera.WorldToScreenPoint(origin);
            useLinearRotation = previousVector.sqrMagnitude < 144f;
            resumedOffset = offset;
            wrappedAxisOffset = wrappedFreeOffset = Vector3.zero;
            float direction = Vector3.Dot(camera.transform.forward, axis) < 0f ? -1f : 1f;
            angle = linearAngleBase = degrees * direction;
            UnwrappedRotationDegrees = degrees;
            RotationDegrees = TransformSnapping.WrapAngle(degrees);
        }

        public void RebasePointer(Vector2 before, Vector2 after) {
            // Carry each world-axis projection independently, so switching axes
            // still measures from the original grab anchor after any wraps.
            var correction = new Vector3(
                Position(before, Vector3.right, false).x - Position(after, Vector3.right, false).x,
                Position(before, Vector3.up, false).y - Position(after, Vector3.up, false).y,
                Position(before, Vector3.forward, false).z - Position(after, Vector3.forward, false).z);
            var freeCorrection = Position(before, Vector3.zero, false) - Position(after, Vector3.zero, false);
            wrappedAxisOffset += correction;
            wrappedFreeOffset += freeCorrection;
        }

        public Vector3 Position(Vector2 mouse, Vector3 axis, bool snap) {
            var normal = camera.transform.forward;
            if (axis != Vector3.zero) {
                normal = Vector3.ProjectOnPlane(normal, axis);
                // Near an end-on axis, its targeting plane can cross the viewport
                // horizon. Use a stable screen projection for the whole drag so
                // wrapping never lands in a dead zone or sends the target to infinity.
                if (UseLinearAxisProjection(normal)) {
                    float depth = Mathf.Max(camera.nearClipPlane, camera.WorldToScreenPoint(origin).z);
                    float unitsPerPixel = Vector3.Distance(
                        camera.ScreenToWorldPoint(new Vector3(0f, 0f, depth)),
                        camera.ScreenToWorldPoint(new Vector3(0f, 1f, depth)));
                    var screenAxis = new Vector2(Vector3.Dot(camera.transform.right, axis), Vector3.Dot(camera.transform.up, axis));
                    if (screenAxis.sqrMagnitude < .001f) screenAxis = Vector2.up;
                    float distance = Vector2.Dot(mouse - startMouse, screenAxis) / screenAxis.sqrMagnitude * unitsPerPixel;
                    var endOnPosition = origin + Vector3.Project(resumedOffset + wrappedAxisOffset, axis) + axis * distance;
                    return snap ? TransformSnapping.SnapPosition(endOnPosition, origin) : endOnPosition;
                }
            }

            var plane = new Plane(normal.normalized, origin);
            var startRay = camera.ScreenPointToRay(startMouse);
            var ray = camera.ScreenPointToRay(mouse);
            if (!plane.Raycast(startRay, out float startDistance) || !plane.Raycast(ray, out float distanceAlongRay)) {
                var fallback = origin + (axis == Vector3.zero ? resumedOffset + wrappedFreeOffset : Vector3.Project(resumedOffset + wrappedAxisOffset, axis));
                return snap ? TransformSnapping.SnapPosition(fallback, origin) : fallback;
            }

            var delta = resumedOffset + ray.GetPoint(distanceAlongRay) - startRay.GetPoint(startDistance);
            if (axis != Vector3.zero)
                delta = Vector3.Project(delta + wrappedAxisOffset, axis);
            else
                delta += wrappedFreeOffset;

            var position = origin + delta;
            return snap ? TransformSnapping.SnapPosition(position, origin) : position;
        }

        private bool UseLinearAxisProjection(Vector3 normal) {
            if (normal.sqrMagnitude < .001f) return true;
            if (camera.orthographic) return false;
            var rect = camera.pixelRect;
            return Vector3.Dot(camera.ScreenPointToRay(new Vector2(rect.xMin, rect.yMin)).direction, normal) <= .0001f ||
                Vector3.Dot(camera.ScreenPointToRay(new Vector2(rect.xMin, rect.yMax)).direction, normal) <= .0001f ||
                Vector3.Dot(camera.ScreenPointToRay(new Vector2(rect.xMax, rect.yMin)).direction, normal) <= .0001f ||
                Vector3.Dot(camera.ScreenPointToRay(new Vector2(rect.xMax, rect.yMax)).direction, normal) <= .0001f;
        }

        public Quaternion Rotation(Vector2 mouse, Vector3 axis, Quaternion initialRotation, bool snap) {
            if (useLinearRotation) {
                var delta = mouse - startMouse;
                angle = linearAngleBase + (delta.x + delta.y) * 0.5f;
            }
            else {
                var vector = mouse - (Vector2)camera.WorldToScreenPoint(origin);
                if (vector.sqrMagnitude > 1f) {
                    angle += Vector2.SignedAngle(previousVector, vector);
                    previousVector = vector;
                }
            }

            if (axis == Vector3.zero)
                axis = DefaultRotationAxis;
            float direction = Vector3.Dot(camera.transform.forward, axis) < 0f ? -1f : 1f;
            float rotationAngle = snap ? TransformSnapping.SnapAngle(angle) : angle;
            UnwrappedRotationDegrees = rotationAngle * direction;
            RotationDegrees = TransformSnapping.WrapAngle(UnwrappedRotationDegrees);
            return Quaternion.AngleAxis(RotationDegrees, axis) * initialRotation;
        }

        public static Vector3 PrimaryAxis(Vector3 direction) {
            var magnitude = new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
            if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z)
                return Vector3.right;
            return magnitude.y >= magnitude.z ? Vector3.up : Vector3.forward;
        }

        public static bool TryReadAxis(out Vector3 axis) {
            var keyboard = Keyboard.current;
            axis = Vector3.zero;
            if (keyboard == null || Protobot.InputEvents.RebindAction.Rebinding)
                return false;
            if (keyboard.xKey.wasPressedThisFrame) axis = Vector3.right;
            else if (keyboard.yKey.wasPressedThisFrame) axis = Vector3.up;
            else if (keyboard.zKey.wasPressedThisFrame) axis = Vector3.forward;
            return axis != Vector3.zero;
        }
    }
}
