using System.Collections.Generic;
using UnityEngine;

namespace Protobot {
    // World reflection S is represented by a proper rotation S R F and a
    // local reflection F across X. Conjugating each child frame by F keeps all
    // hierarchy scales positive, including under selection and movement pivots.
    internal static class PartReflection {
        internal static Vector3 Local(Vector3 value) => new(-value.x, value.y, value.z);
        internal static Quaternion Local(Quaternion value) => new(value.x, -value.y, -value.z, value.w);

        internal static Pose Across(Pose pose, Vector3 pivot, Vector3 axis) => new(
            pivot + Vector3.Reflect(pose.position - pivot, axis),
            Quaternion.LookRotation(Vector3.Reflect(pose.rotation * Vector3.forward, axis),
                Vector3.Reflect(pose.rotation * Vector3.up, axis)));

        internal static void ReflectChildren(Transform body) {
            if (body.TryGetComponent<Simulation.SimulationPart>(out var mechanism)) mechanism.ReflectLocalGeometry();
            if (body.TryGetComponent<Simulation.RopeGroove>(out var groove)) groove.ReflectLocalGeometry();
            foreach (Transform child in body) {
                if (child.TryGetComponent<SavedObject>(out _)) continue;
                child.SetLocalPositionAndRotation(Local(child.localPosition), Local(child.localRotation));
                ReflectChildren(child);
            }
            foreach (var collider in body.GetComponents<Collider>()) {
                if (collider is BoxCollider box) box.center = Local(box.center);
                else if (collider is SphereCollider sphere) sphere.center = Local(sphere.center);
                else if (collider is CapsuleCollider capsule) capsule.center = Local(capsule.center);
            }
        }

        internal static void ReflectMesh(Mesh mesh) {
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Local(vertices[i]);
            mesh.vertices = vertices;
            var normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++) normals[i] = Local(normals[i]);
            if (normals.Length > 0) mesh.normals = normals;
            var tangents = mesh.tangents;
            for (int i = 0; i < tangents.Length; i++) {
                tangents[i].x = -tangents[i].x;
                tangents[i].w = -tangents[i].w;
            }
            if (tangents.Length > 0) mesh.tangents = tangents;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(submesh, false);
                for (int i = 0; i < indices.Length; i += 3)
                    (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                mesh.SetIndices(indices, MeshTopology.Triangles, submesh, false, (int)mesh.GetBaseVertex(submesh));
            }
            mesh.RecalculateBounds();
        }

        [System.Serializable]
        internal sealed class MeshBinding {
            [SerializeField] private Component target;
            [SerializeField] private Mesh original;

            private MeshBinding(Component target, Mesh original) {
                this.target = target;
                this.original = original;
            }

            internal static IEnumerable<MeshBinding> Find(Transform body) {
                // Catalog bores and custom mounting rings are symmetric in X.
                if (body.TryGetComponent<HoleCollider>(out _)) yield break;
                if (!body.TryGetComponent<CustomModel>(out _)) {
                    foreach (var filter in body.GetComponents<MeshFilter>())
                        if (filter.sharedMesh != null) yield return new MeshBinding(filter, filter.sharedMesh);
                    foreach (var collider in body.GetComponents<MeshCollider>())
                        if (collider.sharedMesh != null) yield return new MeshBinding(collider, collider.sharedMesh);
                }
                foreach (Transform child in body) {
                    if (child.TryGetComponent<SavedObject>(out _)) continue;
                    foreach (var binding in Find(child)) yield return binding;
                }
            }

            internal void Apply(Dictionary<Mesh, Mesh> reflected) {
                if (target == null) return;
                var mesh = original;
                if (reflected != null && !reflected.TryGetValue(original, out mesh)) {
                    mesh = Object.Instantiate(original);
                    ReflectMesh(mesh);
                    reflected.Add(original, mesh);
                }
                if (target is MeshFilter filter) filter.sharedMesh = mesh;
                else ((MeshCollider)target).sharedMesh = mesh;
            }
        }
    }
}
