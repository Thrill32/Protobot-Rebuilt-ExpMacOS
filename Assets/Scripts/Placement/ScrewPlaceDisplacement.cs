using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Protobot.Transformations;

namespace Protobot {
    public class ScrewPlaceDisplacement : PlaceDisplacement {
        [SerializeField] private HoleFace currentHoleFace;
        public override bool ModifyRotation => true;

        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            displacement = null;
            if (currentHoleFace.direction == Vector3.zero || !currentHoleFace.gameObject.activeInHierarchy ||
                currentHoleFace.hole?.part == null ||
                !(placementData.objectId.Contains("SCRW") || placementData.objectId.Contains("RBMP"))) return false;

            FixedFastener fixedFastener = null;
            if (placementData is PartPlacementData part && part.partGenerator is ScrewPartGenerator { UsesTwoParams: false }) {
                if (!part.TryGetPartData(out var data) || !data.TryGetComponent(out fixedFastener)) return false;
            }
            else if (placementData is GameObjectPlacementData moving)
                moving.GetGameObject().TryGetComponent(out fixedFastener);

            if (fixedFastener != null) {
                if (!fixedFastener.TryGetSeat(currentHoleFace, out var seat)) return false;
                displacement = new Displacement(seat.position, seat.rotation);
            }
            else displacement = new Displacement(currentHoleFace.position, currentHoleFace.LookRotation);
            CollaborationScene.Read(currentHoleFace.hole.part);
            return true;
        }
    }
}
