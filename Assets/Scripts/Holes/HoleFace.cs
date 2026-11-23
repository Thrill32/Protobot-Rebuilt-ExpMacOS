using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Protobot {
    [DefaultExecutionOrder(-50)]
    public class HoleFace : MonoBehaviour {
        public Vector3 direction;
        public Vector3 position => transform.position;
        public Quaternion Rotation => transform.rotation;
        public Quaternion LookRotation => MoveTargeting.HoleFace(hole, direction).rotation;
        public HoleData hole;

        [CacheComponent] private MeshFilter meshFilter;

        private Vector3 localDirection;

        private void Update() => Refresh();
        private void LateUpdate() => Refresh();

        public void Refresh() {
            if (hole?.part == null) return;
            var body = hole.part.GetComponent<SavedObject>();
            direction = hole.part.transform.TransformDirection(body != null ? body.GeometryLocal(localDirection) : localDirection).normalized;
            var face = MoveTargeting.HoleFace(hole, direction);
            transform.SetPositionAndRotation(face.position, MoveTargeting.Oppose(face.rotation));
        }

        public void Set(HoleData newHole, Vector3 newDir) {
            localDirection = newHole.part != null
                ? newHole.part.transform.InverseTransformDirection(newDir) : newDir;
            if (newHole.part != null && newHole.part.TryGetComponent<SavedObject>(out var body))
                localDirection = body.GeometryLocal(localDirection);
            var face = MoveTargeting.HoleFace(newHole, newDir);
            transform.SetPositionAndRotation(face.position, MoveTargeting.Oppose(face.rotation));
            
            direction = newDir;
            
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            meshFilter.mesh = newHole.shape;
            transform.localScale = new Vector3(newHole.size.x, newHole.size.y, 0.001f);

            hole = newHole;
        }

        public void Set(HoleFace newHoleFace) => Set(newHoleFace.hole, newHoleFace.direction);
    }
}
