using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Parts_List;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protobot {
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class CustomModel : MonoBehaviour {
        public const string PartId = "CUSTOM";
        [SerializeField] private CustomModelData geometry;
        private Mesh ownedMesh;
        private Material[] ownedMaterials;
        private Texture2D[] ownedTextures;
        private long appearanceBytes;
        private static long liveAppearanceBytes;
        private const long MaxLiveAppearanceBytes = 384L * 1024 * 1024;

        internal bool HasImportedAppearance => geometry?.appearance != null;

        internal static long AppearanceCost(CustomModelData data) => data?.appearance == null ? 0 :
            data.appearance.ResourceBytes(data.vertices.Length, data.triangles.Length);

        internal static void RequireAppearanceCapacity(long bytes) {
            if (bytes > MaxLiveAppearanceBytes - liveAppearanceBytes)
                throw new InvalidDataException("Too many custom-model rendering resources are in use.");
        }

        internal long CurrentAppearanceCost => appearanceBytes;

        // Unity clones serialized geometry, while every instance owns its runtime mesh/material.
        private void Awake() {
            if (ownedMesh == null && geometry?.vertices != null) BuildGeometry();
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
            model.geometry = new CustomModelData { name = data.name, vertices = data.vertices, triangles = data.triangles, appearance = data.appearance };
            try {
                model.BuildGeometry();
                foreach (var mount in data.mounts ?? Array.Empty<CustomMountData>()) model.CreateMount(mount);
                OwnedPartMaterials.CaptureDefaults(obj);
                return obj;
            }
            catch {
                model.ReleaseGeometry();
                obj.SetActive(false);
                DestroyOwned(obj);
                throw;
            }
        }

        public CustomModelData Snapshot() => new() {
            name = geometry.name,
            // Geometry is immutable after import. Each snapshot gets an independent mount list.
            vertices = geometry.vertices,
            triangles = geometry.triangles,
            appearance = geometry.appearance,
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

        internal static void DecodeSourceImages(CustomAppearance appearance, List<byte[]> images) {
            for (int i = 0; i < images.Count; i++) {
                var target = appearance.textures[i];
                var staging = new Texture2D(2, 2, TextureFormat.RGB24, false);
                try {
                    if (!ImageConversion.LoadImage(staging, images[i]) || staging.width != target.width || staging.height != target.height)
                        throw new InvalidDataException("A diffuse image could not be decoded at its declared size.");
                    var pixels = staging.GetPixels32();
                    var rgb = new byte[target.RgbBytes];
                    for (int j = 0; j < pixels.Length; j++) {
                        rgb[j * 3] = pixels[j].r; rgb[j * 3 + 1] = pixels[j].g; rgb[j * 3 + 2] = pixels[j].b;
                    }
                    target.SetPixels(rgb);
                }
                finally { DestroyOwned(staging); }
            }
        }

        private void BuildGeometry() {
            Validate(geometry);
            var appearance = geometry.appearance;
            long bytes = AppearanceCost(geometry);
            RequireAppearanceCapacity(bytes);
            appearanceBytes = bytes;
            liveAppearanceBytes += bytes;

            var renderer = GetComponent<MeshRenderer>();
            var inherited = renderer.sharedMaterial;
            bool painted = GetComponent<SavedObject>().hasPaint;
            var inheritedColor = inherited != null ? inherited.color : Color.white;
            var source = Resources.Load<Material>("Custom Models/Model");
            try {
                ownedMesh = new Mesh { name = geometry.name, indexFormat = IndexFormat.UInt32 };
                var vertices = new Vector3[geometry.vertices.Length / 3];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(geometry.vertices[i * 3], geometry.vertices[i * 3 + 1], geometry.vertices[i * 3 + 2]);
                ownedMesh.vertices = vertices;
                if (appearance == null) {
                    ownedMesh.triangles = geometry.triangles;
                    ownedMaterials = new[] { new Material(inherited != null ? inherited : source) };
                }
                else {
                    if (appearance.uv.Length > 0) {
                        var uv = new Vector2[vertices.Length];
                        for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(appearance.uv[i * 2], appearance.uv[i * 2 + 1]);
                        ownedMesh.uv = uv;
                    }
                    var counts = new int[appearance.materials.Length];
                    foreach (int slot in appearance.faceMaterials) counts[slot] += 3;
                    var indices = counts.Select(count => new int[count]).ToArray();
                    Array.Clear(counts, 0, counts.Length);
                    for (int i = 0; i < appearance.faceMaterials.Length; i++) {
                        int slot = appearance.faceMaterials[i];
                        Array.Copy(geometry.triangles, i * 3, indices[slot], counts[slot], 3);
                        counts[slot] += 3;
                    }
                    ownedMesh.subMeshCount = counts.Length;
                    for (int i = 0; i < counts.Length; i++) ownedMesh.SetTriangles(indices[i], i, false);

                    ownedTextures = new Texture2D[appearance.textures.Length];
                    for (int i = 0; i < ownedTextures.Length; i++) {
                        var data = appearance.textures[i];
                        var texture = new Texture2D(data.width, data.height, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Repeat };
                        ownedTextures[i] = texture;
                        texture.LoadRawTextureData(data.GetPixels());
                        texture.Apply(false, true);
                    }
                    ownedMaterials = new Material[counts.Length];
                    for (int i = 0; i < counts.Length; i++)
                        ownedMaterials[i] = new Material(source) { name = "Imported diffuse", };
                    foreach (var material in ownedMaterials) material.SetFloat("_Metallic", 0);
                    ApplyImportedAppearance(painted, inheritedColor);
                }
                ownedMesh.RecalculateNormals(); ownedMesh.RecalculateBounds();
                GetComponent<MeshFilter>().sharedMesh = ownedMesh;
                GetComponent<MeshCollider>().sharedMesh = ownedMesh;
                renderer.sharedMaterials = ownedMaterials;
            }
            catch { ReleaseGeometry(); throw; }
        }

        internal void ApplyImportedAppearance(bool painted, Color paint) {
            var fallback = Resources.Load<Material>("Custom Models/Model").color;
            var defaults = geometry.appearance.materials;
            for (int i = 0; i < defaults.Length; i++) {
                var data = defaults[i];
                var material = ownedMaterials[i];
                Color color = data.hasColor ? new Color(data.r, data.g, data.b, fallback.a) : fallback;
                if (data.texture >= 0 && !data.hasColor) color = new Color(1, 1, 1, fallback.a);
                if (painted) color = new Color(paint.r, paint.g, paint.b, fallback.a);
                material.color = color;
                material.mainTexture = painted || data.texture < 0 ? null : ownedTextures[data.texture];
            }
            GetComponent<SavedObject>().hasPaint = painted;
        }

        public static void Validate(CustomModelData data) {
            if (data?.vertices == null || data.triangles == null || data.vertices.Length < 9 || data.vertices.Length % 3 != 0 ||
                data.vertices.Length / 3 > CustomModelImporter.MaxVertices || data.triangles.Length == 0 ||
                data.triangles.Length % 3 != 0 || data.triangles.Length / 3 > CustomModelImporter.MaxTriangles)
                throw new InvalidDataException("Invalid embedded custom model geometry.");
            if (data.vertices.Any(v => float.IsNaN(v) || float.IsInfinity(v)) ||
                data.triangles.Any(i => i < 0 || i >= data.vertices.Length / 3)) throw new InvalidDataException("Invalid embedded model coordinates or indices.");
            data.appearance?.Validate(data.vertices.Length / 3, data.triangles.Length / 3);
            foreach (var mount in data.mounts ?? Array.Empty<CustomMountData>()) {
                if (mount == null || new[] { mount.x, mount.y, mount.z, mount.qx, mount.qy, mount.qz, mount.qw, mount.diameter, mount.depth }
                    .Any(v => float.IsNaN(v) || float.IsInfinity(v)) || mount.diameter <= 0 || mount.depth <= 0 || mount.diameter > 100 || mount.depth > 100 ||
                    mount.qx * mount.qx + mount.qy * mount.qy + mount.qz * mount.qz + mount.qw * mount.qw < .001f)
                    throw new InvalidDataException("Invalid embedded mounting point.");
            }
        }

        internal bool IsDeletedAppearance => HasImportedAppearance && !gameObject.activeInHierarchy && gameObject.IsDeleted();

        internal void DiscardDeletedAppearance() {
            if (!IsDeletedAppearance) return;
            ReleaseGeometry();
            DestroyOwned(gameObject);
        }

        internal void ReleaseGeometry() {
            DestroyOwned(ownedMesh);
            ownedMesh = null;
            if (ownedMaterials != null) foreach (var material in ownedMaterials) DestroyOwned(material);
            ownedMaterials = null;
            if (ownedTextures != null) foreach (var texture in ownedTextures) DestroyOwned(texture);
            ownedTextures = null;
            liveAppearanceBytes -= appearanceBytes;
            appearanceBytes = 0;
        }

        private static void DestroyOwned(UnityEngine.Object value) {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        private void OnDestroy() => ReleaseGeometry();
    }
}
