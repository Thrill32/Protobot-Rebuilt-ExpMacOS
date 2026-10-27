using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections;
using UnityEngine;
using Protobot.StateSystems;

namespace Protobot {
    public class HoleDetector : MonoBehaviour {
        public HashSet<HoleCollider> holes = new HashSet<HoleCollider>();
        [SerializeField] HoleCollider.HoleType targetHoleType;

        private void Update() {
            var allHoles = new HashSet<HoleCollider>(holes);
            foreach (var holeCollider in allHoles) {
                if (!Usable(holeCollider))
                    RemoveHole(holeCollider);
            }
        }

        public bool TargetHoleFound => TargetHoleCount > 0;
        public int TargetHoleCount => holes.Count(hole => Usable(hole) && IsTargetHole(hole));

        public Action OnAddHole; // Runs whenever any hole gets added to this hole detector
        public Action OnAddTargetHole; // Runs when a target hole is added to this hole detector
        public Action OnRemoveHole; // Runs whenever any hole gets removed from this hole detector
        public Action OnRemoveTargetHole; // Runs when a target hole is added to this hole detector
        
        private static bool Usable(HoleCollider hole) => hole != null && hole.isActiveAndEnabled &&
            hole.holeData?.part != null && !hole.gameObject.IsDeleted();

        private void OnDisable() {
            StopAllCoroutines();
            RemoveAll();
        }
        
        /// <summary>
        /// Makes sure to wait until all holes have been added before deciding to add a target hole
        /// Due to the randomness of OnTriggerStay
        /// </summary>
        /// <returns></returns>
        IEnumerator WaitAddTargetHole() {
            yield return new WaitForEndOfFrame();
            if (TargetHoleFound) OnAddTargetHole?.Invoke();
        }

        public List<GameObject> GetObjects() {
            return holes.Where(Usable).Select(col => col.holeData.part).Distinct().ToList();
        }

        /// <summary>
        /// Returns the list of holes detected in order from distance to the connecting part
        /// </summary>
        /// <returns></returns>
        public List<HoleCollider> GetOrderedHoles() {
            return holes.Where(Usable).OrderBy(x => Mathf.Abs(Vector3.Dot(x.transform.position - transform.parent.position, transform.parent.forward)))
                        .ToList();
        }

        public void RemoveAll() {
            var allHoles = new List<HoleCollider>(holes);
            
            foreach (HoleCollider hole in allHoles) {
                RemoveHole(hole);
            }
        }

        /// <summary>Re-query stationary geometry after regeneration instead of
        /// waiting for trigger callbacks from a moving/woken rigidbody.</summary>
        public void RefreshOverlaps() {
            if (!isActiveAndEnabled) return;
            var capsule = GetComponent<CapsuleCollider>();
            var scale = transform.lossyScale;
            float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float halfSegment = Mathf.Max(0, capsule.height * Mathf.Abs(scale.y) * .5f - radius);
            var center = transform.TransformPoint(capsule.center);
            var overlaps = Physics.OverlapCapsule(center - transform.up * halfSegment,
                center + transform.up * halfSegment, radius, HoleCollider.HOLE_COLLISIONS_MASK,
                QueryTriggerInteraction.Collide).Select(c => c.GetComponent<HoleCollider>())
                .Where(hole => hole != null).ToHashSet();
            foreach (var hole in holes.ToArray())
                if (!overlaps.Contains(hole) || !Usable(hole)) RemoveHole(hole);
            foreach (var hole in overlaps) DetectHole(hole);
            GetComponentInParent<ConnectingPart>()?.FormConnection();
        }

        public static HashSet<HoleDetector> GetAffectedDetectors(params GameObject[] parts) {
            var result = new HashSet<HoleDetector>();
            foreach (var part in parts.Where(part => part != null)) {
                result.UnionWith(part.GetComponentsInChildren<HoleDetector>(true));
                foreach (var hole in part.GetComponentsInChildren<HoleCollider>(true))
                    if (hole.detectors != null) result.UnionWith(hole.detectors);
            }
            return result;
        }

        public static void RefreshPart(GameObject part, HashSet<HoleDetector> affected) {
            // Existence and transforms have just changed; queries must see both.
            Physics.SyncTransforms();
            affected.UnionWith(part.GetComponentsInChildren<HoleDetector>());
            foreach (var hole in part.GetComponentsInChildren<HoleCollider>()) {
                var bounds = hole.GetComponent<Collider>().bounds;
                foreach (var collider in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity,
                    HoleCollider.HOLE_COLLISIONS_MASK, QueryTriggerInteraction.Collide))
                    if (collider.TryGetComponent<HoleDetector>(out var detector)) affected.Add(detector);
            }
            foreach (var detector in affected)
                if (detector != null && detector.isActiveAndEnabled) detector.RefreshOverlaps();
        }

