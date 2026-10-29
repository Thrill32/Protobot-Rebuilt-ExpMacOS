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
            public readonly DriveKind kind;
            public readonly Vector3 contact, face;
            public readonly float span;
            public readonly int starts;
            public Source(SimulationPart part, bool local = false) {
                center = local ? part.localCenter : part.Center; axis = local ? part.localAxis : part.Axis;
                tooth = local ? part.localTooth : part.Tooth; radius = local ? part.pitchRadius : part.Radius; teeth = part.teeth;
                kind = part.kind; contact = local ? part.kind == DriveKind.Spur ? part.localCenter : part.localContactCenter : part.ContactCenter;
                face = local ? part.localFace : part.Face; span = part.span; starts = part.starts;
            }
            public Source(Vector3 center, Vector3 axis, Vector3 tooth, float radius, int teeth) {
                this.center = center; this.axis = axis; this.tooth = tooth; this.radius = radius; this.teeth = teeth;
                kind = DriveKind.Spur; contact = center; face = -axis; span = 0; starts = 1;
            }
        }

        public static bool AreMeshed(SimulationPart a, SimulationPart b) {
            return TryTransmission(a, b, out _);
        }

        public static bool TryTransmission(SimulationPart a, SimulationPart b, out double factor) {
            factor = 0;
            if (a.kind == DriveKind.Spur && b.kind == DriveKind.Spur) {
            var offset = b.Center - a.Center;
            bool meshed = Mathf.Abs(Vector3.Dot(a.Axis, b.Axis)) > .995f &&
                Mathf.Abs(Vector3.Dot(offset, a.Axis)) < Mathf.Max(.025f, Mathf.Min(a.HalfWidth, b.HalfWidth)) &&
                Mathf.Abs(Vector3.ProjectOnPlane(offset, a.Axis).magnitude - a.Radius - b.Radius) < MeshTolerance(a.Radius + b.Radius);
                if (meshed) factor = -(double)a.teeth / b.teeth * Math.Sign(Vector3.Dot(a.Axis, b.Axis));
                return meshed;
            }
            if (a.kind == DriveKind.Bevel && b.kind == DriveKind.Bevel) {
                if (Mathf.Abs(Vector3.Dot(a.Axis, b.Axis)) > .03f ||
                    Vector3.Distance(a.ContactCenter + a.Face * b.Radius, b.ContactCenter + b.Face * a.Radius) > .045f) return false;
                var contact = a.ContactCenter - b.Face * a.Radius;
                float sign = Vector3.Dot(Vector3.Cross(a.Axis, contact - a.Center), Vector3.Cross(b.Axis, contact - b.Center));
                factor = Math.Sign(sign) * (double)a.teeth / b.teeth; return sign != 0;
            }
            if ((a.kind == DriveKind.Worm && b.kind == DriveKind.WormWheel) || (b.kind == DriveKind.Worm && a.kind == DriveKind.WormWheel)) {
                var worm = a.kind == DriveKind.Worm ? a : b; var wheel = worm == a ? b : a;
                if (Mathf.Abs(Vector3.Dot(worm.Axis, wheel.Axis)) > .03f) return false;
                var offset = worm.ContactCenter - wheel.ContactCenter;
                var radial = offset - worm.Axis * Vector3.Dot(offset, worm.Axis);
                if (Mathf.Abs(Vector3.Dot(offset, wheel.Axis)) > .035f || Mathf.Abs(Vector3.Dot(offset, worm.Axis)) > worm.HalfWidth + .025f ||
                    Mathf.Abs(radial.magnitude - worm.Radius - wheel.Radius) > MeshTolerance(worm.Radius + wheel.Radius)) return false;
                double rate = Math.Sign(Vector3.Dot(worm.Axis, Vector3.Cross(wheel.Axis, radial))) * (double)worm.starts / wheel.teeth;
                factor = worm == a ? rate : 1 / rate; return rate != 0;
            }
            if ((a.kind == DriveKind.Rack && b.kind == DriveKind.Spur) || (b.kind == DriveKind.Rack && a.kind == DriveKind.Spur)) {
                var rack = a.kind == DriveKind.Rack ? a : b; var gear = rack == a ? b : a;
                var offset = gear.Center - rack.ContactCenter;
                if (Mathf.Abs(Vector3.Dot(gear.Axis, rack.Axis)) < .995f || Mathf.Abs(Vector3.Dot(offset, rack.Axis)) > .035f ||
                    Mathf.Abs(Vector3.Dot(offset, rack.Face) - gear.Radius) > MeshTolerance(gear.Radius) ||
                    Mathf.Abs(Vector3.Dot(offset, rack.Travel)) > rack.span + gear.Radius) return false;
                double rate = Vector3.Dot(Vector3.Cross(gear.Axis, -rack.Face * gear.Radius), rack.Travel) * Mathf.Deg2Rad;
                factor = gear == a ? rate : 1 / rate; return rate != 0;
            }
            return false;
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
                if (Excluded(gear.transform, ignore) || !Compatible(source.kind, gear.kind)) continue;
                if (!new Plane(gear.Axis, gear.ContactCenter).Raycast(ray, out float distance)) continue;
                var offset = ray.GetPoint(distance) - gear.ContactCenter;
                float ringDistance = offset.magnitude;
                float vicinity = Mathf.Max(.15f, gear.Radius * .15f);
                bool rack = gear.kind == DriveKind.Rack;
                if (rack ? Mathf.Abs(Vector3.Dot(offset, gear.Travel)) > gear.span || Mathf.Abs(Vector3.Dot(offset, gear.Face)) > .2f :
                    ringDistance < gear.Radius * .68f || ringDistance > gear.Radius + vicinity) continue;
                // Never reach through a foreground part to snap to a hidden gear.
                if (visible.collider != null && visible.distance < distance - .05f &&
                    visible.collider.GetComponentInParent<SimulationPart>() != gear) continue;
                if (source.kind != DriveKind.Spur || gear.kind != DriveKind.Spur) {
                    float specialScore = rack ? Mathf.Abs(Vector3.Dot(offset, gear.Face)) : Mathf.Abs(ringDistance - gear.Radius);
                    if (specialScore >= best || !SpecialPose(source, gear, offset, pivot, bypass, ignore, out var special, out var specialAnchor)) continue;
                    candidate = gear; pose = special; anchor = specialAnchor; best = specialScore; continue;
                }
                var proposed = gear.Center + offset.normalized * (source.radius + gear.Radius);
                if (!bypass && !TryPatternCenter(gear, source.radius + gear.Radius, proposed, ignore, out proposed)) continue;
                float score = Mathf.Abs(ringDistance - gear.Radius);
                if (score >= best) continue;
                candidate = gear; center = proposed; best = score;
                pose = AlignAtCenter(source, candidate, center, pivot);
                anchor = new Pose(center, Quaternion.LookRotation(candidate.Axis, candidate.Tooth));
            }
            if (candidate == null) return false;
            return true;
        }

        private static bool Compatible(DriveKind a, DriveKind b) => (a == DriveKind.Spur && b == DriveKind.Spur) ||
            (a == DriveKind.Bevel && b == DriveKind.Bevel) || (a == DriveKind.Worm && b == DriveKind.WormWheel) ||
            (a == DriveKind.WormWheel && b == DriveKind.Worm) || (a == DriveKind.Rack && b == DriveKind.Spur) || (b == DriveKind.Rack && a == DriveKind.Spur);

        private static bool SpecialPose(Source source, SimulationPart target, Vector3 offset, Pose pivot, bool bypass,
            Func<Transform, bool> ignore, out Pose pose, out Pose anchor) {
            pose = anchor = default;
            Vector3 radial = offset.normalized;
            if (!bypass && target.kind != DriveKind.Rack) {
                // Right-angle gears have intrinsic mounting offsets. Quantize the
                // approach around their shaft while retaining their contact geometry.
                float reference = Mathf.Max(.5f, Mathf.Round((source.radius + target.Radius) / .5f) * .5f);
                if (!TryPatternCenter(target, reference, target.Center + radial * reference, ignore, out var point)) return false;
                radial = (point - target.Center).normalized;
            }
            Vector3 contact, axis;
            Quaternion rotation;
            if (source.kind == DriveKind.Bevel) {
                var face = -radial;
                axis = face * Math.Sign(Vector3.Dot(source.face, source.axis));
                contact = target.ContactCenter + radial * target.Radius + target.Face * source.radius;
                rotation = Quaternion.FromToRotation(source.axis, axis);
                float phase = Vector3.SignedAngle(target.Tooth, radial, target.Axis) * target.teeth / 360;
                rotation = Phase(source, rotation, axis, -target.Face, .5f - phase);
            }
            else if (source.kind == DriveKind.Worm) {
                axis = Vector3.Cross(target.Axis, radial).normalized;
                contact = target.ContactCenter + radial * (source.radius + target.Radius);
                rotation = Quaternion.FromToRotation(source.axis, axis);
                float phase = Vector3.SignedAngle(target.Tooth, radial, target.Axis) * target.teeth / 360;
                rotation = Phase(source, rotation, axis, -radial, phase);
            }
            else if (target.kind == DriveKind.Worm) {
                axis = Vector3.Cross(target.Axis, radial).normalized;
                contact = target.ContactCenter + radial * (source.radius + target.Radius);
                rotation = Quaternion.FromToRotation(source.axis, axis);
                float phase = Vector3.SignedAngle(target.Tooth, radial, target.Axis) / 360;
                rotation = Phase(source, rotation, axis, -radial, .5f - phase);
            }
            else if (source.kind == DriveKind.Rack) {
                axis = target.Axis;
                contact = target.Center + radial * target.Radius;
                rotation = Quaternion.LookRotation(axis, -radial) * Quaternion.Inverse(Quaternion.LookRotation(source.axis, source.face));
            }
            else {
                axis = target.Axis;
                float along = Vector3.Dot(offset, target.Travel);
                if (!bypass) along = Mathf.Round(along / TransformSnapping.MovementIncrement) * TransformSnapping.MovementIncrement;
                along = Mathf.Clamp(along, -target.span, target.span);
                contact = target.ContactCenter + target.Travel * along + target.Face * source.radius;
                rotation = Quaternion.FromToRotation(source.axis, axis);
                rotation = Phase(source, rotation, axis, -target.Face, .5f - along / (Mathf.PI / 24));
            }
            var center = contact - rotation * (source.contact - source.center);
            pose = new Pose(center + rotation * (pivot.position - source.center), rotation * pivot.rotation);
            anchor = new Pose(contact, Quaternion.LookRotation(axis, rotation * source.tooth));
            return true;
        }

        private static Quaternion Phase(Source source, Quaternion rotation, Vector3 axis, Vector3 toward, float phase) {
            float pitch = 360f / Mathf.Max(1, source.teeth);
            float adjustment = Vector3.SignedAngle(rotation * source.tooth, toward, axis) - phase * pitch;
            return Quaternion.AngleAxis(Mathf.Repeat(adjustment + pitch / 2, pitch) - pitch / 2, axis) * rotation;
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
