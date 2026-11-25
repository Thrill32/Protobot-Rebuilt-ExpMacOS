using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace Protobot {
    public class Shaft : ConnectingPart {
        internal const float SeatTolerance = .002f;

        public float ShaftLength => transform.localScale.z;
        private float HalfLength => transform.TransformVector(Vector3.forward).magnitude * .5f;
        internal bool IsHighStrength => GetComponent<SavedObject>()?.id?.StartsWith("SHFT-High Strength-", System.StringComparison.Ordinal) == true;
        public override bool CanFormConnection => holeDetector.GetOrderedHoles()
            .Where(holeDetector.IsTargetHole).Select(hole => hole.holeData.part).Distinct().Count() >= 2;

        public override HoleDetector CreateHoleDetector() {
            return HoleDetector.Create(transform, ShaftLength, HoleCollider.HoleType.Clamp);
        }

        public override List<GameObject> GetAttachedParts() {
            if (CanFormConnection) {
                var holes = holeDetector.GetOrderedHoles();
                var clamps = holes.Where(holeDetector.IsTargetHole).Select(Position).ToArray();
                float min = clamps.Min(), max = clamps.Max();
                return holes.Where(hole => Position(hole) >= min - .001f && Position(hole) <= max + .001f)
                    .Select(hole => hole.holeData.part).Distinct().ToList();
            }

            return null;
        }

        internal IEnumerable<(GameObject firstSupport, GameObject secondSupport, GameObject collar)> GetFrameRetentionCandidates() {
            if (!isActiveAndEnabled || holeDetector == null || !holeDetector.CanDetect) yield break;
            var holes = holeDetector.GetOrderedHoles();
            var clamps = holes.Where(holeDetector.IsTargetHole).OrderBy(Position).ToArray();
            if (clamps.Length == 0) yield break;
            var left = clamps[0];
            var right = clamps[^1];
            bool highStrength = IsHighStrength;
            float shaftHalfLength = HalfLength;

            if (left.holeData.part != right.holeData.part && FitsClamp(left) && FitsClamp(right)) {
                var leftWall = SeatedWall(left, -1);
                var rightWall = SeatedWall(right, 1);
                if (leftWall != null && rightWall != null) yield return (leftWall.holeData.part, rightWall.holeData.part, null);
            }

            // Match the tip position used by catalog shaft insertion.
            foreach (var socket in holes) {
                if (!TryGetSeatedMotor(socket, highStrength, out var motor, out float side)) continue;

                float socketPosition = side * Position(socket);
                foreach (var clamp in clamps) {
                    if (!FitsClamp(clamp) || side * Position(clamp) - clamp.WorldHalfDepth < socketPosition - SeatTolerance) continue;
                    var wall = SeatedWall(clamp, side);
                    if (wall != null && FitsBore(wall.holeData, highStrength)) yield return (motor.gameObject, wall.holeData.part, clamp.holeData.part);
                }
            }

            bool FitsClamp(HoleCollider hole) => IsCoaxial(hole) && hole.holeData.IsHighStrength() == highStrength;

            HoleCollider SeatedWall(HoleCollider clamp, float side) {
                float collarFace = side * Position(clamp) + clamp.WorldHalfDepth;
                foreach (var hole in holes) {
                    if (hole.holeType != HoleCollider.HoleType.Normal || !IsCoaxial(hole)) continue;
                    float center = side * Position(hole);
                    float halfDepth = hole.WorldHalfDepth;
                    if (halfDepth > 0 && center > collarFace && center + halfDepth <= shaftHalfLength + SeatTolerance &&
                        Mathf.Abs(center - halfDepth - collarFace) <= SeatTolerance) return hole;
                }
                return null;
            }
        }

        internal bool TryGetSeatedMotor(out Motor motor) {
            motor = null;
            if (!isActiveAndEnabled || holeDetector == null || !holeDetector.CanDetect) return false;
            bool highStrength = IsHighStrength;
            foreach (var socket in holeDetector.GetOrderedHoles()) {
                if (!TryGetSeatedMotor(socket, highStrength, out var seated, out _)) continue;
                if (motor != null) {
                    motor = null;
                    return false;
                }
                motor = seated;
            }
            return motor != null;
        }

        private bool TryGetSeatedMotor(HoleCollider socket, bool highStrength, out Motor motor, out float side) {
            motor = null;
            side = 0;
            if (socket.holeType != HoleCollider.HoleType.Normal || !socket.holeData.part.TryGetComponent(out motor) ||
                !motor.isActiveAndEnabled || motor.ShaftHole != socket || !FitsBore(socket.holeData, highStrength) || !IsCoaxial(socket)) return false;
            side = Mathf.Sign(Vector3.Dot(transform.forward, socket.transform.forward));
            var shaftTip = transform.position - transform.forward * (side * HalfLength);
            return (motor.ShaftSeat(highStrength) - shaftTip).sqrMagnitude <= SeatTolerance * SeatTolerance;
        }

        private float Position(HoleCollider hole) => Vector3.Dot(hole.transform.position - transform.position, transform.forward);

        internal static bool FitsBore(HoleData hole, bool highStrength) => Mathf.Min(hole.size.x, hole.size.y) + SeatTolerance >=
            (highStrength ? .25f : .125f);

        internal bool IsCoaxial(HoleCollider hole) => IsCoaxial(transform.position, transform.forward, hole.transform);

        internal static bool IsCoaxial(Vector3 origin, Vector3 axis, Transform bore) =>
            Mathf.Abs(Vector3.Dot(bore.forward, axis)) > .9999f &&
            Vector3.ProjectOnPlane(bore.position - origin, axis).sqrMagnitude <= SeatTolerance * SeatTolerance;
    }
}