        //Returns true if a hole is intersecting any other hole current detected by holeDetector
        public bool IsHoleIntersecting(HoleCollider otherHole) {
            if (!holes.Contains(otherHole)) {
                var otherHoleBounds = otherHole.GetComponent<MeshCollider>().bounds;
                otherHoleBounds.Expand(-0.01f);

                foreach (HoleCollider hole in holes.Where(Usable)) {
                    var holeBounds = hole.GetComponent<MeshCollider>().bounds;
                    // Aligned holes can have overlapping world AABBs after
                    // rotating an assembly despite clearance along the shaft.
                    // Only reject them if their axial interiors overlap too.
                    if (holeBounds.Intersects(otherHoleBounds) && AxialInteriorsOverlap(hole, otherHole))
                        return true;
                }
            }

            return false;
        }

        private bool AxialInteriorsOverlap(HoleCollider first, HoleCollider second) {
            var a = AxialInterval(first);
            var b = AxialInterval(second);
            return Mathf.Min(a.y, b.y) - Mathf.Max(a.x, b.x) > .005f;
        }

        private Vector2 AxialInterval(HoleCollider hole) {
            var bounds = hole.GetComponent<MeshCollider>().sharedMesh.bounds;
            var source = hole.transform;
            var axis = transform.up;
            float center = Vector3.Dot(source.TransformPoint(bounds.center) - transform.position, axis);
            float radius = Mathf.Abs(Vector3.Dot(source.TransformVector(Vector3.right), axis)) * bounds.extents.x +
                Mathf.Abs(Vector3.Dot(source.TransformVector(Vector3.up), axis)) * bounds.extents.y +
                Mathf.Abs(Vector3.Dot(source.TransformVector(Vector3.forward), axis)) * bounds.extents.z;
            return new Vector2(center - radius, center + radius);
        }

        private void OnTriggerStay(Collider other) {
            if (other.TryGetComponent(out HoleCollider hole))
                DetectHole(hole);
        }

        private void DetectHole(HoleCollider hole) {
            if (Usable(hole) && hole.holeData.part != transform.parent.gameObject &&
                Mathf.Abs(Vector3.Dot(hole.transform.forward, transform.up)) > .98f &&
                (hole.IsOccupiedBy(this) || !hole.IsOccupied) && !IsHoleIntersecting(hole))
                AddHole(hole);
        }

        private void OnTriggerExit(Collider other) {
            if (other.TryGetComponent(out HoleCollider hole))
                if (hole.IsOccupiedBy(this))
                    RemoveHole(hole);
        }

        public void RemoveHole(HoleCollider hole) {
            if (!holes.Remove(hole)) return;
            if (hole != null) hole.RemoveDetector(this);

            if (hole != null && IsTargetHole(hole)) {
                OnRemoveTargetHole?.Invoke();
            }
            
            OnRemoveHole?.Invoke();
        }

        public bool IsTargetHole(HoleCollider hole) => hole.holeType == targetHoleType;

        public void AddHole(HoleCollider hole) {
            if (!isActiveAndEnabled || !Usable(hole) || holes.Contains(hole) || !hole.TryAddDetector(this)) return;
            holes.Add(hole);

            if (IsTargetHole(hole)) {
                StartCoroutine(WaitAddTargetHole());
            }
            
            OnAddHole?.Invoke();
        }

        public static HoleDetector Create(Transform parent, float length, HoleCollider.HoleType targetHoleType) {
            var newGameObject = new GameObject("Hole Detector");
            newGameObject.layer = HoleCollider.HOLE_COLLISIONS_LAYER;

            Rigidbody rb = newGameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;

            CapsuleCollider collider = newGameObject.AddComponent<CapsuleCollider>();
            collider.isTrigger = true;
            collider.radius = 0.01f;
            collider.height = length;

            newGameObject.transform.position = parent.position;
            newGameObject.transform.up = parent.forward;

            newGameObject.transform.SetParent(parent);

            HoleDetector holeDetector = newGameObject.AddComponent<HoleDetector>();
            holeDetector.targetHoleType = targetHoleType;

            return holeDetector;
        }

        public void ClearEvents() {
            OnAddHole = null;
            OnAddTargetHole = null;
            
            OnRemoveHole = null;
            OnRemoveTargetHole = null;
        }
    }
}
