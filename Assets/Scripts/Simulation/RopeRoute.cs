using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.StateSystems;
using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>Owns the authored rope route even when its live geometry is temporarily unavailable.</summary>
    public sealed class RopeRoute : MonoBehaviour {
        private RopeRouteData data;
        private RopeVisual visual;
        private SavedObject[] parts;
        private Matrix4x4[] matrices;
        private bool[] handedness;
        public RopeRouteData Data => data.Snapshot();
        public IEnumerable<string> MemberIds => data.MemberIds;

        public static RopeRoute Find(string drumId) => FindObjectsByType<RopeRoute>(FindObjectsSortMode.None)
            .FirstOrDefault(route => route.data.start.part == drumId);

        public static RopeRouteData[] Capture(ISet<string> ids) => FindObjectsByType<RopeRoute>(FindObjectsSortMode.None)
            .Where(route => route.MemberIds.All(ids.Contains)).Select(route => route.Data).ToArray();

        public static RopeRoute FromData(RopeRouteData data) {
            var root = new GameObject("Rope Route") { layer = LayerMask.NameToLayer("Ignore Raycast") };
            var route = root.AddComponent<RopeRoute>();
            route.visual = root.AddComponent<RopeVisual>();
            route.SetData(data);
            return route;
        }

        public static bool Apply(RopeRouteData data, out string reason) {
            reason = "Editing is unavailable in this shared room.";
            if (!CollaborationSession.CanEdit) return false;
            reason = "Choose a drum attachment, at least one pulley, an end attachment, and a positive length.";
            if (data == null || !data.ValidMetadata()) return false;
            var parts = Resolve(data);
            if (!TryContour(data, parts, out var contour, out reason)) return false;
            if (contour.length > data.availableLength) {
                reason = "The route needs more rope than is available.";
                return false;
            }
            var route = Find(data.start.part);
            if (data.SamePath(route?.data)) return true;
            if (CollaborationSession.IsShared) {
                CollaborationSession.SetRopeRoute(data);
                return true;
            }
            if (route == null) {
                route = FromData(data);
                StateSystem.AddElement(new RopeElement(route, false));
            }
            else {
                StateSystem.AddElement(new RopeElement(route, true));
                var edited = data.Snapshot();
                edited.id = route.data.id;
                route.SetData(edited);
            }
            StateSystem.AddState(new RopeElement(route, true));
            return true;
        }

        internal void SetPreviewHidden(bool hidden) => visual.SetHidden(hidden);

        public void Remove() {
            if (CollaborationSession.IsShared) {
                CollaborationSession.SetRopeRoute(data, remove: true);
                return;
            }
            StateSystem.AddElement(new RopeElement(this, true));
            gameObject.SetActive(false);
            StateSystem.AddState(new RopeElement(this, false));
        }

        internal static void ClearAll() {
            foreach (var route in FindObjectsByType<RopeRoute>(FindObjectsInactive.Include, FindObjectsSortMode.None)) route.Discard();
        }

        internal static void ApplyRecords(IEnumerable<RopeRouteData> records) {
            var accepted = records.ToDictionary(route => route.id, StringComparer.Ordinal);
            foreach (var route in FindObjectsByType<RopeRoute>(FindObjectsSortMode.None)) {
                if (!accepted.Remove(route.data.id, out var data)) route.Discard();
                else if (!route.data.SamePath(data)) route.SetData(data);
            }
            foreach (var data in accepted.Values) FromData(data);
        }

        internal void Discard() {
            gameObject.SetActive(false);
            RopeVisual.Release(gameObject);
        }

        private static bool Live(SavedObject part) => part != null && part.isActiveAndEnabled && !part.gameObject.IsDeleted();

        internal static SavedObject[] Resolve(RopeRouteData data) {
            var live = FindObjectsByType<SavedObject>(FindObjectsSortMode.None).Where(Live).ToLookup(part => part.ConnectionId);
            return data.MemberIds.Select(id => live[id].FirstOrDefault()).ToArray();
        }

        internal static bool TryContour(RopeRouteData data, IReadOnlyList<SavedObject> parts,
            out RopeRouteGeometry.Contour contour, out string reason) {
            contour = null;
            reason = "Choose both attachments and at least one pulley.";
            if (parts.Count < 3 || parts.Any(part => !Live(part))) return false;
            var grooves = new RopeGroove[parts.Count - 1];
            for (int i = 0; i < grooves.Length; i++) {
                string expected = i == 0 ? RopeRouteData.DrumId : RopeRouteData.PulleyId;
                if (parts[i].id != expected || !parts[i].TryGetComponent(out grooves[i])) return false;
            }
            return RopeRouteGeometry.TryBuild(grooves, data.start.WorldPoint(parts[0]),
                data.end.WorldPoint(parts[^1]), data.WorldWraps(parts[0]), out contour, out reason);
        }

        private void SetData(RopeRouteData value) {
            data = value.Snapshot();
            if (string.IsNullOrEmpty(data.id)) data.id = Guid.NewGuid().ToString("N");
            parts = null;
            matrices = null;
            if (isActiveAndEnabled) RefreshVisual();
        }

        private void LateUpdate() => RefreshVisual();

        private void RefreshVisual() {
            if (data == null) return;
            if (parts == null || parts.Any(part => !Live(part))) {
                parts = Resolve(data);
                matrices = null;
            }
            if (parts.Any(part => !Live(part))) {
                visual.Show(null);
                return;
            }
            bool changed = matrices == null;
            if (changed) {
                matrices = new Matrix4x4[parts.Length];
                handedness = new bool[parts.Length];
            }
            for (int i = 0; i < parts.Length; i++) {
                var matrix = parts[i].transform.localToWorldMatrix;
                changed |= !matrices[i].Equals(matrix) || handedness[i] != parts[i].Mirrored;
                matrices[i] = matrix;
                handedness[i] = parts[i].Mirrored;
            }
            if (changed) {
                bool valid = TryContour(data, parts, out var contour, out _) && contour.length <= data.availableLength + .0001f;
                visual.Show(valid ? contour : null, availableLength: data.availableLength);
            }
        }

        private sealed class RopeElement : IElement {
            private readonly RopeRoute route;
            private readonly bool active;
            private readonly RopeRouteData state;

            public RopeElement(RopeRoute route, bool active) {
                this.route = route;
                this.active = active;
                state = route.Data;
            }

            public void Load() {
                SimulationTool.ExitForEdit();
                if (route == null) return;
                route.gameObject.SetActive(active);
                route.SetData(state);
            }
        }
    }
}
