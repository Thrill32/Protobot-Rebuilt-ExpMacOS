using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Protobot.SelectionSystem;
using UnityEngine;

namespace Protobot.Builds {
    /// <summary>Portable selection snapshots shared by export, import, clipboard and placement.</summary>
    public static class BuildTransfer {
        public const string ClipboardPrefix = "PROTOBOT_SELECTION_V1\n";
        private const string AppearanceClipboardPrefix = "PROTOBOT_SELECTION_V2\n";
        private const int MaxClipboardCharacters = 96 * 1024 * 1024;

        public static List<GameObject> SelectedBodies(ISelection selection) {
            if (selection?.gameObject == null) return new List<GameObject>();
            return selection.gameObject.GetConnectedObjects(true, true, !selection.individual)
                .Where(obj => obj != null && obj.activeInHierarchy && !obj.IsDeleted())
                .SelectMany(obj => obj.GetComponentsInChildren<SavedObject>())
                .Where(saved => saved.id != "Error")
                .Select(saved => saved.gameObject).Distinct().ToList();
        }

        public static SelectedHoleAnchor HoleAnchor(ISelection selection) => selection is HoleSelection hole
            ? SelectedHoleAnchor.From(hole) : (selection as MultiSelection)?.placementHole;

        public static BuildData Capture(ISelection selection) {
            if (CollaborationSession.HasPending || CollaborationSession.HasActivePreview) return null;
            var bodies = SelectedBodies(selection);
            if (bodies.Count == 0) return null;
            var build = SceneBuild.ToBuildData(bodies);
            var anchor = HoleAnchor(selection);
            if (anchor?.Hole?.part == null || !bodies.Contains(anchor.Hole.part)) anchor = null;
            var frame = anchor != null ? anchor.Face : new Pose(selection.gameObject.transform.position, selection.gameObject.transform.rotation);
            build.placementAnchor = PlacementAnchorData.From(frame, anchor != null);
            build.name = "Selected Parts";
            build.camera.xPos = frame.position.x; build.camera.yPos = frame.position.y; build.camera.zPos = frame.position.z;
            return build;
        }

        public static string ToClipboard(BuildData build) {
            var prefix = SceneBuild.AppearanceCost(build) > 0 ? AppearanceClipboardPrefix : ClipboardPrefix;
            var text = prefix + JsonUtility.ToJson(build);
            if (text.Length > MaxClipboardCharacters) throw new InvalidDataException("The copied selection is too large.");
            return text;
        }

        public static bool HasClipboardBuild(string text) => text != null &&
            (text.StartsWith(ClipboardPrefix, StringComparison.Ordinal) || text.StartsWith(AppearanceClipboardPrefix, StringComparison.Ordinal));

        public static BuildData FromClipboard(string text) {
            if (!HasClipboardBuild(text)) return null;
            if (text.Length > MaxClipboardCharacters) throw new InvalidDataException("The copied selection is too large.");
            bool appearance = text.StartsWith(AppearanceClipboardPrefix, StringComparison.Ordinal);
            var build = JsonUtility.FromJson<BuildData>(text.Substring(appearance ? AppearanceClipboardPrefix.Length : ClipboardPrefix.Length));
            SceneBuild.ValidateCustomModels(build);
            if (appearance != (SceneBuild.AppearanceCost(build) > 0))
                throw new InvalidDataException("The clipboard selection has an invalid appearance format.");
            ValidateRopes(build);
            return build;
        }

        public static void Validate(BuildData build) {
            if (build?.parts == null || build.parts.Length == 0) throw new InvalidDataException("This build contains no parts to import.");
            if (build.parts.Length > 50000) throw new InvalidDataException("A transferred build may contain at most 50,000 parts.");
            foreach (var part in build.parts) {
                if (part == null || string.IsNullOrEmpty(part.partId)) throw new InvalidDataException("The build contains an invalid part.");
                if (new[] { part.xPos, part.yPos, part.zPos, part.xRot, part.yRot, part.zRot, part.rColor, part.gColor, part.bColor }
                    .Any(v => double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > float.MaxValue))
                    throw new InvalidDataException("The build contains invalid transforms or colors.");
                if (part.IsCustomModel) CustomModel.Validate(part.customModel);
                else if (part.partId != "Error" && PartsManager.GetPartType(part.partId) == null)
                    throw new InvalidDataException("Unknown part type: " + part.partId);
            }
            CustomModel.RequireAppearanceCapacity(SceneBuild.AppearanceCost(build));
            ValidateExplicitChainMetadata(build);
            ValidateRopes(build);
            if (build.placementAnchor != null) {
                var a = build.placementAnchor;
                if (new[] { a.x, a.y, a.z, a.qx, a.qy, a.qz, a.qw }.Any(v => float.IsNaN(v) || float.IsInfinity(v)) ||
                    Mathf.Abs(a.qx * a.qx + a.qy * a.qy + a.qz * a.qz + a.qw * a.qw - 1) > .01f)
                    throw new InvalidDataException("The build contains an invalid placement anchor.");
            }
        }

