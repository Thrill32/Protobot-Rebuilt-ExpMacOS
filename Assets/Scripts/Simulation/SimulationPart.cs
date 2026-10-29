using System;
using System.Linq;
using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>Authored mechanical coordinates, independent of selection/grouping and mesh pivots.</summary>
    public sealed class SimulationPart : MonoBehaviour {
        public Vector3 localCenter;
        public Vector3 localAxis = Vector3.forward;
        public Vector3 localTooth = Vector3.up;
        public int teeth;
        public float pitchRadius;
        public float halfWidth;

        public Vector3 Center => transform.TransformPoint(localCenter);
        public Vector3 Axis => transform.TransformDirection(localAxis).normalized;
        public Vector3 Tooth => transform.TransformDirection(localTooth).normalized;
        public float Radius => transform.TransformVector(localTooth * pitchRadius).magnitude;
        public float HalfWidth => transform.TransformVector(localAxis * halfWidth).magnitude;

        // Used by setup to bake metadata into every gear variant, including regenerated/loaded parts.
        public void Configure(PartData data) {
            var hole = data.primaryHole;
            localCenter = transform.InverseTransformPoint(hole.transform.position);
            localAxis = transform.InverseTransformDirection(hole.transform.forward).normalized;
            teeth = int.Parse(new string(data.param2Value.Where(char.IsDigit).ToArray()));
            float outer = 0;
            halfWidth = 0;
            var filter = GetComponent<MeshFilter>();
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
        }
    }
}
