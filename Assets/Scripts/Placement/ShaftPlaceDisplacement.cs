using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Protobot.Transformations;

namespace Protobot {
    public class ShaftPlaceDisplacement : PlaceDisplacement {
        [SerializeField] private HoleFace currentHoleFace;
        [SerializeField] private MouseCast mouseCast;
        private const float AdjacentMotorDistance = 1f;
        public override bool ModifyRotation => true;
        internal override bool SupportsBodySnap(PlacementData source) => source.objectId.Contains("SHFT");
        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            displacement = null;
            if (!SupportsBodySnap(placementData)) return false;

            var (length, highStrength) = GetShaftInfo(placementData);
            var movingShaft = (placementData as GameObjectPlacementData)?.GetGameObject().GetComponent<Shaft>();
            var hovered = mouseCast.gameObject;
            if (hovered != null && hovered.TryGetComponent(out Motor hoveredMotor)) return Insert(hoveredMotor, out displacement);
            if (currentHoleFace.direction == Vector3.zero || !currentHoleFace.gameObject.activeInHierarchy || currentHoleFace.hole?.part == null)
                return false;

            var bore = currentHoleFace.hole;
            CollaborationScene.Read(bore.part);
            if (bore.part.TryGetComponent(out Motor holeMotor)) return Insert(holeMotor, out displacement);
            if (FindMotorThroughBore(bore, length, highStrength, movingShaft, out var motor, out displacement)) {
                CollaborationScene.Read(motor.gameObject);
                return true;
            }
            displacement = new Displacement(currentHoleFace.position, currentHoleFace.LookRotation);
            return true;

            bool Insert(Motor motor, out Displacement insertion) {
                CollaborationScene.Read(motor.gameObject);
                insertion = motor.GetShaftDisplacement(length, highStrength, movingShaft);
                return insertion != null;
            }
        }

        private static bool FindMotorThroughBore(HoleData bore, float length, bool highStrength, Shaft movingShaft,
            out Motor target, out Displacement displacement) {
            target = null;
            displacement = null;
            if (bore.source == null || !bore.source.TryGetComponent(out HoleCollider opening) ||
                opening.holeType != HoleCollider.HoleType.Normal || !Shaft.FitsBore(bore, highStrength)) return false;

            var reach = bore.forward * Mathf.Max(length, AdjacentMotorDistance);
            var origin = bore.position;
            float nearest = float.PositiveInfinity;
            foreach (var hit in Physics.OverlapCapsule(origin - reach, origin + reach, Shaft.SeatTolerance,
                         HoleCollider.HOLE_COLLISIONS_MASK, QueryTriggerInteraction.Ignore)) {
                if (!hit.TryGetComponent(out HoleCollider socket) || !socket.isActiveAndEnabled || MoveTargeting.Excluded(socket.transform) ||
                    socket.holeData?.part == null || !socket.holeData.part.TryGetComponent(out Motor motor) ||
                    !motor.isActiveAndEnabled || motor.ShaftHole != socket || !Shaft.FitsBore(socket.holeData, highStrength)) continue;
                var outward = socket.transform.forward;
                float distance = Vector3.Dot(origin - socket.transform.position, outward);
                if (distance <= 0 || distance >= nearest || !Shaft.IsCoaxial(origin, bore.forward, socket.transform)) continue;
                // An adjacent socket can be picked while changing the preview length. A farther support must be reachable.
                if (distance > AdjacentMotorDistance && Vector3.Dot(origin - motor.ShaftSeat(highStrength), outward) > length) continue;
                if (!GroupManager.ShareFixedFrame(motor.gameObject, bore.part)) continue;
                var insertion = motor.GetShaftDisplacement(length, highStrength, movingShaft);
                if (insertion == null) continue;
                target = motor;
                displacement = insertion;
                nearest = distance;
            }
            return target != null;
        }

        public (float length, bool highStrength) GetShaftInfo(PlacementData placementData) {
            float length = 0;
            bool highStrength = false;
            
            if (placementData.TryParse(out GameObjectPlacementData objectPlacement)) {
                var obj = objectPlacement.GetGameObject();
                length = obj.transform.localScale.z;

                highStrength = obj.GetComponent<SavedObject>().id.Contains("High");
            }
            
            if (placementData.TryParse(out PartPlacementData partPlacement)) {
                var gen = partPlacement.partGenerator;
                length = float.Parse(gen.param2.value);
                highStrength = gen.param1.value.Contains("High");
            }

            return (length, highStrength);
        }
    }
}
