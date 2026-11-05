using Protobot.SelectionSystem;
using Protobot.StateSystems;
using UnityEngine;

namespace Protobot {
    public sealed class CustomMountPoint : MonoBehaviour {
        [SerializeField] private CustomMountData data;
        [SerializeField] private CustomModel owner;
        public CustomModel Owner => owner != null ? owner : owner = GetComponentInParent<CustomModel>();
        public bool IsPreview { get; private set; }
        public float Diameter => data.diameter;
        public float Depth => data.depth;
        public bool Threaded => data.threaded;
        public Vector3 Surface => transform.position + transform.forward * (data.depth * .5f);
        public Pose LocalPose => transform.parent == Owner.transform ? new Pose(transform.localPosition, transform.localRotation)
            : new Pose(Owner.transform.InverseTransformPoint(transform.position), Quaternion.Inverse(Owner.transform.rotation) * transform.rotation);
        public void Configure(CustomMountData value) { data = value; owner = GetComponentInParent<CustomModel>(); }
        public CustomMountData Snapshot() {
            var pose = CustomModelTool.SavedMountPose(this) ?? LocalPose;
            return new CustomMountData {
                x = pose.position.x, y = pose.position.y, z = pose.position.z,
                qx = pose.rotation.x, qy = pose.rotation.y, qz = pose.rotation.z, qw = pose.rotation.w,
                diameter = data.diameter, depth = data.depth, threaded = data.threaded
            };
        }

        public void ApplyLocalPose(Pose pose, System.Collections.Generic.HashSet<HoleDetector> affected = null) {
            if (Owner == null) return;
            affected ??= HoleDetector.GetAffectedDetectors(Owner.gameObject);
            transform.SetParent(Owner.transform, false);
            transform.localPosition = pose.position; transform.localRotation = pose.rotation;
            gameObject.layer = HoleCollider.HOLE_COLLISIONS_LAYER;
            HoleDetector.RefreshPart(Owner.gameObject, affected);
        }

        public void RecordPose(Pose before, Pose after) {
            if (Vector3.Distance(before.position, after.position) < .000001f && Quaternion.Angle(before.rotation, after.rotation) < .0001f) return;
            StateSystem.AddElement(new PoseElement(this, before));
            StateSystem.AddState(new PoseElement(this, after));
        }

        public void SetSelected(bool selected) {
            var block = selected ? new MaterialPropertyBlock() : null;
            if (block != null) { block.SetColor("_BaseColor", new Color(1, .75f, .1f)); block.SetColor("_Color", new Color(1, .75f, .1f)); }
            foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true)) renderer.SetPropertyBlock(block);
        }

        internal void SetPreview(bool preview) {
            IsPreview = preview;
            GetComponent<Collider>().enabled = !preview;
            GetComponent<HoleCollider>().enabled = !preview;
        }

        // Pick either mounting face as a disk, including its empty center.
        // No extra physics collider is added to fastener overlap detection.
        public static CustomMountPoint Pick(Ray ray) {
            CustomMountPoint best = null; float distance = float.PositiveInfinity;
            foreach (var point in FindObjectsByType<CustomMountPoint>(FindObjectsSortMode.None)) {
                if (point.Owner == null || point.IsPreview || point.gameObject.IsDeleted()) continue;
                foreach (float side in new[] { -1f, 1f }) {
                    var center = point.transform.position + point.transform.forward * (side * (point.Depth * .5f + .004f));
                    var plane = new Plane(point.transform.forward, center);
                    if (!plane.Raycast(ray, out float hit) || hit >= distance) continue;
                    float radius = point.data.diameter * .5f;
                    if ((ray.GetPoint(hit) - center).sqrMagnitude > radius * radius) continue;
                    if (Physics.Raycast(ray, out var obstruction, hit, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore) && obstruction.distance < hit - .01f) continue;
                    best = point; distance = hit;
                }
            }
            return best;
        }

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

        internal IElement PoseState(Pose pose) => new PoseElement(this, pose);
        internal IElement ExistenceState(bool exists) => new MountElement(this, exists);

        internal void SetExists(bool exists) {
            if (this == null) return;
            var owner = transform.parent.gameObject;
            var affected = HoleDetector.GetAffectedDetectors(owner);
            gameObject.SetActive(exists);
            HoleDetector.RefreshPart(owner, affected);
        }

        private void OnEnable() => SetSelected(false);

        private sealed class PoseElement : IElement {
            private readonly CustomMountPoint point;
            private readonly Pose pose;
            public PoseElement(CustomMountPoint point, Pose pose) { this.point = point; this.pose = pose; }
            public void Load() { if (point != null) point.ApplyLocalPose(pose); }
        }

        private sealed class MountElement : IElement {
            private readonly CustomMountPoint point;
            private readonly bool exists;
            public MountElement(CustomMountPoint point, bool exists) { this.point = point; this.exists = exists; }
            public void Load() { if (point != null) point.SetExists(exists); }
        }
    }
}
