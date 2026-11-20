using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using UnityEngine;

namespace Protobot.Simulation {
    internal sealed class RopeRoutingDraft {
        internal enum Pick { None, Drum, End, Pulley }
        internal readonly SavedObject drum;
        internal readonly RopeRoute original;
        internal readonly List<SavedObject> pulleys = new();
        internal readonly List<int> wraps = new() { 0 };
        internal SavedObject EndBody { get; private set; }
        internal RopeAttachment Start { get; private set; }
        internal RopeAttachment End { get; private set; }
        internal float availableLength = RopeRouteData.KitLength;
        private Matrix4x4[] matrices;
        private RopeRouteGeometry.Contour contour;
        private string problem;

        internal RopeRoutingDraft(SavedObject drum, IEnumerable<SavedObject> selected) {
            this.drum = drum;
            original = RopeRoute.Find(drum.ConnectionId);
            if (original == null) {
                foreach (var pulley in selected.OrderBy(p => Vector3.Distance(p.transform.position, drum.transform.position))
                             .ThenBy(p => p.ConnectionId, StringComparer.Ordinal).Take(RopeRouteData.MaxPulleys))
                    AddPulley(pulley, 0);
                return;
            }
            var saved = original.Data;
            var bodies = UnityEngine.Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None)
                .Where(body => !body.gameObject.IsDeleted()).ToLookup(body => body.ConnectionId);
            Start = saved.start;
            End = saved.end;
            EndBody = bodies[End.part].FirstOrDefault();
            availableLength = saved.availableLength;
            wraps[0] = saved.wraps[0];
            for (int i = 0; i < saved.pulleys.Length; i++) {
                var pulley = bodies[saved.pulleys[i]].FirstOrDefault();
                if (pulley != null) AddPulley(pulley, saved.wraps[i + 1]);
            }
        }

        internal RopeRouteData Data => new() {
            start = Start, end = End, availableLength = availableLength,
            pulleys = pulleys.Select(pulley => pulley.ConnectionId).ToArray(), wraps = wraps.ToArray()
        };

        internal void Invalidate() => matrices = null;

        internal void Move(int index, int target) {
            (pulleys[index], pulleys[target]) = (pulleys[target], pulleys[index]);
            (wraps[index + 1], wraps[target + 1]) = (wraps[target + 1], wraps[index + 1]);
            Invalidate();
        }

        internal void Reverse() {
            pulleys.Reverse();
            wraps.Reverse(1, pulleys.Count);
            Invalidate();
        }

        internal void Remove(int index) {
            pulleys.RemoveAt(index);
            wraps.RemoveAt(index + 1);
            Invalidate();
        }

        internal bool Attach(Pick pick, RaycastHit hit, out string reason) {
            reason = "Click a visible part in the build.";
            var body = hit.collider?.GetComponentInParent<SavedObject>();
            if (body == null || body.gameObject.IsDeleted()) return false;
            switch (pick) {
                case Pick.Drum:
                    reason = "Click the selected winch drum, away from its axle center.";
                    if (body != drum) return false;
                    var groove = drum.GetComponent<RopeGroove>();
                    var point = RopeRouteGeometry.DrumPoint(groove, hit.point);
                    if (Vector3.Distance(point, groove.Center) < .001f) return false;
                    Start = RopeAttachment.At(drum, point);
                    break;
                case Pick.End:
                    reason = "Choose an end attachment on a separate part.";
                    if (body == drum || pulleys.Contains(body)) return false;
                    EndBody = body;
                    End = RopeAttachment.At(body, hit.collider.TryGetComponent(out HoleCollider hole) ? hole.transform.position : hit.point);
                    break;
                case Pick.Pulley:
                    reason = $"Choose an unused VEX Pulley. A route supports up to {RopeRouteData.MaxPulleys}.";
                    if (body.id != RopeRouteData.PulleyId || body == EndBody || pulleys.Contains(body) ||
                        pulleys.Count >= RopeRouteData.MaxPulleys) return false;
                    AddPulley(body, 0);
                    break;
                default:
                    return false;
            }
            Invalidate();
            reason = null;
            return true;
        }

        internal bool TryPreview(out RopeRouteGeometry.Contour preview, out string reason) {
            preview = null;
            reason = "Pick both attachments and add at least one VEX Pulley.";
            if (string.IsNullOrEmpty(Start.part) || EndBody == null || pulleys.Count == 0) return false;
            var parts = pulleys.Prepend(drum).Append(EndBody).ToArray();
            if (parts.Any(body => body == null || !body.gameObject.activeInHierarchy || body.gameObject.IsDeleted())) {
                Invalidate();
                reason = "One of the route parts is unavailable.";
                return false;
            }
            bool changed = matrices == null;
            matrices ??= new Matrix4x4[parts.Length];
            for (int i = 0; i < parts.Length; i++) {
                var matrix = parts[i].transform.localToWorldMatrix;
                changed |= !matrices[i].Equals(matrix);
                matrices[i] = matrix;
            }
            if (changed) RopeRoute.TryContour(Data, parts, out contour, out problem);
            preview = contour;
            reason = problem;
            return contour != null;
        }

        private void AddPulley(SavedObject pulley, int wrap) {
            pulleys.Add(pulley);
            wraps.Add(wrap);
        }
    }
}
