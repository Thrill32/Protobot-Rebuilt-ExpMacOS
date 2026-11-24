using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Protobot.Transformations;

namespace Protobot {
    public class InsertsPlaceDisplacement : PlaceDisplacement {
        [SerializeField] private HoleFace hoverHoleFace;
        public override bool ModifyRotation => true;
        internal override bool SupportsBodySnap(PlacementData source) => source.objectId == "PLSI" || source.objectId == "MESI";

        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            if (SupportsBodySnap(placementData)) {
                if (hoverHoleFace.gameObject.activeInHierarchy && hoverHoleFace.hole.IsHighStrength()) {
                    displacement = new Displacement(hoverHoleFace.position - hoverHoleFace.direction * 0.05f, -hoverHoleFace.direction);
                    CollaborationScene.Read(hoverHoleFace.hole.part);
                    return true;
                }
                else {
                    displacement = null;
                    return false;
                }
            }
            displacement = null;
            return false;
        }
    }
}
