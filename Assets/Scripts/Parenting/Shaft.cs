using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace Protobot {
    public class Shaft : ConnectingPart {
        private const float SeatTolerance = .002f;

        public float ShaftLength => transform.localScale.z;
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

        internal (GameObject left, GameObject right) GetSeatedWalls() {
            if (!isActiveAndEnabled || holeDetector == null || !holeDetector.CanDetect) return default;
            var holes = holeDetector.GetOrderedHoles();
            var clamps = holes.Where(holeDetector.IsTargetHole).OrderBy(Position).ToArray();
            if (clamps.Length < 2) return default;
            var left = clamps[0];
            var right = clamps[^1];
            bool highStrength = GetComponent<SavedObject>()?.id?.StartsWith("SHFT-High Strength-", System.StringComparison.Ordinal) == true;
            if (left.holeData.part == right.holeData.part || !Coaxial(left) || !Coaxial(right) ||
                left.holeData.IsHighStrength() != highStrength || right.holeData.IsHighStrength() != highStrength) return default;

            return (SeatedWall(left, -1), SeatedWall(right, 1));

            GameObject SeatedWall(HoleCollider clamp, int side) {
                float collarFace = side * Position(clamp) + HalfDepth(clamp);
                float shaftEnd = transform.TransformVector(Vector3.forward).magnitude * .5f;
                foreach (var hole in holes) {
                    if (hole.holeType != HoleCollider.HoleType.Normal || !Coaxial(hole)) continue;
                    float center = side * Position(hole);
                    float halfDepth = HalfDepth(hole);
                    if (center > collarFace && center + halfDepth <= shaftEnd + SeatTolerance &&
                        Mathf.Abs(center - halfDepth - collarFace) <= SeatTolerance) return hole.holeData.part;
                }
                return null;
            }
        }

        private float Position(HoleCollider hole) => Vector3.Dot(hole.transform.position - transform.position, transform.forward);

        private bool Coaxial(HoleCollider hole) =>
            Mathf.Abs(Vector3.Dot(hole.transform.forward, transform.forward)) > .9999f &&
            (hole.transform.position - transform.position - transform.forward * Position(hole)).sqrMagnitude <= SeatTolerance * SeatTolerance;

        // Use authored mating faces. Some hole colliders overhang those faces for picking.
        private static float HalfDepth(HoleCollider hole) => hole.holeData.depth * .5f *
            hole.transform.parent.TransformVector(hole.transform.localRotation * Vector3.forward).magnitude;
    }
}
