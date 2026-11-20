using System;
using System.Collections.Generic;
using UnityEngine;
using static Protobot.Simulation.ChainRouteGeometry;

namespace Protobot.Simulation {
    /// <summary>Open signed tangents and circular wraps from the drum to a fixed attachment.</summary>
    internal static class RopeRouteGeometry {
        internal const float Diameter = RopeGroove.KitRopeDiameter;
        private const float PlaneSlop = .035f;
        private const float ClearanceSlop = .0002f;
        private const float ArcStep = 4f;

        internal sealed class Contour {
            public Vector3[] points;
            public Vector3 normal, exit, drumCenter;
            public float length, drumWrapLength, drumRadius;
            public int drumExitIndex, drumSign;
            public double DrumCircumference => 2 * Math.PI * drumRadius;
            public double FullDrumTurns(float availableLength) =>
                Math.Max(0, Math.Floor((availableLength - length + .0001) / DrumCircumference));

            // Multiple wound turns share one effective radius. Draw one
            // complete turn while the partial wrap sits under the wound rope.
            public Vector3[] WoundPoints(float availableLength) {
                if (FullDrumTurns(availableLength) < 1) return points;
                int segments = Mathf.CeilToInt(360 / ArcStep);
                var wound = new Vector3[segments + points.Length - drumExitIndex];
                var radial = points[drumExitIndex] - drumCenter;
                for (int i = 0; i < segments; i++)
                    wound[i] = drumCenter + Quaternion.AngleAxis(drumSign * 360f * i / segments, normal) * radial;
                Array.Copy(points, drumExitIndex, wound, segments, points.Length - drumExitIndex);
                return wound;
            }
        }

        internal static float Radius(RopeGroove groove) => groove.SurfaceRadius * groove.transform.lossyScale.x + Diameter * .5f;
        internal static Vector3 DrumPoint(RopeGroove groove, Vector3 pickedPoint) {
            var radial = Vector3.ProjectOnPlane(pickedPoint - groove.Center, groove.Axis);
            return groove.Center + radial.normalized * Radius(groove);
        }

