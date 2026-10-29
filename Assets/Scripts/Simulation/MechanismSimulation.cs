using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>Reversible kinematic rotation graph. Future drives/presets can supply different input motion.</summary>
    public sealed class MechanismSimulation {
        public sealed class Track {
            public GameObject body;
            public Vector3 center, axis;
            public Pose start;
            public double ratio;
            public bool rotates;
            public string Label => PartLabel(body);
            public double Period => 4 / Math.Abs(ratio);
        }

        private sealed class Node {
            public GameObject body;
            public Vector3 center, axis;
            public bool rotates = true;
            public Shaft shaft;
            public SimulationPart gear;
            public readonly List<(Node node, double ratio)> edges = new();
        }

        public readonly List<Track> tracks = new();
        public double Time { get; private set; }
        public double LoopDuration { get; private set; } = 4;
        public bool HasConflict { get; private set; }
        public bool IsValid => tracks.All(t => t.body != null && t.body.activeInHierarchy && !t.body.IsDeleted());
        public bool HasMotion => tracks.Any(t => t.rotates);
        public GameObject Driver { get; private set; }

        public static bool IsDriver(GameObject body) => body != null &&
            (body.GetComponent<Motor>() != null || body.GetComponent<Shaft>() != null || body.GetComponent<SimulationPart>() != null);

        public static string PartLabel(GameObject body) {
            if (body == null) return "Removed part";
            if (body.TryGetComponent<SimulationPart>(out var gear)) return $"{gear.teeth}T gear";
            if (body.GetComponent<Motor>() != null) return "Motor";
            if (body.GetComponent<Shaft>() != null) return "Shaft";
            return body.GetComponent<SavedObject>()?.nameId switch {
                "CLMP" or "LWHSC" => "Shaft collar",
                "SPKT" => "Sprocket",
                "HDCK" => "Hand crank",
                "LKBR" => "Lock bar",
                _ => "Wheel"
            };
        }

        private static bool IsFollower(GameObject body) {
            var id = body.GetComponent<SavedObject>()?.nameId;
            return id == "CLMP" || id == "LWHSC" || id == "SPKT" || id == "TWHL" || id == "OMNI" ||
                id == "FWHL" || id == "MECN" || id == "LKBR" || id == "HDCK";
        }

        public static MechanismSimulation Create(GameObject driver) {
            if (!IsDriver(driver)) return null;
            var nodes = new List<Node>();
            foreach (var saved in UnityEngine.Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None).OrderBy(s => s.id, StringComparer.Ordinal)) {
                var body = saved.gameObject;
                if (body.IsDeleted() || body.layer == Placement.PLACEMENT_LAYER || (!IsDriver(body) && !IsFollower(body))) continue;
                var node = new Node { body = body, shaft = body.GetComponent<Shaft>(), gear = body.GetComponent<SimulationPart>() };
                if (node.shaft != null) {
                    node.center = body.transform.position;
                    node.axis = body.transform.forward;
                }
                else if (node.gear != null) {
                    node.center = node.gear.Center;
                    node.axis = node.gear.Axis;
                }
                else {
                    var motor = body.GetComponent<Motor>();
                    var hole = motor != null ? motor.ShaftHole : body.GetComponentsInChildren<HoleCollider>()
                        .OrderBy(h => h.transform.localPosition.sqrMagnitude).FirstOrDefault();
                    if (hole == null) continue;
                    node.center = hole.transform.position;
                    node.axis = hole.transform.forward;
                    node.rotates = motor == null;
                }
                nodes.Add(node);
            }
            var source = nodes.FirstOrDefault(n => n.body == driver);
            if (source == null) return null;
            void Link(Node a, Node b, double factor) {
                a.edges.Add((b, factor)); b.edges.Add((a, 1 / factor));
            }
            foreach (var shaft in nodes.Where(n => n.shaft != null)) {
                foreach (var other in nodes.Where(n => n != shaft && n.shaft == null)) {
                    float direction = Vector3.Dot(shaft.axis, other.axis);
                    var offset = other.center - shaft.center;
                    float axial = Vector3.Dot(offset, shaft.axis);
                    if (Mathf.Abs(direction) > .995f && Vector3.ProjectOnPlane(offset, shaft.axis).magnitude < .025f &&
                        Mathf.Abs(axial) <= shaft.shaft.ShaftLength / 2 + .025f)
                        Link(shaft, other, Math.Sign(direction));
                }
            }
            var gears = nodes.Where(n => n.gear != null).ToArray();
            for (int i = 0; i < gears.Length; i++) {
                for (int j = i + 1; j < gears.Length; j++) {
                    var a = gears[i]; var b = gears[j];
                    if (GearSnapping.AreMeshed(a.gear, b.gear))
                        Link(a, b, -(double)a.gear.teeth / b.gear.teeth * Math.Sign(Vector3.Dot(a.axis, b.axis)));
                }
            }
            var result = new MechanismSimulation { Driver = driver };
            var ratios = new Dictionary<Node, double> { [source] = 1 };
            var queue = new Queue<Node>(); queue.Enqueue(source);
            while (queue.Count > 0) {
                var current = queue.Dequeue();
                foreach (var edge in current.edges) {
                    double next = ratios[current] * edge.ratio;
                    if (ratios.TryGetValue(edge.node, out var previous)) {
                        if (Math.Abs(previous - next) > .0001 * Math.Max(1, Math.Abs(previous))) result.HasConflict = true;
                    }
                    else { ratios.Add(edge.node, next); queue.Enqueue(edge.node); }
                }
            }
            // Reject incompatible closed gear loops rather than silently ignoring a constraint.
            if (result.HasConflict) return result;
            foreach (var entry in ratios) {
                var node = entry.Key;
                node.body.transform.DOComplete();
                result.tracks.Add(new Track { body = node.body, center = node.center, axis = node.axis,
                    start = new Pose(node.body.transform.position, node.body.transform.rotation), ratio = entry.Value, rotates = node.rotates });
            }
            for (int turns = 1; turns <= 10000; turns++) {
                if (!result.tracks.All(t => Math.Abs(t.ratio * turns - Math.Round(t.ratio * turns)) < .0001)) continue;
                result.LoopDuration = turns * 4;
                break;
            }
            return result;
        }

        public void Evaluate(double seconds) {
            Time = seconds;
            // All poses come from the baseline, so reverse scrubbing and large turn counts do not accumulate drift.
            foreach (var track in tracks.Where(t => t.body != null && t.rotates).OrderBy(t => Depth(t.body.transform))) {
                var rotation = Quaternion.AngleAxis((float)((seconds * 90 * track.ratio) % 360), track.axis);
                track.body.transform.SetPositionAndRotation(track.center + rotation * (track.start.position - track.center), rotation * track.start.rotation);
            }
        }

        private static int Depth(Transform transform) { int depth = 0; while (transform.parent != null) { depth++; transform = transform.parent; } return depth; }

        public void Restore() {
            foreach (var track in tracks.Where(t => t.body != null).OrderBy(t => Depth(t.body.transform)))
                track.body.transform.SetPositionAndRotation(track.start.position, track.start.rotation);
            Time = 0;
        }

        public bool TryGetOriginal(GameObject body, out Pose pose) {
            var track = tracks.FirstOrDefault(t => t.body == body);
            pose = track != null ? track.start : default;
            return track != null;
        }
    }
}
