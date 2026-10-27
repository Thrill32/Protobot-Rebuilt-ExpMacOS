using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Protobot {
    public class HoleCollider : MonoBehaviour {
        public static int HOLE_COLLISIONS_LAYER => 11;
        public static int HOLE_COLLISIONS_MASK => LayerMask.GetMask("HoleCollisions");

        public HoleData holeData;

        public Action<HoleDetector> OnSetDetector;

        public enum HoleType { 
            Normal,
            Threaded,
            Clamp
        }
        
        public HoleType holeType;

        public List<HoleDetector> detectors; // the list of hole detectors attached to this collider

        public bool twoSided = false;
        private int MaxDetectorLimit => twoSided ? 2 : 1;
        
        public bool IsOccupied => detectors.Count >= MaxDetectorLimit;
        public bool IsEmpty => detectors.Count == 0;

        private void Awake() {
            detectors = new List<HoleDetector>();
            MeshCollider meshCollider = GetComponent<MeshCollider>();

            holeData = new HoleData {
                source = transform,
                position = transform.position,
                rotation = transform.rotation,
                forward = transform.forward,

                shape = meshCollider.sharedMesh,
                depth = transform.localScale.z,
                size = new Vector2(transform.localScale.x, transform.localScale.y),
                part = transform.parent.gameObject
            };
        }

        private void Update() {
            holeData.position = transform.position;
            holeData.rotation = transform.rotation;
            holeData.forward = transform.forward;

            RemoveDeletedDetectors();
        }

        public void RemoveDeletedDetectors() {
            var deleteList = detectors.Where(detector => detector == null || detector.gameObject.IsDeleted()).ToList();

            foreach (var detector in deleteList) {
                if (detector != null) detector.RemoveHole(this);
                else detectors.Remove(detector);
            }
        }

        public bool IsOccupiedBy(HoleDetector otherDetector) {
            return detectors.Contains(otherDetector);
        }
        
        public void AddDetector(HoleDetector newDetector) {
            TryAddDetector(newDetector);
        }

        public bool TryAddDetector(HoleDetector newDetector) {
            if (IsOccupiedBy(newDetector)) return true;
            if (IsOccupied) return false;
            detectors.Add(newDetector);
            OnSetDetector?.Invoke(newDetector);
            return true;
        }

        public void RemoveDetector(HoleDetector detector) {
            detectors.Remove(detector);
        }

        private void OnDisable() {
            if (detectors == null) return;
            foreach (var detector in detectors.ToArray())
                if (detector != null) detector.RemoveHole(this);
            detectors.Clear();
        }
    }

    [Serializable]
    public class HoleData {

        public Mesh shape = null;
        public float depth;
        public Vector2 size;
        public GameObject part;

        [NonSerialized] public Transform source;
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("position")] private Vector3 cachedPosition;
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("rotation")] private Quaternion cachedRotation;
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("forward")] private Vector3 cachedForward;
        public Vector3 position { get => source != null ? source.position : cachedPosition; set => cachedPosition = value; }
        public Quaternion rotation { get => source != null ? source.rotation : cachedRotation; set => cachedRotation = value; }
        public Vector3 forward { get => source != null ? source.forward : cachedForward; set => cachedForward = value; }

        public bool IsHighStrength() => size == new Vector2(0.25f, 0.25f) && shape == HoleShapes.instance.GetShapeMesh("square");
    }
}