        internal static bool TryBuild(IReadOnlyList<RopeGroove> grooves, Vector3 start, Vector3 finish,
            IReadOnlyList<int> wraps, out Contour contour, out string reason) {
            contour = null;
            reason = "Choose both attachments and at least one pulley.";
            if (grooves.Count < 2 || grooves.Count != wraps.Count) return false;

            int count = grooves.Count;
            var origin = grooves[0].Center;
            var normal = grooves[0].Axis;
            var centers = new Vector3[count + 1];
            var radii = new float[count];
            reason = "Align the drum, pulley grooves, and end attachment in one plane.";
            if (!Finite(start) || !Finite(finish) || !Finite(origin) ||
                Mathf.Abs(Vector3.Dot(start - origin, normal)) > PlaneSlop ||
                Mathf.Abs(Vector3.Dot(finish - origin, normal)) > PlaneSlop) return false;
            centers[count] = Vector3.ProjectOnPlane(finish - origin, normal);
            for (int i = 0; i < count; i++) {
                var groove = grooves[i];
                if (groove == null || !Finite(groove.Center) || !Finite(groove.Axis) ||
                    Mathf.Abs(Vector3.Dot(groove.Axis, normal)) < .995f ||
                    Mathf.Abs(Vector3.Dot(groove.Center - origin, normal)) > PlaneSlop) return false;
                var scale = groove.transform.lossyScale;
                if (!Finite(scale) || scale.x <= 0 || Mathf.Abs(scale.x - scale.y) > .001f || Mathf.Abs(scale.x - scale.z) > .001f) {
                    reason = "Use uniform positive scales for the drum and pulleys.";
                    return false;
                }
                radii[i] = Radius(groove);
                if (!Finite(radii[i]) || radii[i] <= 0) return false;
                centers[i] = Vector3.ProjectOnPlane(groove.Center - origin, normal);
            }
            reason = "Keep the drum, pulleys, and end attachment clear of each other.";
            for (int i = 0; i < count; i++) {
                for (int j = 0; j < i; j++)
                    if (Vector3.Distance(centers[i], centers[j]) < radii[i] + radii[j] + Diameter - ClearanceSlop) return false;
                if (Vector3.Distance(centers[i], centers[count]) <= radii[i] + ClearanceSlop) return false;
            }

            var outgoing = new Vector3[count];
            var runEnds = new Vector3[count];
            var incoming = new Vector3[count];
            var sweeps = new float[count];
            incoming[0] = Vector3.ProjectOnPlane(start - origin, normal);
            reason = "Pick the start attachment on the drum groove.";
            if (Mathf.Abs(incoming[0].magnitude - radii[0]) > .002f) return false;

            for (int i = 0; i < count; i++) {
                int next = i + 1;
                int sign = wraps[i] == 0 ? 1 : -1;
                float nextRadius = next == count ? 0 : (wraps[next] == 0 ? 1 : -1) * radii[next];
                reason = "These wrap choices have no common tangent.";
                if (!TryTangent(centers[i], centers[next], sign * radii[i], nextRadius, normal, out outgoing[i], out runEnds[i])) return false;
                if (next < count) incoming[next] = runEnds[i];
                sweeps[i] = i == 0 && (incoming[i] - outgoing[i]).sqrMagnitude < 1e-10f ? 0 :
                    SignedSweep(incoming[i] - centers[i], outgoing[i] - centers[i], normal, sign);
                float angle = Mathf.Abs(sweeps[i]);
                reason = "A wrap needs clearance at both ends. Change its side or move an attachment.";
                // The drum represents wound turns on one effective radius.
                // A pulley still needs clearance between its entry and exit.
                if (!Finite(angle) || i > 0 && (angle < .05f ||
                    angle > 180 && Vector3.Distance(incoming[i], outgoing[i]) < Diameter - ClearanceSlop)) return false;
            }

            bool OnArc(int i, Vector3 radial) {
                float angle = Mathf.Abs(SignedSweep(incoming[i] - centers[i], radial, normal, sweeps[i] < 0 ? -1 : 1));
                return angle <= Mathf.Abs(sweeps[i]) + .0001f || angle > 359.9999f;
            }
            float PointArcDistance(int i, Vector3 point) {
                float distance = Mathf.Min(Vector3.Distance(point, incoming[i]), Vector3.Distance(point, outgoing[i]));
                var radial = point - centers[i];
                return OnArc(i, radial) ? Mathf.Min(distance, Mathf.Abs(radial.magnitude - radii[i])) : distance;
            }
            reason = "The rope crosses itself or runs too close to a groove or another wrap.";
            for (int i = 0; i < count; i++) {
                var a = outgoing[i];
                var b = runEnds[i];
                for (int j = 0; j < count; j++) {
                    if (DistanceToSegment(centers[j], a, b) < radii[j] - ClearanceSlop) return false;
                    if (j == i || j == i + 1) continue;
                    var run = b - a;
                    var foot = a + run * Mathf.Clamp01(Vector3.Dot(centers[j] - a, run) / run.sqrMagnitude);
                    float distance = Mathf.Min(PointArcDistance(j, a), PointArcDistance(j, b),
                        DistanceToSegment(incoming[j], a, b), DistanceToSegment(outgoing[j], a, b));
                    if (OnArc(j, foot - centers[j])) distance = Mathf.Min(distance, Mathf.Abs(Vector3.Distance(foot, centers[j]) - radii[j]));
                    if (distance < Diameter - ClearanceSlop) return false;
                }
                for (int j = 0; j < i; j++) {
                    if (Intersects(a, b, outgoing[j], runEnds[j], normal)) return false;
                    if (j == i - 1) continue;
                    if (Mathf.Min(DistanceToSegment(a, outgoing[j], runEnds[j]), DistanceToSegment(b, outgoing[j], runEnds[j]),
                        DistanceToSegment(outgoing[j], a, b), DistanceToSegment(runEnds[j], a, b)) < Diameter - ClearanceSlop) return false;
                }
            }

            var points = new List<Vector3>();
            float length = 0;
            int drumExitIndex = 0;
            for (int i = 0; i < count; i++) {
                var radial = incoming[i] - centers[i];
                int samples = Mathf.CeilToInt(Mathf.Abs(sweeps[i]) / ArcStep);
                for (int step = 0; step < samples; step++)
                    points.Add(origin + centers[i] + Quaternion.AngleAxis(sweeps[i] * step / samples, normal) * radial);
                if (i == 0) drumExitIndex = points.Count;
                points.Add(origin + outgoing[i]);
                var end = i == count - 1 ? finish : origin + runEnds[i];
                length += Mathf.Abs(sweeps[i]) * Mathf.Deg2Rad * radii[i] + Vector3.Distance(origin + outgoing[i], end);
            }
            points.Add(finish);
            reason = "The rope exceeds numerical geometry limits.";
            if (!Finite(length)) return false;
            for (int i = 1; i < points.Count; i++)
                if (!Finite(points[i]) || (points[i] - points[i - 1]).sqrMagnitude < 1e-12f) return false;
            contour = new Contour { points = points.ToArray(), normal = normal, length = length,
                exit = origin + outgoing[^1], drumWrapLength = Mathf.Abs(sweeps[0]) * Mathf.Deg2Rad * radii[0],
                drumCenter = origin, drumRadius = radii[0], drumExitIndex = drumExitIndex, drumSign = wraps[0] == 0 ? 1 : -1 };
            reason = null;
            return true;
        }
    }
}
