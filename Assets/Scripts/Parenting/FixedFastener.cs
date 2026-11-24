using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Protobot {
    public sealed class FixedFastener : ConnectingPart {
        [SerializeField] private HoleCollider.HoleType targetType;
        [SerializeField] private float diameter;
        [SerializeField] private float engagementDepth;

        // Local zero is the mounting face. The socket or rivet cap faces +Z.
        private const float SeatTolerance = .002f;
        private const float FitTolerance = .004f;

        private bool Fits(HoleCollider hole) {
            if (hole == null || hole.holeType != targetType || hole.holeData?.part == null) return false;
            var size = hole.holeData.size;
            if (targetType == HoleCollider.HoleType.Threaded) {
                return hole.holeData.part.GetComponent<SavedObject>()?.id == "CLMP-Normal-Normal" &&
                    Mathf.Abs(size.x - diameter) <= FitTolerance && Mathf.Abs(size.y - diameter) <= FitTolerance;
            }
            // The split sleeve compresses in the .164-inch bearing bore and
            // also fits the nominal .182-inch square structural hole.
            return Mathf.Min(size.x, size.y) >= diameter - FitTolerance &&
                Mathf.Max(size.x, size.y) <= .182f + FitTolerance;
        }

        public bool TryGetSeat(HoleFace face, out Pose seat) {
            seat = default;
            var hole = face.hole?.source?.GetComponent<HoleCollider>();
            if (!Fits(hole) || hole.holeData.depth > engagementDepth + SeatTolerance) return false;
            float alignment = Vector3.Dot(face.direction, hole.transform.forward);
            if (Mathf.Abs(alignment) < .9999f || targetType == HoleCollider.HoleType.Threaded && alignment > 0) return false;
            seat = MoveTargeting.HoleFace(hole.holeData, face.direction);
            return true;
        }

        private float EntryDepth(HoleData hole) =>
            Vector3.Dot(transform.position - hole.position, transform.forward) - hole.depth * .5f;

        internal override bool AcceptsHole(HoleCollider hole) {
            if (!Fits(hole)) return false;
            var offset = hole.transform.position - transform.position;
            float entry = EntryDepth(hole.holeData);
            return Mathf.Abs(Vector3.Dot(transform.forward, hole.transform.forward)) > .9999f &&
                Vector3.ProjectOnPlane(offset, transform.forward).sqrMagnitude <= SeatTolerance * SeatTolerance &&
                entry >= -SeatTolerance && entry + hole.holeData.depth <= engagementDepth + SeatTolerance &&
                (targetType != HoleCollider.HoleType.Threaded ||
                    entry <= SeatTolerance && Vector3.Dot(transform.forward, hole.transform.forward) < 0);
        }

        public override HoleDetector CreateHoleDetector() {
            var detector = HoleDetector.Create(transform, engagementDepth + 2 * SeatTolerance, targetType);
            detector.transform.position -= transform.forward * (engagementDepth * .5f);
            return detector;
        }

        public override bool CanFormConnection => holeDetector.GetOrderedHoles()
            .Any(hole => EntryDepth(hole.holeData) <= SeatTolerance);

        public override List<GameObject> GetAttachedParts() {
            var attached = new List<GameObject>();
            float end = 0;
            foreach (var hole in holeDetector.GetOrderedHoles()) {
                float entry = EntryDepth(hole.holeData);
                if (entry > end + SeatTolerance) break;
                if (!attached.Contains(hole.holeData.part)) attached.Add(hole.holeData.part);
                end = Mathf.Max(end, entry + hole.holeData.depth);
            }
            return attached;
        }
    }
}
