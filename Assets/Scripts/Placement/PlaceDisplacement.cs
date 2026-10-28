using UnityEngine;
using Protobot.Transformations;

namespace Protobot {
    public abstract class PlaceDisplacement : MonoBehaviour {
        public abstract bool ModifyRotation { get; }
        public abstract bool TryGetDisplacement(PlacementData placementData, out Displacement displacement);
        public virtual Pose GetRotationAnchor(Displacement displacement) => new(
            displacement.translation.Position, displacement.rotation.Orientation);
    }
}
