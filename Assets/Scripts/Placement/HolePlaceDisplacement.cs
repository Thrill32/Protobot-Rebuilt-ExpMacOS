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
            HoleFace source = null;
            if (placementData is SelectionPlacementData copy) {
                if (!copy.HasHole) return false;
            }
            else if (placementData is not GameObjectPlacementData moving ||
                !moving.GetGameObject().TryGetComponent(out source)) return false;
            if (!MoveTargeting.TryRaycast(mouseCast.ray, mouseCast.layerMask,
                oppositeHoleInput != null && oppositeHoleInput.IsCurrentlyPressed, true, null, out var target)) return false;
            rotationAnchor = target.face;
            Pose aligned;
            if (source != null) {
                source.Refresh();
                aligned = MoveTargeting.Align(new Pose(source.position, source.LookRotation), target.face,
                    new Pose(source.position, source.Rotation));
            }
            else aligned = MoveTargeting.Align(Pose.identity, target.face, Pose.identity);
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
