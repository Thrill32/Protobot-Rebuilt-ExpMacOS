using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Protobot.Transformations;

namespace Protobot {
    public class NutPlaceDisplacement : PlaceDisplacement {
        [SerializeField] private ObjectLink mouseObj;
        public override bool ModifyRotation => true;
        private bool HoveringOverScrew => mouseObj.active && mouseObj.obj.CompareTag("Screw");
        internal override bool SupportsBodySnap(PlacementData source) => source.objectId.Contains("NUT");
        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            if (SupportsBodySnap(placementData) && HoveringOverScrew) {
                Screw screw = mouseObj.obj.GetComponent<Screw>();

                displacement = new Displacement(screw.GetLowestPoint(), mouseObj.tform.forward);
                return true;
            }

            displacement = null;
            return false;
        }
    }
}
