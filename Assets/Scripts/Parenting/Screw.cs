using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace Protobot {
    public class Screw : ConnectingPart {
        public float ScrewLength => GetComponent<MeshFilter>().mesh.bounds.size.z - 0.087f;

        public override bool CanFormConnection => holeDetector.TargetHoleFound;

        public override HoleDetector CreateHoleDetector() {
            var detector = HoleDetector.Create(transform, ScrewLength, HoleCollider.HoleType.Threaded);
            detector.transform.position -= (transform.forward * ScrewLength/2);
            return detector;
        }
        public override List<GameObject> GetAttachedParts() {
            if (CanFormConnection) {
                var orderedHoles = holeDetector.GetOrderedHoles();

                bool connect = false;

                List<GameObject> attachedParts = new List<GameObject>();

                //iterate backwards through the ordered holes to find the lowest threaded hole
                for (int i = orderedHoles.Count - 1; i >= 0; i--) {
                    if (holeDetector.IsTargetHole(orderedHoles[i]))
                        connect = true;

                    if (connect)
                        attachedParts.Add(orderedHoles[i].holeData.part);
                }

                return attachedParts.Distinct().ToList();

            }
            
            return null;
        }

        /// <summary>
        /// Returns the list of attached parts given a custom a position and direction
        /// </summary>
        public List<GameObject> GetAttachedParts(Vector3 position, Vector3 direction) {
            List<GameObject> attachedParts = new List<GameObject>();

            var hits = GetRaycastHits(position, direction);

            for (int i = 0; i < hits.Length; i++) {
                var hit = hits[i];

                if (hit.collider.CompareTag("HoleCollider"))
                    attachedParts.Add(hit.transform.parent.gameObject);
            }

            return attachedParts;
        }


        private RaycastHit[] GetRaycastHits(Vector3 position, Vector3 direction) {
            Ray lowRay = new Ray(position + (direction * 0.1f), -direction);
            return Physics.RaycastAll(lowRay, ScrewLength);
        }

        private RaycastHit[] GetOrderedHoleHits(Func<Transform, bool> ignore = null) {
            return GetRaycastHits(transform.position, transform.forward)
                .Where(hit => hit.collider.TryGetComponent<HoleCollider>(out _) && !hit.collider.gameObject.IsDeleted() &&
                    (ignore == null || !ignore(hit.collider.transform)))
                .OrderBy(hit => Vector3.Distance(hit.point, transform.position))
                .ToArray();
        }

        public Vector3 GetLowestPoint(Func<Transform, bool> ignore = null) {
            var hits = GetOrderedHoleHits(ignore);
            return hits.Length > 0 ? GetExitPoint(hits[hits.Length - 1].collider) : transform.position;
        }

        public Vector3 FindLowestPoint(int attachedPartsIndex) {
            var hits = GetOrderedHoleHits();
            return attachedPartsIndex >= 0 && attachedPartsIndex < hits.Length
                ? GetExitPoint(hits[attachedPartsIndex].collider) : transform.position;
        }

        private Vector3 GetExitPoint(Collider hole) {
            var ray = new Ray(transform.position - transform.forward * ScrewLength, transform.forward);
            return hole.Raycast(ray, out var hit, ScrewLength) ? hit.point : transform.position;
        }
    }
}
