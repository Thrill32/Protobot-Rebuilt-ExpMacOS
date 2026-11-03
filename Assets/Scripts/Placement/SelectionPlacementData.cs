using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.SelectionSystem;
using UnityEngine;

namespace Protobot {
    /// <summary>A frozen, repeatable copy of a selection. Templates never enter the live build.</summary>
    public sealed class SelectionPlacementData : PlacementData, IDisposable {
        public override Transform placementTransform { get; set; }
        public override string objectId => "Selection Copy";
        public Quaternion InitialRotation { get; }
        public bool HasHole { get; }
        public int PartCount => templates.Count;

        private readonly GameObject templateRoot;
        private readonly List<GameObject> templates;
        private readonly ObjectDuplicator duplicator;
        private Mesh displayMesh;
        private bool disposed;

        public static SelectionPlacementData Capture(ISelection selection, Transform preview, ObjectDuplicator duplicator) {
            if (selection?.gameObject == null || duplicator == null) return null;
            var bodies = selection.gameObject.GetConnectedObjects(true, true, !selection.individual)
                .Where(obj => obj != null && obj.activeInHierarchy && !obj.IsDeleted())
                .SelectMany(obj => obj.GetComponentsInChildren<SavedObject>())
                .Select(saved => saved.gameObject).Distinct().ToList();
            if (bodies.Count == 0) return null;
            var anchor = selection is HoleSelection hole ? SelectedHoleAnchor.From(hole) : (selection as MultiSelection)?.placementHole;
            if (anchor?.Hole?.part == null || !bodies.Contains(anchor.Hole.part)) anchor = null;
            var frame = anchor != null ? anchor.Face : new Pose(selection.gameObject.transform.position, selection.gameObject.transform.rotation);
            return new SelectionPlacementData(bodies, frame, anchor != null, preview, duplicator);
        }

        private SelectionPlacementData(List<GameObject> bodies, Pose frame, bool hasHole, Transform preview, ObjectDuplicator duplicator) {
            placementTransform = preview;
            InitialRotation = frame.rotation;
            HasHole = hasHole;
            this.duplicator = duplicator;
            displayMesh = MeshCombiner.CombineMeshes(bodies, frame.position, frame.rotation);
            templateRoot = new GameObject("Selection placement templates");
            templateRoot.SetActive(false);
            templates = duplicator.CreateCopies(bodies, templateRoot.transform);
            foreach (var template in templates) {
                var position = template.transform.position;
                var rotation = template.transform.rotation;
                template.transform.localPosition = Quaternion.Inverse(frame.rotation) * (position - frame.position);
                template.transform.localRotation = Quaternion.Inverse(frame.rotation) * rotation;
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
