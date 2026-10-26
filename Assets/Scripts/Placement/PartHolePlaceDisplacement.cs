using Protobot.InputEvents;
using Protobot.Transformations;
using UnityEngine;

namespace Protobot {
    // Fallback after the specialized hardware rules and before free plane placement.
    public class PartHolePlaceDisplacement : PlaceDisplacement {
        [SerializeField] private MouseCast mouseCast;
        [SerializeField] private InputEvent flipInput;
        public override bool ModifyRotation => true;

        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            displacement = null;
            if (placementData is not PartPlacementData part) return false;
            var hit = mouseCast.hit;
            if (hit.collider == null || !hit.collider.TryGetComponent(out HoleCollider hole) ||
                hole.holeData?.part == null) return false;

            var direction = flipInput.IsPressed ? -hit.normal : hit.normal;
            var target = hole.holeData.position + direction * (hole.holeData.depth / 2f);
            var targetRotation = Quaternion.LookRotation(direction, hole.holeData.rotation * Vector3.up);
            displacement = Align(part, target, targetRotation);
            return true;
        }

        public static Displacement Align(PartPlacementData part, Vector3 target, Quaternion targetRotation) {
            var anchor = part.attachmentPose;
            var rotation = targetRotation * Quaternion.Inverse(anchor.rotation);
            var matingFace = anchor.position - anchor.rotation * Vector3.forward * (part.attachmentDepth / 2f);
            return new Displacement(target - rotation * matingFace, rotation);
        }
    }
}
