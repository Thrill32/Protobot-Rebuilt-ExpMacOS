using System.Collections;
using System.Collections.Generic;
using Protobot.Transformations;
using UnityEngine;

namespace Protobot {
    public class Motor : MonoBehaviour {
        [SerializeField] private HoleCollider shaftHole;
        public HoleCollider ShaftHole => shaftHole;
        private float HsOffset => 0.2775f;
        private float NormOffset => 0.47f;

        internal Vector3 ShaftSeat(bool highStrength) => shaftHole.transform.position -
            shaftHole.transform.forward * (highStrength ? HsOffset : NormOffset);
        
        public Displacement GetShaftDisplacement(float shaftLength, bool highStrength, Shaft movingShaft = null) {
            bool reseating = movingShaft != null && shaftHole.IsOccupiedBy(movingShaft.holeDetector);
            if (shaftHole.IsOccupied && !reseating) return null;
            Vector3 normal = shaftHole.holeData.forward;

            Vector3 pos = ShaftSeat(highStrength) + normal * (shaftLength * .5f);

            if (reseating) {
                var shaftTransform = movingShaft.transform;
                var forward = shaftTransform.forward;
                var aligned = Vector3.Dot(forward, normal) < 0 ? -normal : normal;
                var rotation = Quaternion.FromToRotation(forward, aligned) * shaftTransform.rotation;
                return new Displacement(pos, rotation);
            }
            return new Displacement(pos, normal);
        }
    }
}
