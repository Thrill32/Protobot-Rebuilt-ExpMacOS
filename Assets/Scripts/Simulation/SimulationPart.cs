using System;
using System.Linq;
using UnityEngine;

namespace Protobot.Simulation {
    public enum DriveKind { Spur, Bevel, Worm, WormWheel, Sprocket, Rack, Attachment }
    /// <summary>Authored mechanical coordinates, independent of selection/grouping and mesh pivots.</summary>
    public sealed class SimulationPart : MonoBehaviour {
        public DriveKind kind;
        public Vector3 localCenter;
        public Vector3 localAxis = Vector3.forward;
        public Vector3 localTooth = Vector3.up;
        public int teeth;
        public float pitchRadius;
        public float halfWidth;
        public Vector3 localContactCenter;
        public Vector3 localFace = Vector3.back;
        public float span;
        public float chainPitch;
        public int starts = 1;

        public Vector3 Center => transform.TransformPoint(localCenter);
        public Vector3 Axis => transform.TransformDirection(localAxis).normalized;
        public Vector3 Tooth => transform.TransformDirection(localTooth).normalized;
        public float Radius => transform.TransformVector(localTooth * pitchRadius).magnitude;
        public float HalfWidth => transform.TransformVector(localAxis * halfWidth).magnitude;
        public Vector3 ContactCenter => kind == DriveKind.Spur ? Center : transform.TransformPoint(localContactCenter);
        public Vector3 Face => transform.TransformDirection(localFace).normalized;
        public Vector3 Travel => Tooth;
        public bool IsLinear => kind == DriveKind.Rack;
        public bool CanMesh => kind != DriveKind.Attachment && kind != DriveKind.Sprocket;

        // Used by setup to bake metadata into every gear variant, including regenerated/loaded parts.
        public void Configure(PartData data, DriveKind driveKind = DriveKind.Spur) {
            kind = driveKind;
            var hole = data.primaryHole;
            var filter = GetComponent<MeshFilter>();
            if (hole == null || !hole.transform.IsChildOf(data.transform))
                hole = data.GetComponentsInChildren<HoleCollider>(true).OrderBy(h => h.holeType == HoleCollider.HoleType.Clamp ? 0 : 1)
                    .ThenBy(h => (data.transform.InverseTransformPoint(h.transform.position) - filter.sharedMesh.bounds.center).sqrMagnitude).FirstOrDefault();
            localCenter = hole != null ? transform.InverseTransformPoint(hole.transform.position) : filter.sharedMesh.bounds.center;
            localAxis = hole != null ? transform.InverseTransformDirection(hole.transform.forward).normalized : Vector3.forward;
            localContactCenter = localCenter;
            string digits = new string(data.param2Value.Where(char.IsDigit).ToArray());
            teeth = int.TryParse(digits, out var count) ? count : kind == DriveKind.WormWheel ? 24 : 1;
            float outer = 0;
            halfWidth = 0;
            foreach (var vertex in filter.sharedMesh.vertices) {
                var offset = transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)) - localCenter;
                float axial = Vector3.Dot(offset, localAxis);
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(axial));
                var radial = offset - localAxis * axial;
                if (radial.magnitude > outer) {
                    outer = radial.magnitude;
                    localTooth = radial.normalized;
                }
            }
            // Tooth tips are flat: the farthest vertex is usually a corner, not
            // the tooth's center. Recover the repeated tip's angular center so
            // different tooth counts/mesh variants phase into actual gaps.
            var reference = localTooth;
            float cosine = 0, sine = 0;
            foreach (var vertex in filter.sharedMesh.vertices) {
                var offset = transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)) - localCenter;
                var radial = Vector3.ProjectOnPlane(offset, localAxis);
                if (radial.magnitude < outer * .985f) continue;
                float angle = Vector3.SignedAngle(reference, radial, localAxis) * Mathf.Deg2Rad * teeth;
                cosine += Mathf.Cos(angle); sine += Mathf.Sin(angle);
            }
            if (cosine * cosine + sine * sine > .0001f)
                localTooth = Quaternion.AngleAxis(Mathf.Atan2(sine, cosine) * Mathf.Rad2Deg / teeth, localAxis) * reference;
            // Standard spur gear: outside diameter = module * (teeth + 2).
            pitchRadius = outer * teeth / (teeth + 2f);
            if (kind == DriveKind.Bevel) {
                pitchRadius = teeth / 48f;
                localFace = -localAxis;
                var tips = filter.sharedMesh.vertices.Where(v => Vector3.ProjectOnPlane(v - localCenter, localAxis).magnitude > outer * .98f).ToArray();
                if (tips.Length > 0) localContactCenter = localCenter + localAxis * tips.Average(v => Vector3.Dot(v - localCenter, localAxis));
            }
            if (kind == DriveKind.Worm) {
                teeth = starts = 1; pitchRadius = .25f;
                localContactCenter = localCenter + localAxis * Vector3.Dot(filter.sharedMesh.bounds.center - localCenter, localAxis);
            }
            if (kind == DriveKind.WormWheel) { teeth = 24; pitchRadius = .5f; }
            if (kind == DriveKind.Sprocket) {
                chainPitch = data.param1Value == "High Strength" ? .3937f : .1475f;
                pitchRadius = chainPitch / (2 * Mathf.Sin(Mathf.PI / teeth));
            }
            if (kind == DriveKind.Rack) {
                localCenter = filter.sharedMesh.bounds.center;
                localAxis = Vector3.forward; localTooth = Vector3.right; localFace = Vector3.up;
                // The authored rack's upper face carries the teeth; subtract one addendum.
                localContactCenter = new Vector3(localCenter.x, filter.sharedMesh.bounds.max.y - 1f / 24, localCenter.z);
                span = filter.sharedMesh.bounds.extents.x; pitchRadius = 0; teeth = 19;
            }
            if (kind == DriveKind.Attachment) { teeth = 0; pitchRadius = 0; }
        }
    }
}
