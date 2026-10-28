using Protobot.InputEvents;
using Protobot.Transformations;
using UnityEngine;

namespace Protobot {
    // Fallback after the specialized hardware rules and before free plane placement.
    public class PartHolePlaceDisplacement : PlaceDisplacement {
        [SerializeField] private MouseCast mouseCast;
        [SerializeField] private InputEvent flipInput;
        public override bool ModifyRotation => true;
        private Pose rotationAnchor;
        public override Pose GetRotationAnchor(Displacement displacement) => rotationAnchor;

        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            displacement = null;
            if (placementData is not PartPlacementData part) return false;
            if (!MoveTargeting.TryRaycast(mouseCast.ray, mouseCast.layerMask, flipInput.IsPressed, false, null, out var target)) return false;
            rotationAnchor = target.face;
            displacement = Align(part, target.face.position, target.face.rotation);
            return true;
        }

        public static Displacement Align(PartPlacementData part, Vector3 target, Quaternion targetRotation) {
            var anchor = part.attachmentPose;
            var matingFace = anchor.position - anchor.rotation * Vector3.forward * (part.attachmentDepth / 2f);
            var aligned = MoveTargeting.Align(new Pose(matingFace, MoveTargeting.Oppose(anchor.rotation)),
                new Pose(target, targetRotation), new Pose(Vector3.zero, Quaternion.identity));
            return new Displacement(aligned.position, aligned.rotation);
        }
    }
}
