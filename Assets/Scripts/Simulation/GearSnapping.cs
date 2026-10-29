using System;
using System.Linq;
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
                Mathf.Abs(Vector3.ProjectOnPlane(offset, a.Axis).magnitude - a.Radius - b.Radius) <
                    Mathf.Max(.035f, (a.Radius + b.Radius) * .015f);
        }

        public static Pose Align(Source source, SimulationPart target, Vector3 radial, Pose pivot) {
            radial = Vector3.ProjectOnPlane(radial, target.Axis).normalized;
            var align = Quaternion.FromToRotation(source.axis, target.Axis);
            float targetPhase = Vector3.SignedAngle(target.Tooth, radial, target.Axis) * target.teeth / 360f;
            float desired = (.5f - targetPhase) * 360f / source.teeth;
            float current = Vector3.SignedAngle(align * source.tooth, -radial, target.Axis);
            float pitch = 360f / source.teeth;
            float adjustment = Mathf.Repeat(current - desired + pitch / 2, pitch) - pitch / 2;
            var rotation = Quaternion.AngleAxis(adjustment, target.Axis) * align;
            var center = target.Center + radial * (source.radius + target.Radius);
            return new Pose(center + rotation * (pivot.position - source.center), rotation * pivot.rotation);
        }

        public static bool TrySnap(Ray ray, Source source, Pose pivot, Func<Transform, bool> ignore,
            out Pose pose, out Pose anchor) {
            pose = anchor = default;
            var visible = Physics.RaycastAll(ray, Mathf.Infinity, LayerMask.GetMask("Default", "HoleCollisions"), QueryTriggerInteraction.Ignore)
                .OrderBy(hit => hit.distance).FirstOrDefault(hit => !Excluded(hit.collider.transform, ignore));
            SimulationPart candidate = null;
            Vector3 radial = default;
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
                float score = Mathf.Abs(ringDistance - gear.Radius);
                if (score >= best) continue;
                candidate = gear; radial = offset; best = score;
            }
            if (candidate == null) return false;
            pose = Align(source, candidate, radial, pivot);
            var center = candidate.Center + radial.normalized * (source.radius + candidate.Radius);
            anchor = new Pose(center, Quaternion.LookRotation(candidate.Axis, candidate.Tooth));
            return true;
        }

        private static bool Excluded(Transform transform, Func<Transform, bool> ignore) {
            if (ignore != null && ignore(transform)) return true;
            for (var current = transform; current != null; current = current.parent)
                if (current.gameObject.layer == Placement.PLACEMENT_LAYER || current.gameObject.IsDeleted()) return true;
            return false;
        }
    }
}