        public static void ValidateRopes(BuildData build) {
            if (build?.ropes == null || build.ropes.Length == 0) return;
            if (build.parts == null || build.ropes.Length > build.parts.Length)
                throw new InvalidDataException("The build contains more rope routes than parts.");
            var parts = (build.parts ?? Array.Empty<ObjectData>()).Where(part => !string.IsNullOrEmpty(part?.connectionId))
                .ToLookup(part => part.connectionId, StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var drums = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rope in build.ropes) {
                if (rope == null || !rope.ValidMetadata() || string.IsNullOrEmpty(rope.id) || !ids.Add(rope.id) || !drums.Add(rope.start.part) ||
                    rope.MemberIds.Any(id => parts[id].Count() != 1 || parts[id].First().partId == "Error") ||
                    parts[rope.start.part].First().partId != RopeRouteData.DrumId ||
                    rope.pulleys.Any(id => parts[id].First().partId != RopeRouteData.PulleyId))
                    throw new InvalidDataException("The build contains an invalid rope route.");
            }
        }

        public static bool IsExplicitRoute(ChainData chain) => chain != null &&
            (chain.routeVersion != 0 || chain.wraps != null && chain.wraps.Length != 0);
        public static void ValidateExplicitChainMetadata(BuildData build) {
            var routes = (build.chains ?? Array.Empty<ChainData>()).Where(IsExplicitRoute).ToArray();
            if (routes.Length == 0) return;
            ValidateChains(new BuildData { parts = build.parts, chains = routes });
        }
        // Preflight new routes before a file load removes the current build.
        // Only the referenced sprockets are generated; no logical chain or scene
        // selection is created. Legacy optional records keep their old behavior.
        public static void ValidateExplicitChainGeometry(BuildData build) {
            ValidateExplicitChainMetadata(build);
            var routes = (build.chains ?? Array.Empty<ChainData>()).Where(IsExplicitRoute).ToArray();
            if (routes.Length == 0) return;
            var ids = routes.SelectMany(c => c.members).ToHashSet();
            var root = new GameObject("Chain load validation"); root.SetActive(false);
            var temporary = new List<GameObject>();
            try {
                var parts = new Dictionary<string, Simulation.SimulationPart>(StringComparer.Ordinal);
                foreach (var part in build.parts.Where(p => ids.Contains(p.connectionId))) {
                    if (!part.partId.StartsWith("SPKT-", StringComparison.Ordinal)) throw new InvalidDataException("Chain endpoints must be sprockets.");
                    var body = SceneBuild.GenerateObject(part, build); temporary.Add(body); body.transform.SetParent(root.transform, true);
                    parts.Add(part.connectionId, body.GetComponent<Simulation.SimulationPart>());
                }
                foreach (var chain in routes) if (!Simulation.ChainRouteGeometry.TryBuild(chain.members.Select(id => parts[id]).ToArray(),
                    chain.routeVersion, chain.wraps, out _, out var reason)) throw new InvalidDataException("Invalid chain route: " + reason);
            }
            finally {
                ObjectDuplicator.DiscardCopies(temporary);
                if (Application.isPlaying) UnityEngine.Object.Destroy(root); else UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // Connected placement must fail closed because accepted logical topology
        // is durable. Offline placement ignores malformed legacy optional
        // records; explicit routes are always validated.
        public static void ValidateChains(BuildData build) {
            var chains = build?.chains ?? Array.Empty<ChainData>();
            if (chains.Length > Protobot.Collaboration.DocumentModel.MaxChains)
                throw new InvalidDataException("A transferred build contains too many chains.");
            var idCounts = build.parts.Where(part => !string.IsNullOrEmpty(part?.connectionId))
                .GroupBy(part => part.connectionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var topologies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chain in chains) {
                if (chain == null || !Protobot.Collaboration.DocumentModel.ValidRoute(chain.members, chain.routeVersion, chain.wraps) ||
                    chain.members.Any(string.IsNullOrEmpty) || chain.members.Distinct(StringComparer.Ordinal).Count() != chain.members.Length ||
                    chain.members.Any(id => !idCounts.TryGetValue(id, out var count) || count != 1))
                    throw new InvalidDataException("The build contains an invalid chain.");
                var topology = string.Join("\0", chain.members.OrderBy(id => id, StringComparer.Ordinal));
                if (!topologies.Add(topology)) throw new InvalidDataException("The build contains a duplicate chain.");
            }
        }
    }
}
