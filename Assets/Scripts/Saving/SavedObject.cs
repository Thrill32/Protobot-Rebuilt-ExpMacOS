using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Protobot {
    public class SavedObject : MonoBehaviour {
        public string id;
        public string nameId => id.Split('-')[0];
        public string state;
        public bool hasPaint;
        [SerializeField] private bool mirrored;
        [SerializeField] private List<PartReflection.MeshBinding> mirrorMeshes;
        private Dictionary<Mesh, Mesh> ownedMirrorMeshes;

        public bool Mirrored => mirrored;
        internal Vector3 GeometryLocal(Vector3 value) => mirrored ? PartReflection.Local(value) : value;
        internal Quaternion GeometryLocal(Quaternion value) => mirrored ? PartReflection.Local(value) : value;

        internal void InitializeMirrorMeshes() {
            if (!mirrored || ownedMirrorMeshes != null) return;
            ownedMirrorMeshes = new Dictionary<Mesh, Mesh>();
            foreach (var binding in mirrorMeshes) binding.Apply(ownedMirrorMeshes);
        }

        internal void SetMirrored(bool value) {
            if (mirrored == value) return;
            if (value) mirrorMeshes = new List<PartReflection.MeshBinding>(PartReflection.MeshBinding.Find(transform));
            mirrored = value;
            PartReflection.ReflectChildren(transform);
            if (TryGetComponent<CustomModel>(out var custom)) custom.ReflectMesh();
            if (mirrored) InitializeMirrorMeshes();
            else {
                foreach (var binding in mirrorMeshes) binding.Apply(null);
                mirrorMeshes = null;
                ReleaseMirrorMeshes();
            }
        }

        private void Awake() => InitializeMirrorMeshes();

        internal void ReleaseMirrorMeshes() {
            if (ownedMirrorMeshes == null) return;
            foreach (var mesh in ownedMirrorMeshes.Values) {
                if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
            }
            ownedMirrorMeshes = null;
        }

        private void OnDestroy() => ReleaseMirrorMeshes();

        [NonSerialized] private string connectionId;
        public string ConnectionId {
            get => connectionId ??= Guid.NewGuid().ToString("N");
            set => connectionId = string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString("N") : value;
        }
    }
}
