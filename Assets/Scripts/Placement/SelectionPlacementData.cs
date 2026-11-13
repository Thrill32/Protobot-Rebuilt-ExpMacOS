using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using UnityEngine;

namespace Protobot {
    /// <summary>A frozen, repeatable copy of a selection. Templates never enter the live build.</summary>
    public sealed class SelectionPlacementData : PlacementData, IDisposable {
        public override Transform placementTransform { get; set; }
        public override string objectId => "Selection Copy";
        public Quaternion InitialRotation { get; private set; }
        public bool HasHole { get; private set; }
        public bool RepeatPlacement { get; private set; } = true;
        public SelectionManager ResultSelection { get; private set; }
        public int PartCount => templates.Count;
        public readonly List<GameObject> GeneratedChains = new();

        private readonly GameObject templateRoot;
        private readonly List<GameObject> templates;
        private readonly ObjectDuplicator duplicator;
        private Mesh displayMesh;
        private bool disposed;
        private ChainData[] chains = Array.Empty<ChainData>();

        public static SelectionPlacementData Capture(ISelection selection, Transform preview, ObjectDuplicator duplicator) {
            if (selection?.gameObject == null || duplicator == null) return null;
            var bodies = BuildTransfer.SelectedBodies(selection);
            if (bodies.Count == 0) return null;
            var anchor = BuildTransfer.HoleAnchor(selection);
            if (anchor?.Hole?.part == null || !bodies.Contains(anchor.Hole.part)) anchor = null;
            var frame = anchor != null ? anchor.Face : new Pose(selection.gameObject.transform.position, selection.gameObject.transform.rotation);
            return new SelectionPlacementData(bodies, frame, anchor != null, preview, duplicator);
        }

        private SelectionPlacementData(List<GameObject> bodies, Pose frame, bool hasHole, Transform preview, ObjectDuplicator duplicator) {
            placementTransform = preview;
            InitialRotation = frame.rotation;
            HasHole = hasHole;
            this.duplicator = duplicator;
            templateRoot = new GameObject("Selection placement templates");
            templateRoot.SetActive(false);
            templates = new List<GameObject>();
            try {
                displayMesh = MeshCombiner.CombineMeshes(bodies, frame.position, frame.rotation);
                templates.AddRange(duplicator.CreateCopies(bodies, templateRoot.transform, true));
                var ids = bodies.SelectMany(body => body.GetComponentsInChildren<SavedObject>()).Select(s => s.ConnectionId).ToHashSet();
                chains = ChainLoop.SaveAll().Where(chain => chain.members.All(ids.Contains)).ToArray();
                Normalize(frame);
            }
            catch { Dispose(); throw; }
        }

        private SelectionPlacementData(Transform preview, ObjectDuplicator duplicator) {
            placementTransform = preview;
            this.duplicator = duplicator;
            templateRoot = new GameObject("Imported build templates");
            templateRoot.SetActive(false);
            templates = new List<GameObject>();
        }

