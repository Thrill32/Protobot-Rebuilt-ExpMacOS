using System;
using System.Linq;
using UnityEngine;

namespace Protobot {
    public readonly struct MoveTarget {
        public readonly Pose face;
        public readonly HoleData hole;
        public readonly Screw screw;
        public GameObject Body => hole != null ? hole.part : screw.gameObject;

        public MoveTarget(Pose face, HoleData hole = null, Screw screw = null) {
            this.face = face;
            this.hole = hole;
            this.screw = screw;
        }
    }

    /// <summary>Face geometry, visible target picking and rigid mating shared by Move, G/H and placement.</summary>
    public static class MoveTargeting {
        public static Quaternion Oppose(Quaternion face) => face * Quaternion.Euler(0, 180, 0);

        public static Pose HoleFace(HoleData hole, Vector3 normal) => new(
            hole.position + normal * (hole.depth / 2f), Quaternion.LookRotation(normal, hole.rotation * Vector3.up));

        public static Pose Align(Pose sourceFace, Pose targetFace, Pose pivot) {
            var rotation = Oppose(targetFace.rotation) * Quaternion.Inverse(sourceFace.rotation);
            return new Pose(targetFace.position + rotation * (pivot.position - sourceFace.position), rotation * pivot.rotation);
        }

        public static MoveTarget ForScrew(Screw screw, Func<Transform, bool> ignore = null) => new(
            new Pose(screw.GetLowestPoint(ignore), screw.transform.GetAlignmentRotation(true)), screw: screw);

        public static bool TryRaycast(Ray ray, int mask, bool opposite, bool allowScrews,
            Func<Transform, bool> ignore, out MoveTarget target) {
            bool Ignored(Transform transform) => Excluded(transform) || (ignore != null && ignore(transform));
            foreach (var hit in Physics.RaycastAll(ray, Mathf.Infinity, mask, QueryTriggerInteraction.Ignore).OrderBy(hit => hit.distance)) {
                if (Ignored(hit.collider.transform)) continue;
                if (hit.collider.TryGetComponent<HoleCollider>(out var collider) && collider.holeData?.part != null) {
                    target = new MoveTarget(HoleFace(collider.holeData, opposite ? -hit.normal : hit.normal), collider.holeData);
                    return true;
                }
                var screw = allowScrews ? hit.collider.GetComponentInParent<Screw>() : null;
                if (screw != null) {
                    // Exclude moving geometry from the endpoint query too: a snapped source cannot push its own target away.
                    target = ForScrew(screw, Ignored);
                    return true;
                }
                // The first visible ordinary body blocks targets behind it.
                break;
            }
            target = default;
            return false;
        }

        private static bool Excluded(Transform transform) {
            for (var current = transform; current != null; current = current.parent)
                if (current.gameObject.layer == Placement.PLACEMENT_LAYER || current.gameObject.IsDeleted()) return true;
            return false;
        }
    }
}
