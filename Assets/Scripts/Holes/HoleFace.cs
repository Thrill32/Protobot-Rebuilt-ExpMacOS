using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Protobot {
    [DefaultExecutionOrder(-50)]
    public class HoleFace : MonoBehaviour {
        public Vector3 direction;
        public Vector3 position => transform.position;
        public Quaternion Rotation => transform.rotation;
        public Quaternion LookRotation => Quaternion.LookRotation(direction, transform.up);
        public HoleData hole;

        [CacheComponent] private MeshFilter meshFilter;

        private Vector3 localDirection;

        private void Update() => Refresh();
        private void LateUpdate() => Refresh();

        public void Refresh() {
            if (hole?.part == null) return;
            direction = hole.part.transform.TransformDirection(localDirection).normalized;
            transform.SetPositionAndRotation(hole.position + direction * (hole.depth / 2f),
                Quaternion.LookRotation(-direction, hole.rotation * Vector3.up));
        }

        public void Set(HoleData newHole, Vector3 newDir) {
            localDirection = newHole.part != null
                ? newHole.part.transform.InverseTransformDirection(newDir) : newDir;
            transform.rotation = Quaternion.LookRotation(-newDir,  newHole.rotation * Vector3.up);
            
            Vector3 newHolePos = newHole.position;

            Vector3 newPos = newHolePos + (newDir * (newHole.depth / 2));
            transform.position = newPos;
            
            direction = newDir;
            
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            meshFilter.mesh = newHole.shape;
            transform.localScale = new Vector3(newHole.size.x, newHole.size.y, 0.001f);

            hole = newHole;
        }

        public void Set(HoleFace newHoleFace) => Set(newHoleFace.hole, newHoleFace.direction);
    }
}