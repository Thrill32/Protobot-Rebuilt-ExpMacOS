using Protobot.InputEvents;
using Protobot.Transformations;
using UnityEngine;

namespace Protobot {
    public class HolePlaceDisplacement : PlaceDisplacement {
        [SerializeField] private MouseCast mouseCast;
        [SerializeField] private HoleFace hoverHoleFace;
        [SerializeField] private InputEvent oppositeHoleInput;
        [SerializeField] private Material targetMarkerMaterial;

        private MoveTargetPreview targetPreview;
        private Placement placement;
        private int targetFrame = -1;
        public override bool ModifyRotation => true;
        private Pose rotationAnchor;
        public override Pose GetRotationAnchor(Displacement displacement) => rotationAnchor;

        private void Awake() => placement = GetComponent<Placement>();

        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            displacement = null;
            if (placementData is not GameObjectPlacementData moving ||
                !moving.GetGameObject().TryGetComponent<HoleFace>(out var source)) return false;
            if (!MoveTargeting.TryRaycast(mouseCast.ray, mouseCast.layerMask,
                oppositeHoleInput != null && oppositeHoleInput.IsCurrentlyPressed, true, null, out var target)) return false;
            source.Refresh();
            rotationAnchor = target.face;
            var aligned = MoveTargeting.Align(new Pose(source.position, source.LookRotation), target.face,
                new Pose(source.position, source.Rotation));
            displacement = new Displacement(aligned.position, aligned.rotation);
            targetPreview ??= new MoveTargetPreview(hoverHoleFace, targetMarkerMaterial);
            targetPreview.Show(target);
            targetFrame = Time.frameCount;
            return true;
        }

        private void LateUpdate() {
            if (placement == null || !placement.placing || (targetFrame != Time.frameCount && !placement.rotating)) targetPreview?.Hide();
        }

        private void OnDisable() => targetPreview?.Hide();
        private void OnDestroy() => targetPreview?.Dispose();
    }
}
