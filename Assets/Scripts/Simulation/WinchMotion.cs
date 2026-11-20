using System;
using System.Linq;
using Protobot.Builds;
using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>The free rope runs between fixed grooves and translates the load along the exit tangent.</summary>
    internal sealed class WinchMotion {
        public const string RatchetId = "WRAT";
        public const string PawlId = "WPAW";
        public readonly RopeRoute route;
        public readonly GameObject pawl;
        public readonly Vector3 axis, normal, pulleyCenter;
        public readonly double inchesPerDegree, pulleyRatio;
        private readonly double takeUpLimit, payoutLimit;
        public bool PawlEngaged { get; set; }
        public bool HasPawl { get; }
        public double PayoutSpeed => inchesPerDegree * 90;
        public double MinTime => Math.Min(-takeUpLimit / PayoutSpeed, (PawlEngaged ? 0 : payoutLimit) / PayoutSpeed);
        public double MaxTime => Math.Max(-takeUpLimit / PayoutSpeed, (PawlEngaged ? 0 : payoutLimit) / PayoutSpeed);

        public static WinchMotion Create(RopeRoute route, RopeRouteData data, SavedObject[] parts, double drumRatio,
            GameObject pawl, out string reason) {
            if (!RopeRoute.TryContour(data, parts, out var contour, out reason)) return null;
            if (contour.length > data.availableLength + .0001f) {
                reason = "The winch route exceeds its rope length.";
                return null;
            }
            var drum = parts[0].GetComponent<RopeGroove>();
            var pulley = parts[1].GetComponent<RopeGroove>();
            var start = parts[0].transform.TransformPoint(data.start.LocalPoint);
            var end = parts[2].transform.TransformPoint(data.end.LocalPoint);
            if (Mathf.Abs(Vector3.Dot(pulley.Center - drum.Center, contour.normal)) > .001f ||
                Mathf.Abs(Vector3.Dot(end - contour.exit, contour.normal)) > .001f ||
                Mathf.Abs(Vector3.Dot(pulley.Axis, contour.normal)) < .99999f) {
                reason = "Align the winch grooves and load point in the same travel plane.";
                return null;
            }
            if (!ClearOfDrum(drum, contour.exit, end)) {
                reason = "Keep the load's travel span clear of the drum.";
                return null;
            }
            return new WinchMotion(route, data, pawl, contour, drum, pulley, start, end, drumRatio);
        }

        private WinchMotion(RopeRoute route, RopeRouteData data, GameObject pawl, RopeRouteGeometry.Contour contour,
            RopeGroove drum, RopeGroove pulley, Vector3 start, Vector3 end, double drumRatio) {
            this.route = route;
            this.pawl = pawl;
            HasPawl = pawl != null;
            PawlEngaged = HasPawl;
            normal = contour.normal;
            pulleyCenter = pulley.Center;
            axis = Vector3.ProjectOnPlane(end - contour.exit, normal).normalized;
            double drumRadius = RopeRouteGeometry.Radius(drum), pulleyRadius = RopeRouteGeometry.Radius(pulley);
            inchesPerDegree = drumRatio * WrapSign(data.wraps[0]) * drumRadius * Math.PI / 180;
            pulleyRatio = inchesPerDegree * 180 / Math.PI / pulleyRadius * WrapSign(data.wraps[1]);

            // Each additional turn fits only when the authored length can cover it.
            // The visible partial turn ends at the explicit drum attachment.
            double turn = 2 * Math.PI * drumRadius;
            double turns = Math.Max(0, Math.Floor((data.availableLength - contour.length + .0001) / turn));
            double payout = contour.drumWrapLength + turns * turn;
            float takeUp = Mathf.Max(0, Vector3.Dot(end - contour.exit, axis) - RopeRouteGeometry.Diameter);
            var grooves = new[] { drum, pulley };
            bool Clear(double distance) {
                Vector3 target = end + axis * (float)distance;
                return ClearOfDrum(drum, contour.exit, target) &&
                    RopeRouteGeometry.TryBuild(grooves, start, target, data.wraps, out _, out _);
            }
            takeUpLimit = ClearDistance(takeUp, distance => Clear(-distance));
            payoutLimit = ClearDistance(payout, Clear);
        }

        private static int WrapSign(int wrap) => wrap == 0 ? 1 : -1;

        private static bool ClearOfDrum(RopeGroove drum, Vector3 exit, Vector3 end) =>
            ChainRouteGeometry.DistanceToSegment(drum.Center, exit, end) >= RopeRouteGeometry.Radius(drum) + RopeRouteGeometry.Diameter;

        private static double ClearDistance(double limit, Func<double, bool> clear) {
            if (limit <= 0 || clear(limit)) return limit;
            // The end follows the same tangent. Once the growing span reaches
            // another part of this rope contour, further payout stays blocked.
            double near = 0, far = Math.Min(1, limit);
            while (far < limit && clear(far)) { near = far; far = Math.Min(limit, far * 2); }
            for (int i = 0; i < 32 && far - near > .00001; i++) {
                double middle = near + (far - near) / 2;
                if (clear(middle)) near = middle; else far = middle;
            }
            return near;
        }

        internal static bool ReachesRatchet(SimulationPart pawl, SimulationPart ratchet) {
            if (Mathf.Abs(Vector3.Dot(pawl.Axis, ratchet.Axis)) < .995f) return false;
            var mesh = pawl.GetComponent<MeshFilter>().sharedMesh;
            var gearBounds = ratchet.GetComponent<MeshFilter>().sharedMesh.bounds;
            float radius = ratchet.ToothTipRadius;
            // The keyed pivot is outside the wheel. Test the pawl's central
            // tip section against the swept tooth envelope, across tooth phases.
            var pivot = ratchet.transform.InverseTransformPoint(pawl.Center);
            float gearScale = ratchet.transform.lossyScale.x;
            if (Vector3.ProjectOnPlane(pivot, ratchet.localAxis).magnitude * gearScale <= radius) return false;
            return mesh.vertices.Any(vertex => {
                if (vertex.x < .43f || Mathf.Abs(vertex.z) > .06f) return false;
                var point = ratchet.transform.InverseTransformPoint(pawl.transform.TransformPoint(vertex));
                return point.z >= gearBounds.min.z + .055f && point.z <= gearBounds.max.z + .02f &&
                    Vector3.ProjectOnPlane(point, ratchet.localAxis).magnitude * gearScale <= radius + .025f;
            });
        }
    }
}
