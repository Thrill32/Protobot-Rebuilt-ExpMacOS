using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>Rigid fastener assemblies coupled by shaft, gear, rack and chain motion.</summary>
    public sealed class MechanismSimulation {
        public sealed class Track {
            public GameObject body;
            public Vector3 center, axis;
            public Pose start;
            public double ratio;
            public bool rotates, linear, showInTimeline = true;
            public string Label => PartLabel(body);
            public double Period => linear ? double.PositiveInfinity : 4 / Math.Abs(ratio);
            public double LinearSpeed => 90 * ratio;
        }
        private sealed class Port {
            public GameObject body;
            public Vector3 center, axis;
            public Shaft shaft;
            public Motor motor;
            public SimulationPart gear;
            public Node node;
            public bool Linear => gear != null && gear.IsLinear;
        }
        private sealed class Edge { public Port from, to; public double factor; }
        private sealed class Node {
            public readonly List<GameObject> bodies = new();
            public readonly List<Edge> edges = new();
            public bool assigned, linear;
            public Vector3 center, axis;
            public double ratio;
        }
        public readonly List<Track> tracks = new();
        private readonly List<ChainLoop> chains = new();
        public double Time { get; private set; }
        public double LoopDuration { get; private set; } = 4;
        public bool HasFiniteLoop { get; private set; } = true;
        public bool HasConflict { get; private set; }
        public bool IsValid => tracks.All(t => t.body != null && t.body.activeInHierarchy && !t.body.IsDeleted());
        public bool HasMotion => tracks.Any(t => t.rotates);
        public GameObject Driver { get; private set; }
        public static bool IsDriver(GameObject body) => body != null &&
            (body.GetComponent<Motor>() != null || body.GetComponent<Shaft>() != null || body.GetComponent<SimulationPart>() != null);
        public static string PartLabel(GameObject body) {
            if (body == null) return "Removed part";
            if (body.TryGetComponent<SimulationPart>(out var part)) {
                if (part.kind == DriveKind.Sprocket) return $"{part.teeth}T sprocket";
                if (part.kind == DriveKind.Bevel) return $"{part.teeth}T bevel gear";
                if (part.kind == DriveKind.Worm) return "Worm";
                if (part.kind == DriveKind.Rack) return "Rack";
                if (part.kind != DriveKind.Attachment) return $"{part.teeth}T gear";
            }
            if (body.GetComponent<Motor>() != null) return "Motor";
            if (body.GetComponent<Shaft>() != null) return "Shaft";
            var id = body.GetComponent<SavedObject>()?.nameId;
            return id switch {
                "CLMP" or "LWHSC" => "Shaft collar", "SPKT" => "Sprocket", "HDCK" => "Hand crank", "LKBR" => "Lock bar",
                _ => body.name.Replace("(Clone)", "")
            };
        }
        public static MechanismSimulation Create(GameObject driver) {
            if (!IsDriver(driver)) return null;
            var bodies = UnityEngine.Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None)
                .Select(s => s.gameObject).Where(Live).ToList();
            foreach (var body in bodies) body.transform.DOComplete();
            Physics.SyncTransforms();
            foreach (var detector in UnityEngine.Object.FindObjectsByType<HoleDetector>(FindObjectsSortMode.None)) detector.RefreshOverlaps();
            var parent = bodies.ToDictionary(b => b, b => b);
            GameObject Root(GameObject body) { while (parent[body] != body) { parent[body] = parent[parent[body]]; body = parent[body]; } return body; }
            void Union(GameObject a, GameObject b) { if (parent.ContainsKey(a) && parent.ContainsKey(b)) parent[Root(b)] = Root(a); }
            foreach (var body in bodies) {
                var owner = body.transform.parent != null ? body.transform.parent.GetComponentInParent<SavedObject>() : null;
                if (owner != null) Union(owner.gameObject, body);
            }
            // Screw joints are rigid; a shaft passing through a bearing is not.
            foreach (var body in bodies) {
                var screw = body.GetComponent<Screw>();
                if (screw == null || !screw.CanFormConnection) continue;
                foreach (var attached in screw.GetAttachedParts() ?? new List<GameObject>())
                    if (attached != null) Union(body, attached);
            }
            var ports = new List<Port>();
            foreach (var body in bodies.Where(IsDriver)) {
                var p = new Port { body = body, shaft = body.GetComponent<Shaft>(), motor = body.GetComponent<Motor>(), gear = body.GetComponent<SimulationPart>() };
                if (p.shaft != null) { p.center = body.transform.position; p.axis = body.transform.forward; }
                else if (p.motor != null && p.motor.ShaftHole != null) { p.center = p.motor.ShaftHole.transform.position; p.axis = p.motor.ShaftHole.transform.forward; }
                else if (p.gear != null) { p.center = p.gear.Center; p.axis = p.Linear ? p.gear.Travel : p.gear.Axis; }
                else continue;
                ports.Add(p);
            }
            foreach (var shaft in ports.Where(p => p.shaft != null)) foreach (var hub in ports.Where(p => p.gear != null && !p.Linear)) {
                var delta = hub.center - shaft.center;
                if (Mathf.Abs(Vector3.Dot(shaft.axis, hub.axis)) > .995f && Vector3.ProjectOnPlane(delta, shaft.axis).magnitude < .025f &&
                    Mathf.Abs(Vector3.Dot(delta, shaft.axis)) <= shaft.shaft.ShaftLength / 2 + .025f) Union(shaft.body, hub.body);
            }
            var nodes = bodies.GroupBy(Root).ToDictionary(g => g.Key, g => {
                var node = new Node(); node.bodies.AddRange(g); return node;
            });
            foreach (var port in ports) port.node = port.motor != null ? new Node() : nodes[Root(port.body)];
            var source = ports.FirstOrDefault(p => p.body == driver);
            if (source == null) return null;
            void Link(Port a, Port b, double factor) {
                a.node.edges.Add(new Edge { from = a, to = b, factor = factor });
                b.node.edges.Add(new Edge { from = b, to = a, factor = 1 / factor });
            }
            foreach (var motor in ports.Where(p => p.motor != null)) foreach (var shaft in ports.Where(p => p.shaft != null)) {
                var delta = motor.center - shaft.center;
                float sign = Vector3.Dot(motor.axis, shaft.axis);
                if (Mathf.Abs(sign) > .995f && Vector3.ProjectOnPlane(delta, shaft.axis).magnitude < .025f &&
                    Mathf.Abs(Vector3.Dot(delta, shaft.axis)) <= shaft.shaft.ShaftLength / 2 + .025f) Link(motor, shaft, Math.Sign(sign));
            }
            var gears = ports.Where(p => p.gear != null && p.gear.CanMesh).ToArray();
            for (int i = 0; i < gears.Length; i++) for (int j = i + 1; j < gears.Length; j++)
                if (GearSnapping.TryTransmission(gears[i].gear, gears[j].gear, out double factor)) Link(gears[i], gears[j], factor);
            var result = new MechanismSimulation { Driver = driver };
            foreach (var chain in UnityEngine.Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None)) {
                var members = chain.ResolveParts();
                if (!ChainLoop.Compatible(members, out _)) continue;
                var chainPorts = members.Select(g => ports.FirstOrDefault(p => p.body == g.gameObject)).ToArray();
                if (chainPorts.Any(p => p == null)) continue;
                for (int i = 1; i < chainPorts.Length; i++) {
                    var a = chainPorts[0]; var b = chainPorts[i];
                    Link(a, b, (double)a.gear.teeth / b.gear.teeth * Math.Sign(Vector3.Dot(a.axis, b.axis)));
                }
                result.chains.Add(chain);
            }
            var queue = new Queue<Node>();
            void Assign(Node node, Port anchor, double ratio) {
                node.assigned = true; node.center = anchor.center; node.axis = anchor.axis; node.linear = anchor.Linear; node.ratio = ratio; queue.Enqueue(node);
            }
            Assign(source.node, source, source.Linear ? 1 / 90.0 : 1);
            while (queue.Count > 0) {
                var current = queue.Dequeue();
                foreach (var edge in current.edges) {
                    float sign = Vector3.Dot(current.axis, edge.from.axis);
                    if (current.linear != edge.from.Linear || Mathf.Abs(sign) < .995f ||
                        (!current.linear && Vector3.ProjectOnPlane(edge.from.center - current.center, current.axis).magnitude > .025f)) continue;
                    double next = current.ratio * Math.Sign(sign) * edge.factor;
                    var target = edge.to.node;
                    if (!target.assigned) Assign(target, edge.to, next);
                    else if (target.linear != edge.to.Linear ||
                        (target.axis * (float)target.ratio - edge.to.axis * (float)next).magnitude > .0001f * Math.Max(1, Math.Abs(next)) ||
                        (!target.linear && Vector3.ProjectOnPlane(target.center - edge.to.center, target.axis).magnitude > .025f)) result.HasConflict = true;
                }
            }
            if (result.HasConflict) return result;
            foreach (var node in nodes.Values.Where(n => n.assigned)) foreach (var body in node.bodies) {
                var saved = body.GetComponent<SavedObject>();
                result.tracks.Add(new Track { body = body, center = node.center, axis = node.axis, linear = node.linear,
                    start = new Pose(body.transform.position, body.transform.rotation), ratio = node.ratio, rotates = true,
                    showInTimeline = body.GetComponent<Screw>() == null && saved.nameId != "NUT" && saved.nameId != "WSHR" });
            }
            result.HasFiniteLoop = !result.tracks.Any(t => t.linear);
            if (result.HasFiniteLoop) {
                result.HasFiniteLoop = false;
                for (int turns = 1; turns <= 10000; turns++) {
                    if (!result.tracks.All(t => Math.Abs(t.ratio * turns - Math.Round(t.ratio * turns)) < .0001)) continue;
                    result.LoopDuration = turns * 4; result.HasFiniteLoop = true; break;
                }
            }
            return result;
        }
        private static bool Live(GameObject body) => body != null && body.activeInHierarchy && !body.IsDeleted() && body.layer != Placement.PLACEMENT_LAYER;
        public void Evaluate(double seconds) {
            Time = seconds;
            foreach (var track in tracks.Where(t => t.body != null && t.rotates).OrderBy(t => Depth(t.body.transform))) {
                if (track.linear) { track.body.transform.SetPositionAndRotation(track.start.position + track.axis * (float)(seconds * 90 * track.ratio), track.start.rotation); continue; }
                var rotation = Quaternion.AngleAxis((float)((seconds * 90 * track.ratio) % 360), track.axis);
                track.body.transform.SetPositionAndRotation(track.center + rotation * (track.start.position - track.center), rotation * track.start.rotation);
            }
            foreach (var chain in chains.Where(c => c != null)) {
                var first = chain.ResolveParts().FirstOrDefault();
                var track = first != null ? tracks.FirstOrDefault(t => t.body == first.gameObject) : null;
                if (track != null) chain.SetTravel((float)(seconds * 90 * track.ratio / 360) * first.teeth * first.chainPitch * Math.Sign(Vector3.Dot(track.axis, first.Axis)));
            }
        }
        private static int Depth(Transform transform) { int depth = 0; while (transform.parent != null) { depth++; transform = transform.parent; } return depth; }
        public void Restore() {
            foreach (var track in tracks.Where(t => t.body != null).OrderBy(t => Depth(t.body.transform))) track.body.transform.SetPositionAndRotation(track.start.position, track.start.rotation);
            foreach (var chain in chains.Where(c => c != null)) chain.SetTravel(0);
            Time = 0;
        }
        public bool TryGetOriginal(GameObject body, out Pose pose) {
            var track = tracks.FirstOrDefault(t => t.body == body); pose = track != null ? track.start : default; return track != null;
        }
    }
}