        public static SelectionPlacementData FromBuild(BuildData build, Transform preview, ObjectDuplicator duplicator, SelectionManager selectResult,
            bool validateChains = false) {
            BuildTransfer.Validate(build);
            if (validateChains) BuildTransfer.ValidateChains(build);
            var data = new SelectionPlacementData(preview, duplicator) { RepeatPlacement = false, ResultSelection = selectResult };
            try {
                var renderers = new List<MeshRenderer>();
                Bounds? bounds = null;
                foreach (var part in build.parts.Where(p => p.partId != "Error")
                             .OrderBy(p => !p.IsCustomModel && PartsManager.GetPartType(p.partId)?.connectingPart == true)) {
                    var obj = SceneBuild.GenerateObject(part, build);
                    data.templates.Add(obj);
                    OwnedPartMaterials.CaptureGenerated(obj);
                    var visible = MeshCombiner.GetRenderers(new[] { obj });
                    foreach (var renderer in visible) {
                        if (!bounds.HasValue) bounds = renderer.bounds;
                        else { var combined = bounds.Value; combined.Encapsulate(renderer.bounds); bounds = combined; }
                    }
                    renderers.AddRange(visible);
                    obj.transform.SetParent(data.templateRoot.transform, true);
                }
                if (data.templates.Count == 0) throw new System.IO.InvalidDataException("This build contains no parts to import.");
                var frame = build.placementAnchor != null ? build.placementAnchor.GetPose() : new Pose(bounds?.center ?? Vector3.zero, Quaternion.identity);
                data.InitialRotation = frame.rotation;
                data.HasHole = build.placementAnchor?.hole == true;
                data.displayMesh = MeshCombiner.CombineMeshes(renderers, frame.position, frame.rotation);
                data.chains = build.chains ?? Array.Empty<ChainData>();
                if (validateChains) data.ValidateChains();
                data.Normalize(frame);
                return data;
            }
            catch { data.Dispose(); throw; }
        }

        private void Normalize(Pose frame) {
            foreach (var template in templates) {
                var position = template.transform.position;
                var rotation = template.transform.rotation;
                template.transform.localPosition = Quaternion.Inverse(frame.rotation) * (position - frame.position);
                template.transform.localRotation = Quaternion.Inverse(frame.rotation) * rotation;
            }
        }

        private void ValidateChains() {
            if (chains.Length == 0) return;
            var parts = templates.SelectMany(template => template.GetComponentsInChildren<SavedObject>(true))
                .GroupBy(saved => saved.ConnectionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var chain in chains) {
                var sprockets = chain.members.Select(id => parts[id].GetComponent<SimulationPart>()).ToList();
                if (!ChainLoop.Compatible(sprockets, out var reason))
                    throw new System.IO.InvalidDataException("The build contains an invalid chain: " + reason + ".");
            }
        }

        // Placement takes ownership of this mesh, just as it does for ordinary parts.
        public override Mesh GetDisplayMesh() {
            var mesh = displayMesh;
            displayMesh = null;
            return mesh;
        }

        public List<GameObject> Generate(Vector3 position, Quaternion rotation) {
            if (disposed) throw new ObjectDisposedException(nameof(SelectionPlacementData));
            var copies = duplicator.CreateCopies(templates);
            foreach (var copy in copies) {
                var localPosition = copy.transform.position;
                var localRotation = copy.transform.rotation;
                copy.transform.SetPositionAndRotation(position + rotation * localPosition, rotation * localRotation);
            }
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < templates.Count; i++) {
                var source = templates[i].GetComponentsInChildren<SavedObject>(true);
                var placed = copies[i].GetComponentsInChildren<SavedObject>(true);
                for (int j = 0; j < source.Length; j++) ids[source[j].ConnectionId] = placed[j].ConnectionId;
            }
            GeneratedChains.Clear();
            foreach (var chain in chains.Where(chain => chain?.members != null && chain.members.All(id => !string.IsNullOrEmpty(id) && ids.ContainsKey(id)))) {
                var loop = ChainLoop.FromData(new ChainData { members = chain.members.Select(id => ids[id]).ToArray() });
                if (loop != null) GeneratedChains.Add(loop.gameObject);
            }
            Physics.SyncTransforms();
            foreach (var detector in copies.SelectMany(copy => copy.GetComponentsInChildren<HoleDetector>())) detector.RefreshOverlaps();
            return copies;
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            ObjectDuplicator.DiscardCopies(templates);
            if (Application.isPlaying) UnityEngine.Object.Destroy(templateRoot);
            else UnityEngine.Object.DestroyImmediate(templateRoot);
            if (displayMesh != null) {
                if (Application.isPlaying) UnityEngine.Object.Destroy(displayMesh);
                else UnityEngine.Object.DestroyImmediate(displayMesh);
            }
        }
    }
}
