using System;
using System.IO;
using System.Linq;
using Parts_List;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protobot {
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class CustomModel : MonoBehaviour {
        public const string PartId = "CUSTOM";
        [SerializeField] private CustomModelData geometry;
        private Mesh ownedMesh;
        private Material ownedMaterial;

        // Unity clones serialized geometry, while every instance owns its runtime mesh/material.
        private void Awake() {
            if (geometry?.vertices != null) BuildGeometry();
        }

        internal void InitializeTemplate() {
            if (ownedMesh == null && geometry?.vertices != null) BuildGeometry();
        }

        public static GameObject Create(CustomModelData data, Vector3 position, Quaternion rotation) {
            Validate(data);
            var obj = new GameObject(data.name, typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            obj.tag = "Object";
            obj.transform.SetPositionAndRotation(position, rotation);
            obj.AddComponent<SavedObject>().id = PartId;
            obj.AddComponent<PartName>().name = data.name + " (custom)";
            var model = obj.AddComponent<CustomModel>();
            model.geometry = new CustomModelData { name = data.name, vertices = data.vertices, triangles = data.triangles };
            model.BuildGeometry();
            foreach (var mount in data.mounts ?? Array.Empty<CustomMountData>()) model.CreateMount(mount);
            OwnedPartMaterials.CaptureDefaults(obj);
            return obj;
        }

        public CustomModelData Snapshot() => new() {
            name = geometry.name,
            // Geometry is immutable after import. Each snapshot gets an independent mount list.
            vertices = geometry.vertices,
            triangles = geometry.triangles,
            mounts = GetComponentsInChildren<CustomMountPoint>().Where(p => p.Owner == this && p.gameObject.activeSelf && !p.IsPreview).Select(p => p.Snapshot()).ToArray()
        };

        internal void ApplyMountLayout(CustomMountData[] mounts) {
            mounts ??= Array.Empty<CustomMountData>();
            Validate(new CustomModelData { name = geometry.name, vertices = geometry.vertices, triangles = geometry.triangles, mounts = mounts });
            // Mount state is per instance. Rebuild only the ordered point children;
            // the content-addressed geometry, mesh and material stay untouched.
            foreach (var point in GetComponentsInChildren<CustomMountPoint>(true).Where(point => point.Owner == this).ToArray()) {
                point.SetSelected(false);
                point.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(point.gameObject); else DestroyImmediate(point.gameObject);
            }
            foreach (var mount in mounts) CreateMount(mount);
        }

        public CustomMountPoint AddMount(Vector3 surface, Vector3 outwardNormal, float diameter, float depth, bool threaded) =>
            AddMount(new Pose(surface, Quaternion.LookRotation(outwardNormal)), diameter, depth, threaded);

        public CustomMountPoint AddMount(Pose face, float diameter, float depth, bool threaded) {
            if (diameter <= 0 || depth <= 0 || diameter > 100 || depth > 100 ||
                float.IsNaN(diameter) || float.IsNaN(depth)) throw new ArgumentOutOfRangeException(nameof(diameter));
            var outwardNormal = face.rotation * Vector3.forward;
            var center = transform.InverseTransformPoint(face.position - outwardNormal * (depth * .5f));
            var rotation = Quaternion.Inverse(transform.rotation) * face.rotation;
            var data = new CustomMountData { x = center.x, y = center.y, z = center.z,
                qx = rotation.x, qy = rotation.y, qz = rotation.z, qw = rotation.w,
                diameter = diameter, depth = depth, threaded = threaded };
            // A repeated click at the same mounting face should not create overlapping detectors.
            foreach (var existing in GetComponentsInChildren<CustomMountPoint>())
                if (existing.Owner == this && !existing.IsPreview && !existing.gameObject.IsDeleted() &&
                    (existing.Surface - face.position).sqrMagnitude < .0001f && Vector3.Dot(existing.transform.forward, outwardNormal) > .99f)
                    return null;
            return CreateMount(data);
        }

        internal CustomMountPoint CopyMount(CustomMountPoint source) {
            var copy = CreateMount(source.Snapshot());
            copy.SetPreview(true);
            return copy;
        }

        private CustomMountPoint CreateMount(CustomMountData data) {
            var obj = new GameObject("Custom mounting point");
            obj.SetActive(false);
            obj.transform.SetParent(transform, false);
            obj.transform.localPosition = new Vector3(data.x, data.y, data.z);
            obj.transform.localRotation = new Quaternion(data.qx, data.qy, data.qz, data.qw);
            // A tiny collider overhang lets the point be picked over the uncut model surface.
            obj.transform.localScale = new Vector3(data.diameter, data.diameter, data.depth + .004f);
            obj.layer = HoleCollider.HOLE_COLLISIONS_LAYER;
            obj.tag = "HoleCollider";
            var point = obj.AddComponent<CustomMountPoint>();
            point.Configure(data);
            obj.AddComponent<MeshCollider>().sharedMesh = HoleShapes.instance.GetShapeMesh("circle");
            var hole = obj.AddComponent<HoleCollider>();
            hole.holeType = data.threaded ? HoleCollider.HoleType.Threaded : HoleCollider.HoleType.Normal;
            hole.twoSided = true;
            point.CreateVisual();
            obj.SetActive(true);
            return point;
        }

        private void BuildGeometry() {
            Validate(geometry);
            ownedMesh = new Mesh { name = geometry.name, indexFormat = IndexFormat.UInt32 };
            var vertices = new Vector3[geometry.vertices.Length / 3];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(geometry.vertices[i * 3], geometry.vertices[i * 3 + 1], geometry.vertices[i * 3 + 2]);
            ownedMesh.vertices = vertices;
            ownedMesh.triangles = geometry.triangles;
            ownedMesh.RecalculateNormals(); ownedMesh.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = ownedMesh;
            GetComponent<MeshCollider>().sharedMesh = ownedMesh;
            var renderer = GetComponent<MeshRenderer>();
            var source = renderer.sharedMaterial != null ? renderer.sharedMaterial : Resources.Load<Material>("Custom Models/Model");
            ownedMaterial = new Material(source);
            renderer.sharedMaterial = ownedMaterial;
        }

        public static void Validate(CustomModelData data) {
            if (data?.vertices == null || data.triangles == null || data.vertices.Length < 9 || data.vertices.Length % 3 != 0 ||
                data.vertices.Length / 3 > CustomModelImporter.MaxVertices || data.triangles.Length == 0 ||
                data.triangles.Length % 3 != 0 || data.triangles.Length / 3 > CustomModelImporter.MaxTriangles)
                throw new InvalidDataException("Invalid embedded custom model geometry.");
            if (data.vertices.Any(v => float.IsNaN(v) || float.IsInfinity(v)) ||
                data.triangles.Any(i => i < 0 || i >= data.vertices.Length / 3)) throw new InvalidDataException("Invalid embedded model coordinates or indices.");
            foreach (var mount in data.mounts ?? Array.Empty<CustomMountData>()) {
                if (mount == null || new[] { mount.x, mount.y, mount.z, mount.qx, mount.qy, mount.qz, mount.qw, mount.diameter, mount.depth }
                    .Any(v => float.IsNaN(v) || float.IsInfinity(v)) || mount.diameter <= 0 || mount.depth <= 0 || mount.diameter > 100 || mount.depth > 100 ||
                    mount.qx * mount.qx + mount.qy * mount.qy + mount.qz * mount.qz + mount.qw * mount.qw < .001f)
                    throw new InvalidDataException("Invalid embedded mounting point.");
            }
        }

        internal void ReleaseGeometry() {
            if (ownedMesh != null) {
                if (Application.isPlaying) Destroy(ownedMesh); else DestroyImmediate(ownedMesh);
                ownedMesh = null;
            }
            if (ownedMaterial != null) {
                if (Application.isPlaying) Destroy(ownedMaterial); else DestroyImmediate(ownedMaterial);
                ownedMaterial = null;
            }
        }

        private void OnDestroy() => ReleaseGeometry();
    }
}
