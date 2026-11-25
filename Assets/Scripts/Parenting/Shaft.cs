using System.Collections.Generic;
using Protobot.Simulation;
using UnityEngine;
using System.Linq;

namespace Protobot {
    public class Shaft : ConnectingPart {
        internal const float SeatTolerance = .002f;
        private const float MaxMotorStackEndplay = 1f / 16f;

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

        internal IEnumerable<(GameObject firstSupport, GameObject secondSupport, HoleCollider motorStop)> GetFrameRetentionCandidates() {
            if (!isActiveAndEnabled || holeDetector == null || !holeDetector.CanDetect) yield break;
            var holes = holeDetector.GetOrderedHoles();
            var clamps = holes.Where(holeDetector.IsTargetHole).OrderBy(Position).ToArray();
            if (clamps.Length == 0) yield break;
            var left = clamps[0];
            var right = clamps[^1];
            bool highStrength = IsHighStrength;
            float shaftHalfLength = HalfLength;

            if (left.holeData.part != right.holeData.part && FitsClamp(left, highStrength) && FitsClamp(right, highStrength)) {
                var leftWall = SeatedWall(left, -1);
                var rightWall = SeatedWall(right, 1);
                if (leftWall != null && rightWall != null) yield return (leftWall.holeData.part, rightWall.holeData.part, null);
            }

            // Match the tip position used by catalog shaft insertion.
            foreach (var socket in holes) {
                if (!TryGetSeatedMotor(socket, highStrength, out var motor, out float side)) continue;

                float socketPosition = side * Position(socket);
                foreach (var clamp in clamps) {
                    if (!FitsClamp(clamp, highStrength) || side * Position(clamp) - clamp.WorldHalfDepth < socketPosition - SeatTolerance) continue;
                    var wall = SeatedWall(clamp, side);
                    if (wall != null && FitsBore(wall.holeData, highStrength)) yield return (motor.gameObject, wall.holeData.part, clamp);
                }
            }

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

        // The motor and its far stop have already retained the shaft. A fixed face
        // and a collar on that shaft can then capture a stack with running clearance.
        internal IEnumerable<GameObject> GetMotorRetainedStack(GameObject motor, HoleCollider motorStop) {
            var socket = motor.GetComponent<Motor>().ShaftHole;
            float side = Mathf.Sign(Vector3.Dot(transform.forward, socket.transform.forward));
            float socketPosition = side * Position(socket);
            float stopFace = side * Position(motorStop) - motorStop.WorldHalfDepth;
            bool highStrength = IsHighStrength;
            var square = HoleShapes.instance.GetShapeMesh("square");
            float shaftWidth = highStrength ? .25f : .125f;
            var stack = new List<GameObject>();
            bool supported = false, hasDrivenHub = false;
            float face = 0, endplay = 0;

            var bores = holeDetector.GetOrderedHoles()
                .Where(hole => hole.holeType != HoleCollider.HoleType.Threaded && IsCoaxial(hole))
                .Select(hole => (hole, near: side * Position(hole) - hole.WorldHalfDepth, far: side * Position(hole) + hole.WorldHalfDepth))
                .Where(bore => bore.far > bore.near && bore.near >= socketPosition - SeatTolerance && bore.near <= stopFace + SeatTolerance)
                .OrderBy(bore => bore.near);

            foreach (var (hole, near, far) in bores) {
                var part = hole.holeData.part;
                bool fits = FitsBore(hole.holeData, highStrength);
                if (hole.holeType == HoleCollider.HoleType.Normal && fits && GroupManager.ShareFixedFrame(motor, part)) {
                    supported = true;
                    hasDrivenHub = false;
                    face = far;
                    endplay = 0;
                    stack.Clear();
                    continue;
                }
                if (!supported) continue;

                float gap = near - face;
                endplay += Mathf.Max(0, gap);
                if (gap < -SeatTolerance || endplay > MaxMotorStackEndplay) {
                    supported = false;
                    continue;
                }
                if (hole.holeType == HoleCollider.HoleType.Clamp) {
                    if (hasDrivenHub && FitsClamp(hole, highStrength)) {
                        foreach (var member in stack) yield return member;
                        yield return part;
                    }
                    supported = false;
                    continue;
                }

                var id = part.GetComponent<SavedObject>()?.nameId;
                bool spacer = id == "SPCR" || id == "WSHR";
                bool hub = IsKeyedHub(hole, part, id);
                if (!fits || (!spacer && !hub)) {
                    supported = false;
                    continue;
                }
                hasDrivenHub |= hub;
                stack.Add(part);
                face = far;
            }

            bool IsKeyedHub(HoleCollider hole, GameObject part, string id) {
                if (!part.TryGetComponent<SimulationPart>(out var driven) ||
                    ((driven.kind is DriveKind.Attachment or DriveKind.Rack) && id != "LKBR") ||
                    (driven.Center - hole.transform.position).sqrMagnitude > SeatTolerance * SeatTolerance) return false;
                var bore = hole.holeData;
                return (bore.shape == square && Mathf.Abs(bore.size.x - shaftWidth) <= SeatTolerance &&
                    Mathf.Abs(bore.size.y - shaftWidth) <= SeatTolerance) ||
                    (!highStrength && hole.TryGetComponent<HoleInsert>(out _));
            }
        }

        private bool FitsClamp(HoleCollider hole, bool highStrength) =>
            IsCoaxial(hole) && hole.holeData.IsHighStrength() == highStrength;

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
