using System.Linq;
using Protobot.Transformations;
using UnityEngine;

namespace Protobot.Simulation {
    public sealed class GearPlaceDisplacement : PlaceDisplacement {
        [SerializeField] private MouseCast mouseCast;
        private Pose anchor;
        public override bool ModifyRotation => true;
        public override Pose GetRotationAnchor(Displacement displacement) => anchor;

        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            displacement = null;
            SimulationPart gear;
            Pose pivot;
            GameObject moving = null;
            if (placementData is PartPlacementData part) {
                if (!part.partGenerator.TryGetPartData(out var data) || !data.TryGetComponent(out gear)) return false;
                var source = new GearSnapping.Source(gear.localCenter, gear.localAxis, gear.localTooth, gear.pitchRadius, gear.teeth);
                if (!GearSnapping.TrySnap(mouseCast.ray, source, Pose.identity, null, out var placed, out anchor)) return false;
                displacement = new Displacement(placed.position, placed.rotation);
                return true;
            }
            if (placementData is not GameObjectPlacementData existing) return false;
            moving = existing.GetGameObject();
            var sourceBody = moving.TryGetComponent<HoleFace>(out var face) ? face.hole?.part : moving;
            gear = sourceBody != null ? sourceBody.GetComponent<SimulationPart>() : null;
            if (gear == null) return false;
            pivot = new Pose(moving.transform.position, moving.transform.rotation);
            if (!GearSnapping.TrySnap(mouseCast.ray, new GearSnapping.Source(gear), pivot,
                t => t == sourceBody.transform || t.IsChildOf(sourceBody.transform) || t.gameObject.layer == Placement.PLACEMENT_LAYER,
                out var pose, out anchor)) return false;
            displacement = new Displacement(pose.position, pose.rotation);
            return true;
        }
    }
}
