using System.Linq;
using UnityEngine;

namespace Protobot {
    /// <summary>Body colors shared by painting, picking, regeneration and persistence.</summary>
    public static class PartColors {
        public static GameObject BodyOf(GameObject target) {
            if (target == null) return null;
            if (target.TryGetComponent<HoleFace>(out var face)) return face.hole?.part;
            if (target.TryGetComponent<HoleCollider>(out var hole)) return hole.holeData?.part;
            return target.GetComponentInParent<SavedObject>()?.gameObject;
        }

        internal static MeshRenderer[] Renderers(GameObject part) {
            if (part == null) return System.Array.Empty<MeshRenderer>();
            // Disabled renderers can be temporarily hidden by Move previews;
            // saving or regenerating a part must still retain their appearance.
            return part.GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => r.gameObject.layer != HoleCollider.HOLE_COLLISIONS_LAYER &&
                    r.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null &&
                    r.GetComponentInParent<SavedObject>()?.gameObject == part &&
                    r.GetComponentInParent<HoleCollider>() == null && r.GetComponentInParent<HoleFace>() == null).ToArray();
        }

        public static Material[] Materials(GameObject part) => Renderers(part)
            .SelectMany(r => r.sharedMaterials).Where(CanColor).Distinct().ToArray();

        public static Material SampleMaterial(GameObject target) {
            var materials = Materials(BodyOf(target));
            // Prefer the clicked surface on a composite part; holes use its body.
            if (target != null && target.TryGetComponent<Renderer>(out var renderer)) {
                var surface = renderer.sharedMaterials.FirstOrDefault(m => materials.Contains(m));
                if (surface != null) return surface;
            }
            return materials.FirstOrDefault();
        }

        public static Color GetColor(GameObject part) => SampleMaterial(part)?.color ?? Color.white;
        public static bool SameRGB(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b;

        public static bool IsDefault(GameObject part) => OwnedPartMaterials.CaptureDefaults(part)
            .DefaultColors().All(entry => entry.material.color == entry.color);

        public static void RestoreDefaults(GameObject part) {
            OwnedPartMaterials.CaptureGenerated(part);
            foreach (var entry in part.GetComponent<OwnedPartMaterials>().DefaultColors()) entry.material.color = entry.color;
            part.GetComponent<SavedObject>().hasPaint = false;
        }

        public static void ApplyPaint(GameObject part, Color color) {
            // Instance every ordinary material slot before mutation and register
            // ownership. CustomModel already owns its body's material.
            OwnedPartMaterials.CaptureGenerated(part);
            foreach (var material in Materials(part)) {
                var tint = material.color;
                tint.r = color.r; tint.g = color.g; tint.b = color.b;
                material.color = tint;
            }
            part.GetComponent<SavedObject>().hasPaint = true;
        }

        internal static bool CanColor(Material material) => material != null &&
            (material.HasProperty("_BaseColor") || material.HasProperty("_Color"));
    }
}
