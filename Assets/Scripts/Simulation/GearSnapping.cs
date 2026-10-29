using System;
using System.Linq;
using Protobot.Tools;
using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>Pitch-circle placement and tooth phasing shared by placement, Move and G/H.</summary>
    public static class GearSnapping {
        public readonly struct Source {
            public readonly Vector3 center, axis, tooth;
            public readonly float radius;
            public readonly int teeth;
            public Source(SimulationPart part) : this(part.Center, part.Axis, part.Tooth, part.Radius, part.teeth) { }
            public Source(Vector3 center, Vector3 axis, Vector3 tooth, float radius, int teeth) {
                this.center = center; this.axis = axis; this.tooth = tooth; this.radius = radius; this.teeth = teeth;
            }
        }

        public static bool AreMeshed(SimulationPart a, SimulationPart b) {
            var offset = b.Center - a.Center;
            return Mathf.Abs(Vector3.Dot(a.Axis, b.Axis)) > .995f &&
                Mathf.Abs(Vector3.Dot(offset, a.Axis)) < Mathf.Max(.025f, Mathf.Min(a.HalfWidth, b.HalfWidth)) &&
                Mathf.Abs(Vector3.ProjectOnPlane(offset, a.Axis).magnitude - a.Radius - b.Radius) < MeshTolerance(a.Radius + b.Radius);
        }

        public static Pose Align(Source source, SimulationPart target, Vector3 radial, Pose pivot) {
            radial = Vector3.ProjectOnPlane(radial, target.Axis).normalized;
            return AlignAtCenter(source, target, target.Center + radial * (source.radius + target.Radius), pivot);
        }

        private static Pose AlignAtCenter(Source source, SimulationPart target, Vector3 center, Pose pivot) {
            var radial = (center - target.Center).normalized;
            var align = Quaternion.FromToRotation(source.axis, target.Axis);
            float targetPhase = Vector3.SignedAngle(target.Tooth, radial, target.Axis) * target.teeth / 360f;
            float desired = (.5f - targetPhase) * 360f / source.teeth;
            float current = Vector3.SignedAngle(align * source.tooth, -radial, target.Axis);
            float pitch = 360f / source.teeth;
            float adjustment = Mathf.Repeat(current - desired + pitch / 2, pitch) - pitch / 2;
            var rotation = Quaternion.AngleAxis(adjustment, target.Axis) * align;
            return new Pose(center + rotation * (pivot.position - source.center), rotation * pivot.rotation);
        }

        public static bool TrySnap(Ray ray, Source source, Pose pivot, Func<Transform, bool> ignore,
            out Pose pose, out Pose anchor, bool bypass = false) {
            pose = anchor = default;
            var visible = Physics.RaycastAll(ray, Mathf.Infinity, LayerMask.GetMask("Default", "HoleCollisions"), QueryTriggerInteraction.Ignore)
                .OrderBy(hit => hit.distance).FirstOrDefault(hit => !Excluded(hit.collider.transform, ignore));
            SimulationPart candidate = null;
            Vector3 center = default;
            float best = float.PositiveInfinity;
            foreach (var gear in UnityEngine.Object.FindObjectsByType<SimulationPart>(FindObjectsSortMode.None)) {
                if (Excluded(gear.transform, ignore) || gear.teeth <= 0) continue;
                if (!new Plane(gear.Axis, gear.Center).Raycast(ray, out float distance)) continue;
                var offset = ray.GetPoint(distance) - gear.Center;
                float ringDistance = offset.magnitude;
                float vicinity = Mathf.Max(.15f, gear.Radius * .15f);
                if (ringDistance < gear.Radius * .68f || ringDistance > gear.Radius + vicinity) continue;
                // Never reach through a foreground part to snap to a hidden gear.
                if (visible.collider != null && visible.distance < distance - .05f &&
                    visible.collider.GetComponentInParent<SimulationPart>() != gear) continue;
                var proposed = gear.Center + offset.normalized * (source.radius + gear.Radius);
                if (!bypass && !TryPatternCenter(gear, source.radius + gear.Radius, proposed, ignore, out proposed)) continue;
                float score = Mathf.Abs(ringDistance - gear.Radius);
                if (score >= best) continue;
                candidate = gear; center = proposed; best = score;
            }
            if (candidate == null) return false;
            pose = AlignAtCenter(source, candidate, center, pivot);
            anchor = new Pose(center, Quaternion.LookRotation(candidate.Axis, candidate.Tooth));
            return true;
        }

        private static float MeshTolerance(float centerDistance) => Mathf.Max(.035f, centerDistance * .015f);

        private static bool TryPatternCenter(SimulationPart gear, float centerDistance, Vector3 desired,
            Func<Transform, bool> ignore, out Vector3 center) {
            PatternAxes(gear, ignore, out var x, out var y);
            float pitch = TransformSnapping.MovementIncrement;
            float tolerance = MeshTolerance(centerDistance);
            int extent = Mathf.CeilToInt((centerDistance + tolerance) / pitch);
            float best = float.PositiveInfinity;
            center = default;
            // Search the virtual half-inch lattice around the stationary shaft.
            // Keep the exact lattice position instead of projecting it back onto
            // the pitch circle; no actual hole is needed at the chosen position.
            for (int column = -extent; column <= extent; column++) {
                for (int row = -extent; row <= extent; row++) {
                    if (column == 0 && row == 0) continue;
                    var offset = (x * column + y * row) * pitch;
                    if (Mathf.Abs(offset.magnitude - centerDistance) >= tolerance) continue;
                    var candidate = gear.Center + offset;
                    float score = (candidate - desired).sqrMagnitude;
                    if (score >= best) continue;
                    center = candidate; best = score;
                }
            }
            return !float.IsPositiveInfinity(best);
        }

        private static void PatternAxes(SimulationPart gear, Func<Transform, bool> ignore, out Vector3 x, out Vector3 y) {
            // A mounting frame follows rotated builds but must not turn with the
            // gear's teeth. Without one, use a stable projection of world axes.
            var axis = gear.Axis;
            x = Vector3.ProjectOnPlane(Mathf.Abs(Vector3.Dot(axis, Vector3.right)) < .9f ? Vector3.right : Vector3.up, axis).normalized;
            float nearest = float.PositiveInfinity;
            foreach (var hole in UnityEngine.Object.FindObjectsByType<HoleCollider>(FindObjectsSortMode.None)) {
                if (!hole.isActiveAndEnabled || Excluded(hole.transform, ignore)) continue;
                var owner = hole.holeData?.part;
                var saved = owner != null ? owner.GetComponent<SavedObject>() : null;
                if (saved == null || string.IsNullOrEmpty(saved.id)) continue;
                var id = saved.nameId;
                var type = PartsManager.partTypes?.FirstOrDefault(t => t != null && t.id == id);
                if (type?.group != PartType.PartGroup.Structure && owner.GetComponent<Motor>() == null &&
                    id != "BRNG" && id != "HSBG" && id != "BLCK" && id != "HSBP") continue;
                if (Mathf.Abs(Vector3.Dot(hole.transform.forward, axis)) < .995f) continue;
                var offset = hole.transform.position - gear.Center;
                if (Vector3.ProjectOnPlane(offset, axis).magnitude > .025f) continue;
                float distance = Mathf.Abs(Vector3.Dot(offset, axis));
                if (distance >= nearest) continue;
                x = Vector3.ProjectOnPlane(hole.transform.right, axis).normalized;
                nearest = distance;
            }
            y = Vector3.Cross(axis, x).normalized;
        }

        private static bool Excluded(Transform transform, Func<Transform, bool> ignore) {
            if (ignore != null && ignore(transform)) return true;
            for (var current = transform; current != null; current = current.parent)
                if (current.gameObject.layer == Placement.PLACEMENT_LAYER || current.gameObject.IsDeleted()) return true;
            return false;
        }
    }
}
