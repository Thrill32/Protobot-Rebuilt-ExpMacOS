using UnityEngine;
using Protobot.InputEvents;

namespace Protobot.Tools {
    /// <summary>
    /// Shared snapping configuration for transform gizmos and part placement.
    /// Project geometry is authored in inches, and VEX structural holes use a
    /// 0.5 inch center-to-center pitch.
    /// </summary>
    public static class TransformSnapping {
        public const float MovementIncrement = 0.5f;
        public const float RotationIncrement = 15f;

        public static bool Enabled { get; set; } = true;
        public static bool Active => Enabled && !SnapBypassInput.IsHeld;

        public static Vector3 SnapPosition(Vector3 position, Vector3 origin) {
            return origin + (position - origin).Round(MovementIncrement);
        }

        public static float SnapAngle(float angle) {
            return Mathf.Round(angle / RotationIncrement) * RotationIncrement;
        }

        /// <summary>One signed turn, measured in the direction from the starting pose.</summary>
        public static float WrapAngle(float angle) {
            float wrapped = angle % 360f;
            // Pointer deltas accumulate a little float error over full turns.
            return Mathf.Abs(wrapped) < .001f || 360f - Mathf.Abs(wrapped) < .001f ? 0f : wrapped;
        }
    }
}
