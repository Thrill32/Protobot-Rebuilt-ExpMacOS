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
            var text = ClipboardPrefix + JsonUtility.ToJson(build);
            if (text.Length > MaxClipboardCharacters) throw new InvalidDataException("The copied selection is too large.");
            return text;
        }

        public static BuildData FromClipboard(string text) {
            if (string.IsNullOrEmpty(text) || !text.StartsWith(ClipboardPrefix, StringComparison.Ordinal)) return null;
            if (text.Length > MaxClipboardCharacters) throw new InvalidDataException("The copied selection is too large.");
            var build = JsonUtility.FromJson<BuildData>(text.Substring(ClipboardPrefix.Length));
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
            if (build.placementAnchor != null) {
                var a = build.placementAnchor;
                if (new[] { a.x, a.y, a.z, a.qx, a.qy, a.qz, a.qw }.Any(v => float.IsNaN(v) || float.IsInfinity(v)) ||
                    Mathf.Abs(a.qx * a.qx + a.qy * a.qy + a.qz * a.qz + a.qw * a.qw - 1) > .01f)
                    throw new InvalidDataException("The build contains an invalid placement anchor.");
            }
        }

        // Connected placement must fail closed because accepted logical topology
        // is durable. Offline placement retains its legacy behavior of ignoring
        // malformed/incomplete optional chain records.
        public static void ValidateChains(BuildData build) {
            var chains = build?.chains ?? Array.Empty<ChainData>();
            if (chains.Length > Protobot.Collaboration.DocumentModel.MaxChains)
                throw new InvalidDataException("A transferred build contains too many chains.");
            var idCounts = build.parts.Where(part => !string.IsNullOrEmpty(part?.connectionId))
                .GroupBy(part => part.connectionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var topologies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chain in chains) {
                if (chain?.members == null || chain.members.Length < 2 || chain.members.Length > Protobot.Collaboration.DocumentModel.MaxWriteSet ||
                    chain.members.Any(string.IsNullOrEmpty) || chain.members.Distinct(StringComparer.Ordinal).Count() != chain.members.Length ||
                    chain.members.Any(id => !idCounts.TryGetValue(id, out var count) || count != 1))
                    throw new InvalidDataException("The build contains an invalid chain.");
                var topology = string.Join("\0", chain.members.OrderBy(id => id, StringComparer.Ordinal));
                if (!topologies.Add(topology)) throw new InvalidDataException("The build contains a duplicate chain.");
            }
        }
    }
}
