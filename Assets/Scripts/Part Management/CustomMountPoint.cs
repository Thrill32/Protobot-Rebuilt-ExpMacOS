using Protobot.SelectionSystem;
using Protobot.StateSystems;
using UnityEngine;

namespace Protobot {
    public sealed class CustomMountPoint : MonoBehaviour {
        [SerializeField] private CustomMountData data;
        public float Depth => data.depth;
        public Vector3 Surface => transform.position + transform.forward * (data.depth * .5f);
        public void Configure(CustomMountData value) => data = value;
        public CustomMountData Snapshot() => new() {
            x = data.x, y = data.y, z = data.z, qx = data.qx, qy = data.qy, qz = data.qz, qw = data.qw,
            diameter = data.diameter, depth = data.depth, threaded = data.threaded
        };

        private static Mesh ring;
        public static Mesh RingMesh {
            get {
                if (ring != null) return ring;
                const int segments = 40;
                var vertices = new Vector3[segments * 2];
                var triangles = new int[segments * 12];
                for (int i = 0; i < segments; i++) {
                    var dir = new Vector3(Mathf.Cos(i * Mathf.PI * 2 / segments), Mathf.Sin(i * Mathf.PI * 2 / segments), 0);
                    // Keep the visible ring inside the pickable bore footprint.
                    vertices[i * 2] = dir * .35f; vertices[i * 2 + 1] = dir * .5f;
                    int a = i * 2, b = (i * 2 + 2) % vertices.Length;
                    int[] faces = { a, a + 1, b, b, a + 1, b + 1, b, a + 1, a, b + 1, a + 1, b };
                    System.Array.Copy(faces, 0, triangles, i * 12, 12);
                }
                ring = new Mesh { name = "Custom mounting point ring", vertices = vertices, triangles = triangles };
                ring.RecalculateNormals(); ring.RecalculateBounds();
                return ring;
            }
        }

        public void CreateVisual() {
            foreach (float side in new[] { -1f, 1f }) {
                var visual = new GameObject("Mounting face", typeof(MeshFilter), typeof(MeshRenderer));
                visual.transform.SetParent(transform, false);
                visual.layer = HoleCollider.HOLE_COLLISIONS_LAYER;
                visual.transform.localPosition = Vector3.forward * (side * (data.depth * .5f + .003f) / transform.localScale.z);
                visual.GetComponent<MeshFilter>().sharedMesh = RingMesh;
                visual.GetComponent<MeshRenderer>().sharedMaterial = Resources.Load<Material>("Custom Models/Mount");
            }
        }

        public void RecordAdded() {
            StateSystem.AddElement(new MountElement(this, false));
            SetExists(true);
            StateSystem.AddState(new MountElement(this, true));
        }

        public void Delete(SelectionManager selection) {
            if (selection != null) selection.ClearCurrent(false, true, true);
            StateSystem.AddElement(new MountElement(this, true));
            SetExists(false);
            StateSystem.AddState(new MountElement(this, false));
        }

        private void SetExists(bool exists) {
            if (this == null) return;
            var owner = transform.parent.gameObject;
            var affected = HoleDetector.GetAffectedDetectors(owner);
            gameObject.SetActive(exists);
            HoleDetector.RefreshPart(owner, affected);
        }

        private sealed class MountElement : IElement {
            private readonly CustomMountPoint point;
            private readonly bool exists;
            public MountElement(CustomMountPoint point, bool exists) { this.point = point; this.exists = exists; }
            public void Load() { if (point != null) point.SetExists(exists); }
        }
    }
}
