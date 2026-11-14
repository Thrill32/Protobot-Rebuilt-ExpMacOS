using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>One planar signed-circle construction shared by preview, rendering and transmission validation.</summary>
    public static class ChainRouteGeometry {
        public const float Width = .045f;
        private const float ArcStep = 8;
        public sealed class Contour {
            public SimulationPart[] parts;
            public int[] wraps;
            public Vector3 normal;
            public Vector3[] points;
            public float RotationSign(int index) => (wraps[index] == 1 ? -1 : 1) * Mathf.Sign(Vector3.Dot(normal, parts[index].Axis));
        }
        public static bool CompatiblePlane(IReadOnlyList<SimulationPart> parts, out string reason) {
            reason = "Select two or more distinct sprockets";
            if (parts == null || parts.Count < 2 || parts.Count > Protobot.Collaboration.DocumentModel.MaxWriteSet ||
                parts.Any(p => p == null || p.kind != DriveKind.Sprocket) || parts.Distinct().Count() != parts.Count) return false;
            var first = parts[0];
            foreach (var part in parts) {
                reason = "Sprockets must use the same chain size and lie in one plane (flipped faces are allowed)";
                if (!Finite(part.ContactCenter) || !Finite(part.Axis) || !Finite(part.Radius) || !Finite(part.chainPitch) ||
                    part.Radius <= 0 || part.chainPitch <= 0 || part.teeth < 1 ||
                    Mathf.Abs(Vector3.Dot(first.Axis, part.Axis)) < .995f ||
                    Mathf.Abs(Vector3.Dot(part.ContactCenter - first.ContactCenter, first.Axis)) > .035f ||
                    Mathf.Abs(part.chainPitch - first.chainPitch) > .005f) return false;
                reason = "Use a uniform positive sprocket scale";
                var scale = part.transform.lossyScale;
                if (!Finite(scale) || Mathf.Min(scale.x, scale.y, scale.z) <= 0 ||
                    Mathf.Max(scale.x, scale.y, scale.z) - Mathf.Min(scale.x, scale.y, scale.z) > .001f) return false;
            }
            reason = null; return true;
        }
        public static Vector3 PlaneNormal(IReadOnlyList<SimulationPart> parts) {
            var normal = parts.OrderBy(p => p.GetComponent<SavedObject>().ConnectionId, StringComparer.Ordinal).First().Axis;
            // Axis reversal must not change the automatic hull or its texture direction.
            float dominant = Mathf.Abs(normal.x) >= Mathf.Abs(normal.y) && Mathf.Abs(normal.x) >= Mathf.Abs(normal.z) ? normal.x :
                Mathf.Abs(normal.y) >= Mathf.Abs(normal.z) ? normal.y : normal.z;
            return dominant < 0 ? -normal : normal;
        }
        public static List<SimulationPart> OuterOrder(IReadOnlyList<SimulationPart> parts) {
            var n = PlaneNormal(parts);
            Basis(n, out var x, out var y);
            var origin = parts[0].ContactCenter;
            Vector2 Point(SimulationPart p) => new(Vector3.Dot(p.ContactCenter - origin, x), Vector3.Dot(p.ContactCenter - origin, y));
            float Cross(SimulationPart a, SimulationPart b, SimulationPart c) => Cross2(Point(b) - Point(a), Point(c) - Point(a));
            var sorted = parts.OrderBy(p => Point(p).x).ThenBy(p => Point(p).y)
                .ThenBy(p => p.GetComponent<SavedObject>().ConnectionId, StringComparer.Ordinal).ToArray();
            if (parts.Count == 2) return sorted.ToList();
            var hull = new List<SimulationPart>();
            foreach (var p in sorted) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= .00001f) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            int lower = hull.Count;
            for (int i = sorted.Length - 2; i >= 0; i--) { while (hull.Count > lower && Cross(hull[^2], hull[^1], sorted[i]) <= .00001f) hull.RemoveAt(hull.Count - 1); hull.Add(sorted[i]); }
            hull.RemoveAt(hull.Count - 1); return hull;
        }
        public static bool TryBuild(IReadOnlyList<SimulationPart> members, int version, int[] metadata, out Contour contour, out string reason) {
            contour = null;
            if (!CompatiblePlane(members, out reason)) return false;
            reason = "Invalid chain route metadata";
            if (!Protobot.Collaboration.DocumentModel.ValidRoute(members.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray(), version, metadata)) return false;
            var parts = version == 0 ? OuterOrder(members).ToArray() : members.ToArray();
            reason = "Quick chains need a clear outer loop; use Shift + Chain to edit an inner/idler route";
            if (parts.Length != members.Count) return false;
            var wraps = version == 0 ? new int[parts.Length] : metadata.ToArray();
            int count = parts.Length;
            var n = PlaneNormal(parts);
            var origin = parts[0].ContactCenter;
            var centers = parts.Select(p => Vector3.ProjectOnPlane(p.ContactCenter - origin, n)).ToArray();
            float area = 0;
            for (int i = 0; i < count; i++) area += Vector3.Dot(Vector3.Cross(centers[i], centers[(i + 1) % count]), n);
            reason = "The chain route exceeds numerical geometry limits";
            if (!Finite(area) || centers.Any(c => !Finite(c))) return false;
            if (area < 0) n = -n;
            reason = "The route needs an unambiguous planar winding";
            if (count > 2 && Mathf.Abs(area) < .00001f) return false;
            // Include the ribbon half-width and maximum sampled-arc sagitta.
            var radii = parts.Select(p => (Mathf.Max(p.Radius, p.ToothTipRadius) + Width / 2) / Mathf.Cos(ArcStep * .5f * Mathf.Deg2Rad)).ToArray();
            reason = "Move overlapping sprockets apart to leave room for the chain";
            for (int i = 0; i < count; i++) {
                if (!Finite(radii[i])) return false;
                for (int j = 0; j < i; j++) if (Vector3.Distance(centers[i], centers[j]) <= radii[i] + radii[j] + .001f) return false;
            }
            var starts = new Vector3[count]; var ends = new Vector3[count];
            for (int i = 0; i < count; i++) {
                int next = (i + 1) % count;
                float a = (wraps[i] == 1 ? -1 : 1) * radii[i];
                float b = (wraps[next] == 1 ? -1 : 1) * radii[next];
                var delta = centers[next] - centers[i];
                reason = "The chain route exceeds numerical geometry limits";
                float distance = delta.magnitude;
                if (!Finite(distance)) return false;
                float cosine = (a - b) / distance;
                reason = "No tangent exists for these wrap choices";
                if (Mathf.Abs(cosine) >= 1) return false;
                var u = delta.normalized;
                var tangentNormal = u * cosine - Vector3.Cross(n, u) * Mathf.Sqrt(1 - cosine * cosine);
                starts[i] = centers[i] + a * tangentNormal;
                ends[i] = centers[next] + b * tangentNormal;
            }
            var incoming = new Vector3[count]; var angles = new float[count];
            float totalTurn = 0;
            for (int i = 0; i < count; i++) {
                incoming[i] = ends[(i + count - 1) % count] - centers[i];
                int sign = wraps[i] == 1 ? -1 : 1;
                angles[i] = sign * Mathf.Repeat(sign * Vector3.SignedAngle(incoming[i], starts[i] - centers[i], n), 360);
                reason = "A sprocket has no usable wrap; change its wrap or route order";
                if (!Finite(angles[i]) || Mathf.Abs(angles[i]) < .05f || Mathf.Abs(angles[i]) > 359.95f) return false;
                totalTurn += angles[i];
            }
            reason = "Wrap choices must form one simple closed chain with outer winding";
            if (Mathf.Abs(totalTurn - 360) > .05f) return false;
            bool OnArc(int i, Vector3 radial) {
                float sign = Mathf.Sign(angles[i]);
                float angle = Mathf.Repeat(sign * Vector3.SignedAngle(incoming[i], radial, n), 360);
                return angle <= Mathf.Abs(angles[i]) + .0001f || angle > 359.9999f;
            }
            float PointArcDistance(int i, Vector3 point) {
                float distance = Mathf.Min(Vector3.Distance(point, centers[i] + incoming[i]), Vector3.Distance(point, starts[i]));
                var radial = point - centers[i];
                if (OnArc(i, radial)) distance = Mathf.Min(distance, Mathf.Abs(radial.magnitude - radii[i]));
                return distance;
            }
            // Full ribbon clearance between nonincident runs/arcs and between
            // separate wraps. Checking only tooth discs misses tangent self-touches.
            reason = "The chain wraps or runs are too close to each other";
            for (int i = 0; i < count; i++) {
                for (int j = 0; j < count; j++) {
                    if (j == i || j == (i + 1) % count) continue;
                    var run = ends[i] - starts[i];
                    var foot = starts[i] + run * Mathf.Clamp01(Vector3.Dot(centers[j] - starts[i], run) / run.sqrMagnitude);
                    float distance = Mathf.Min(PointArcDistance(j, starts[i]), PointArcDistance(j, ends[i]),
                        DistanceToSegment(centers[j] + incoming[j], starts[i], ends[i]), DistanceToSegment(starts[j], starts[i], ends[i]));
                    if (OnArc(j, foot - centers[j])) distance = Mathf.Min(distance, Mathf.Abs(Vector3.Distance(foot, centers[j]) - radii[j]));
                    if (distance < Width - .0001f) return false;
                }
                for (int j = 0; j < i; j++) {
                    float distance = Mathf.Min(PointArcDistance(i, centers[j] + incoming[j]), PointArcDistance(i, starts[j]),
                        PointArcDistance(j, centers[i] + incoming[i]), PointArcDistance(j, starts[i]));
                    var toward = (centers[j] - centers[i]).normalized;
                    if (OnArc(i, toward) && OnArc(j, -toward)) distance = Mathf.Min(distance, Vector3.Distance(centers[i], centers[j]) - radii[i] - radii[j]);
                    if (distance < Width - .0001f) return false;
                }
            }
            // Exact straight-run clearance against every sprocket, including
            // non-neighbors. Separate inflated circles make arc/arc collisions impossible.
            reason = "A tangent run hits a sprocket; change the route order or wrap";
            for (int i = 0; i < count; i++) for (int j = 0; j < count; j++)
                if (DistanceToSegment(centers[j], starts[i], ends[i]) < radii[j] - .0001f) return false;
            reason = "The chain crosses itself or its straight runs have insufficient clearance";
            for (int i = 0; i < count; i++) for (int j = 0; j < i; j++) {
                if (Intersects(starts[i], ends[i], starts[j], ends[j], n)) return false;
                bool adjacent = j == i - 1 || i == count - 1 && j == 0;
                if (!adjacent && Mathf.Min(DistanceToSegment(starts[i], starts[j], ends[j]), DistanceToSegment(ends[i], starts[j], ends[j]),
                    DistanceToSegment(starts[j], starts[i], ends[i]), DistanceToSegment(ends[j], starts[i], ends[i])) < Width) return false;
            }
            var points = new List<Vector3>();
            float segmentLength = radii.Min() * ArcStep * Mathf.Deg2Rad;
            for (int i = 0; i < count; i++) {
                int samples = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(angles[i]) / ArcStep));
                for (int sample = 0; sample <= samples; sample++) points.Add(origin + centers[i] + Quaternion.AngleAxis(angles[i] * sample / samples, n) * incoming[i]);
                float sampleCount = Vector3.Distance(starts[i], ends[i]) / segmentLength;
                reason = "The chain route is too large to render";
                // Bound the float before casting: CeilToInt overflows for
                // malicious finite coordinates and could bypass an int bound.
                if (!Finite(sampleCount) || sampleCount > 8192 || points.Count + sampleCount > 65536) return false;
                int runSamples = Mathf.CeilToInt(sampleCount);
                for (int sample = 1; sample < runSamples; sample++) points.Add(origin + Vector3.Lerp(starts[i], ends[i], sample / (float)runSamples));
            }
            reason = "The chain route exceeds numerical geometry limits";
            for (int i = 0; i < points.Count; i++) if (!Finite(points[i]) || (points[i] - points[(i + 1) % points.Count]).sqrMagnitude < 1e-12f) return false;
            contour = new Contour { parts = parts, wraps = wraps, normal = n, points = points.ToArray() };
            reason = null; return true;
        }
        public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b) {
            var d = b - a;
            return Vector3.Distance(p, a + d * Mathf.Clamp01(Vector3.Dot(p - a, d) / Mathf.Max(1e-20f, d.sqrMagnitude)));
        }
        private static bool Intersects(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n) {
            float Cross(Vector3 u, Vector3 v) => Vector3.Dot(Vector3.Cross(u, v), n);
            float denominator = Cross(b - a, d - c);
            if (Mathf.Abs(denominator) < 1e-9f) return false;
            float t = Cross(c - a, d - c) / denominator, u = Cross(c - a, b - a) / denominator;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }
        private static void Basis(Vector3 n, out Vector3 x, out Vector3 y) {
            x = Vector3.ProjectOnPlane(Mathf.Abs(Vector3.Dot(n, Vector3.right)) < .9f ? Vector3.right : Vector3.up, n).normalized;
            y = Vector3.Cross(n, x);
        }
        private static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    }
}
