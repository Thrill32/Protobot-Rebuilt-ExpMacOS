using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protobot {
    public static class MeshCombiner {
        // Preview building never changes the source hierarchy or world transforms.
        public static MeshRenderer[] GetRenderers(IEnumerable<GameObject> objects) => objects
            .Where(obj => obj != null)
            .SelectMany(obj => obj.GetComponentsInChildren<MeshRenderer>()).Distinct()
            .Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.gameObject.layer != HoleCollider.HOLE_COLLISIONS_LAYER &&
                renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null).ToArray();

        public static Mesh CombineMeshes(List<GameObject> objects, Vector3 position, Quaternion rotation) =>
            CombineMeshes(GetRenderers(objects), position, rotation);

        public static Mesh CombineMeshes(IEnumerable<MeshRenderer> renderers, Vector3 position, Quaternion rotation) {
            var inverse = Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
            var instances = new List<CombineInstance>();
            foreach (var renderer in renderers.Distinct()) {
                var source = renderer.GetComponent<MeshFilter>().sharedMesh;
                for (int submesh = 0; submesh < source.subMeshCount; submesh++)
                    instances.Add(new CombineInstance { mesh = source, subMeshIndex = submesh,
                        transform = inverse * renderer.localToWorldMatrix });
            }
            var mesh = new Mesh { name = "Move preview geometry", indexFormat = IndexFormat.UInt32 };
            if (instances.Count > 0) mesh.CombineMeshes(instances.ToArray(), true, true);
            return mesh;
        }
    }
}
