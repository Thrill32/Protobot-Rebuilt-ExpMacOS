using System.Linq;
using UnityEngine;

namespace Protobot {
    /// <summary>A mounting face's first point anchors its quarter-inch hole layout.</summary>
    public static class CustomMountGrid {
        public const float Spacing = .25f;
        public static Pose PlacementPose(CustomModel model, Vector3 surface, Vector3 normal, bool snap, out bool onGrid) {
            normal.Normalize();
            var reference = model.GetComponentsInChildren<CustomMountPoint>().FirstOrDefault(point =>
                point.Owner == model && !point.IsPreview && !point.gameObject.IsDeleted() &&
                Mathf.Abs(Vector3.Dot(point.transform.forward, normal)) > .99999f &&
                Mathf.Abs(Vector3.Dot(surface - point.Surface, normal)) < .001f);
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
