using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Protobot {
    /// <summary>Releases only runtime materials explicitly owned by a copied part.</summary>
    public sealed class OwnedPartMaterials : MonoBehaviour {
        // Never copy ownership references when Unity clones a part's components.
        [NonSerialized] private readonly HashSet<Material> materials = new();

        public static void CloneForCopy(GameObject part) {
            if (part.GetComponent<CustomModel>() != null) return;
            var owner = part.GetComponent<OwnedPartMaterials>() ?? part.AddComponent<OwnedPartMaterials>();
            foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = renderer.sharedMaterials.Select(source => {
                    if (source == null) return null;
                    var material = new Material(source);
                    owner.materials.Add(material);
                    return material;
                }).ToArray();
            // Establish Unity's renderer instances now, before later color/save
            // code reads .material and creates an otherwise untracked instance.
            CaptureGenerated(part);
        }

        // Freshly generated build templates still reference prefab materials,
        // especially wheel tires/rollers in child renderers. Unity's materials
        // getter instances every slot; existing root color instances are retained.
        public static void CaptureGenerated(GameObject part) {
            if (part.GetComponent<CustomModel>() != null) return;
            var owner = part.GetComponent<OwnedPartMaterials>() ?? part.AddComponent<OwnedPartMaterials>();
            foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.materials)
                    if (material != null) owner.materials.Add(material);
        }

        public void Release() {
            foreach (var material in materials) {
                if (material == null) continue;
                if (Application.isPlaying) Destroy(material);
                else DestroyImmediate(material);
            }
            materials.Clear();
        }
        private void OnDestroy() => Release();
    }
}
