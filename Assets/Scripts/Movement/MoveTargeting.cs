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

        public static MoveTarget ForHole(HoleData hole, Vector3 normal, bool opposite,
            Func<Transform, bool> ignore = null, Func<Transform, bool> stackMember = null) {
            var last = hole;
            var direction = -normal.normalized;
            // Follow the bore, not the camera ray: an oblique view can miss the
            // later holes even though their mating faces touch along one axis.
            if (opposite && Mathf.Abs(Vector3.Dot(direction, hole.forward)) > .9999f &&
                (stackMember == null || hole.source != null && stackMember(hole.source))) {
                const float tolerance = .002f;
                float end = hole.depth * .5f;
                var ray = new Ray(hole.position - direction * (end + .01f), direction);
                foreach (var hit in Physics.RaycastAll(ray, Mathf.Infinity, HoleCollider.HOLE_COLLISIONS_MASK,
                             QueryTriggerInteraction.Ignore).OrderBy(hit => hit.distance)) {
                    var transform = hit.collider.transform;
                    if (Excluded(transform) || ignore != null && ignore(transform) ||
                        stackMember != null && !stackMember(transform) ||
                        !hit.collider.TryGetComponent<HoleCollider>(out var collider) || !collider.isActiveAndEnabled || collider.holeData?.part == null) continue;
                    var candidate = collider.holeData;
                    if (candidate == hole || Mathf.Abs(Vector3.Dot(direction, candidate.forward)) < .9999f) continue;
                    var offset = candidate.position - hole.position;
                    float center = Vector3.Dot(offset, direction);
                    if ((offset - direction * center).sqrMagnitude > tolerance * tolerance) continue;
                    float far = center + candidate.depth * .5f;
                    if (far <= end + .0001f) continue;
                    // A free gap ends this stack; do not jump to unrelated parts
                    // that happen to have a hole on the same infinite line.
                    if (center - candidate.depth * .5f > end + tolerance) break;
                    last = candidate;
                    end = far;
                }
            }
            return new MoveTarget(HoleFace(last, opposite ? direction : normal), last);
        }

        public static bool TryRaycast(Ray ray, int mask, bool opposite, bool allowScrews,
            Func<Transform, bool> ignore, out MoveTarget target, Func<Transform, bool> stackMember = null) {
            bool Ignored(Transform transform) => Excluded(transform) || (ignore != null && ignore(transform));
            foreach (var hit in Physics.RaycastAll(ray, Mathf.Infinity, mask, QueryTriggerInteraction.Ignore).OrderBy(hit => hit.distance)) {
                if (Ignored(hit.collider.transform)) continue;
                if (hit.collider.TryGetComponent<HoleCollider>(out var collider) && collider.holeData?.part != null) {
                    target = ForHole(collider.holeData, hit.normal, opposite, Ignored, stackMember);
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
