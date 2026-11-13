using System.Linq;
using Protobot.StateSystems;
using UnityEngine;

namespace Protobot {
    /// <summary>A mounting face's first point anchors its quarter-inch hole layout.</summary>
    public static class CustomMountGrid {
        public const float Spacing = .25f;
        private static CustomMountPoint Reference(CustomModel model, Vector3 surface, Vector3 normal) =>
            model.GetComponentsInChildren<CustomMountPoint>().FirstOrDefault(point =>
                point.Owner == model && !point.IsPreview && !point.gameObject.IsDeleted() &&
                Mathf.Abs(Vector3.Dot(point.transform.forward, normal)) > .99999f &&
                Mathf.Abs(Vector3.Dot(surface - point.Surface, normal)) < .001f);

        public static bool Recalibrate(CustomMountPoint point, bool recordState = true) {
            if (point == null || point.Owner == null || point.IsPreview || !point.gameObject.activeInHierarchy || point.gameObject.IsDeleted() ||
                Reference(point.Owner, point.Surface, point.transform.forward) == point) return false;
            // The first active point on each plane defines its frame. Snapshot/load
            // and whole-model copies retain child order, so no file-format change
            // is needed to persist an explicitly chosen reference.
            if (recordState) StateSystem.AddElement(new GridOrderElement(point, point.transform.GetSiblingIndex()));
            point.transform.SetAsFirstSibling();
            if (recordState) StateSystem.AddState(new GridOrderElement(point, 0));
            return true;
        }

        private sealed class GridOrderElement : IElement {
            private readonly CustomMountPoint point;
            private readonly int index;
            public GridOrderElement(CustomMountPoint point, int index) { this.point = point; this.index = index; }
            public void Load() { if (point != null) point.transform.SetSiblingIndex(index); }
        }

        public static Pose PlacementPose(CustomModel model, Vector3 surface, Vector3 normal, bool snap, out bool onGrid) {
            normal.Normalize();
            var reference = Reference(model, surface, normal);
            onGrid = snap && reference != null;
            if (onGrid) {
                var offset = surface - reference.Surface;
                var right = reference.transform.right; var up = reference.transform.up;
                float pitch = Spacing;
                var snapped = reference.Surface + right * (Mathf.Round(Vector3.Dot(offset, right) / pitch) * pitch)
                    + up * (Mathf.Round(Vector3.Dot(offset, up) / pitch) * pitch);
                // Preserve the clicked face's depth through small mesh/pose noise.
                surface = snapped + normal * Vector3.Dot(surface - snapped, normal);
            }
            // Keep the frame with the model as it moves. A first point on a new
            // face follows the model's axes; later points inherit its grid roll.
            var tangent = reference != null ? reference.transform.right : model.transform.right;
            if (Mathf.Abs(Vector3.Dot(tangent, normal)) > .9f) tangent = model.transform.up;
            var x = Vector3.ProjectOnPlane(tangent, normal).normalized;
            return new Pose(surface, Quaternion.LookRotation(normal, Vector3.Cross(normal, x)));
        }
    }
}
