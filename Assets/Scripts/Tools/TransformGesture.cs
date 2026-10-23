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
        public float RotationDegrees { get; private set; }
        public Vector3 DefaultRotationAxis { get; private set; }

        public void Begin(Camera camera, Vector3 origin, Vector2 mouse) {
            this.camera = camera;
            this.origin = origin;
            startMouse = mouse;
            previousVector = mouse - (Vector2)camera.WorldToScreenPoint(origin);
            useLinearRotation = previousVector.sqrMagnitude < 144f;
            angle = linearAngleBase = RotationDegrees = 0f;
            resumedOffset = Vector3.zero;
            DefaultRotationAxis = PrimaryAxis(camera.transform.forward);
        }

        public void Resume(Vector2 mouse, Vector3 offset, float degrees, Vector3 axis) {
            startMouse = mouse;
            previousVector = mouse - (Vector2)camera.WorldToScreenPoint(origin);
            useLinearRotation = previousVector.sqrMagnitude < 144f;
            resumedOffset = offset;
            float direction = Vector3.Dot(camera.transform.forward, axis) < 0f ? -1f : 1f;
            angle = linearAngleBase = degrees * direction;
            RotationDegrees = degrees;
        }

        public Vector3 Position(Vector2 mouse, Vector3 axis, bool snap) {
            var normal = camera.transform.forward;
            if (axis != Vector3.zero) {
                normal = Vector3.ProjectOnPlane(normal, axis);
                // Looking straight down the axis has no unique ray/axis intersection.
                if (normal.sqrMagnitude < 0.001f) {
                    float depth = Mathf.Max(camera.nearClipPlane, camera.WorldToScreenPoint(origin).z);
                    float unitsPerPixel = Vector3.Distance(
                        camera.ScreenToWorldPoint(new Vector3(0f, 0f, depth)),
                        camera.ScreenToWorldPoint(new Vector3(0f, 1f, depth)));
                    float distance = (mouse.y - startMouse.y) * unitsPerPixel;
                    if (snap)
                        distance = Mathf.Round(distance / TransformSnapping.MovementIncrement) * TransformSnapping.MovementIncrement;
                    var endOnPosition = origin + Vector3.Project(resumedOffset, axis) + axis * distance;
                    return snap ? TransformSnapping.SnapPosition(endOnPosition, origin) : endOnPosition;
                }
            }

            var plane = new Plane(normal.normalized, origin);
            var startRay = camera.ScreenPointToRay(startMouse);
            var ray = camera.ScreenPointToRay(mouse);
            if (!plane.Raycast(startRay, out float startDistance) || !plane.Raycast(ray, out float distanceAlongRay))
                return origin;

            var delta = resumedOffset + ray.GetPoint(distanceAlongRay) - startRay.GetPoint(startDistance);
            if (axis != Vector3.zero)
                delta = Vector3.Project(delta, axis);

            var position = origin + delta;
            return snap ? TransformSnapping.SnapPosition(position, origin) : position;
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
            RotationDegrees = rotationAngle * direction;
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
