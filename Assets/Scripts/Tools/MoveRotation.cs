using UnityEngine;
using UnityEngine.InputSystem;

namespace Protobot.Tools {
    /// <summary>Rotation of a placement or rigid move about its free pivot or mating axis.</summary>
    public sealed class MoveRotation {
        private readonly TransformGesture gesture = new();
        private Pose start;
        private Vector3 pivot;
        private bool paused;
        private float degrees;
        private float initialTwist;
        private Quaternion worldOrientation;
        private Pose? exactPose;
        public TransformNumericInput NumericInput { get; } = new();
        public bool Active { get; private set; }
        public Pose? Attachment { get; private set; }
        public Vector3 Axis { get; private set; }
        public Vector3 Pivot => pivot;
        public float Degrees => degrees;

        public void Begin(Camera camera, Pose pose, Pose? attachment, Vector2 mouse,
            float initialTwist = 0, Quaternion? worldOrientation = null) {
            NumericInput.Close();
            this.initialTwist = initialTwist;
            this.worldOrientation = worldOrientation ?? pose.rotation;
            degrees = 0;
            exactPose = null;
            start = pose;
            Attachment = attachment;
            pivot = attachment?.position ?? pose.position;
            Axis = attachment.HasValue ? attachment.Value.rotation * Vector3.forward : TransformGesture.PrimaryAxis(camera.transform.forward);
            gesture.Begin(camera, pivot, mouse, radialRotationOnly: true);
            Active = true;
            paused = false;
        }

        public Pose Evaluate(Vector2 mouse, bool snap) {
            if (exactPose.HasValue) return exactPose.Value;
            if (paused) {
                Pause(mouse);
                paused = false;
            }
            var delta = gesture.Rotation(mouse, Axis, Quaternion.identity, snap);
            degrees = gesture.RotationDegrees;
            return Around(start, pivot, delta);
        }

        public void Pause(Vector2 mouse) {
            gesture.Resume(mouse, Vector3.zero, gesture.UnwrappedRotationDegrees, Axis);
            paused = true;
        }

        public void End() {
            Active = false;
            NumericInput.Close();
        }

        public static bool SlashPressed(Keyboard keyboard) => keyboard != null &&
            (keyboard.slashKey.wasPressedThisFrame || keyboard.numpadDivideKey.wasPressedThisFrame);

        public bool HandleNumeric(Keyboard keyboard, Vector2 mouse, out Pose? confirmed) {
            confirmed = null;
            if (SlashPressed(keyboard)) {
                NumericInput.Cycle(false);
                if (!NumericInput.IsOpen) Pause(mouse);
                return true;
            }
            if (!NumericInput.IsOpen) return false;
            NumericInput.Edit(keyboard);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) {
                if (TrySubmitNumeric(out var pose)) confirmed = pose;
                else Pause(mouse);
            }
            return true;
        }

        public bool TrySubmitNumeric(out Pose pose) {
            bool global = NumericInput.Mode == TransformNumericInput.InputMode.Global;
            bool valid = TransformNumericInput.TryEvaluate(NumericInput.Text, global && !Attachment.HasValue,
                true, Axis, pivot, worldOrientation, out _, out var delta, out float value);
            NumericInput.Close();
            pose = default;
            if (!valid) return false;
            // Absolute attachment angles are twist in the mating frame. Setting
            // a world Euler component here would tilt the hole out of alignment.
            if (global && Attachment.HasValue) {
                value = TransformSnapping.WrapAngle(value - initialTwist);
                delta = Quaternion.AngleAxis(value, Axis);
            }
            degrees = value;
            pose = Around(start, pivot, delta);
            exactPose = pose;
            return true;
        }

        public string ConstraintLabel => Attachment.HasValue ? "attachment" : Axis == Vector3.right ? "X" :
            Axis == Vector3.up ? "Y" : "Z";

        public void DrawFeedback() {
            if (!NumericInput.IsOpen) TransformFeedback.Draw(TransformFeedback.Angle(Degrees));
            else {
                bool global = NumericInput.Mode == TransformNumericInput.InputMode.Global;
                string next = global ? "/: mouse control" : "/: global value";
                TransformFeedback.DrawNumeric($"{(global ? "Global" : "Relative")} {ConstraintLabel} rotation (degrees)",
                    NumericInput.DisplayText, $"Enter: apply exact value\n{next}    Invalid input: mouse control");
            }
        }

        public static Pose Around(Pose pose, Vector3 pivot, Quaternion rotation) => new(
            pivot + rotation * (pose.position - pivot), rotation * pose.rotation);

        public static Pose Twist(Pose pose, Pose attachment, float degrees) => Around(pose,
            attachment.position, Quaternion.AngleAxis(degrees, attachment.rotation * Vector3.forward));

        public static Pose Interpolate(Pose from, Pose to, Vector3 pivot, float progress) {
            var rotation = Quaternion.Slerp(from.rotation, to.rotation, progress);
            var localOffset = Vector3.Lerp(Quaternion.Inverse(from.rotation) * (from.position - pivot),
                Quaternion.Inverse(to.rotation) * (to.position - pivot), progress);
            return new Pose(pivot + rotation * localOffset, rotation);
        }
    }
}
