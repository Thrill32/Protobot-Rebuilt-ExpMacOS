using UnityEngine;

namespace Protobot {
    public class PartPlacementData : PlacementData {
        public override Transform placementTransform { get; set; }

        public PartType partType { get; }
        public PartGenerator partGenerator { get; }
        public override string objectId => partType.id;

        public Pose attachmentPose { get; private set; }
        public float attachmentDepth { get; private set; }
        public Quaternion initialRotation { get; private set; }

        public void RefreshAttachment() {
            partGenerator.TryGetPlacementHole(out var pose, out var depth);
            initialRotation = partGenerator.FlipPlacement ? Quaternion.Euler(180, 0, 0) : Quaternion.identity;
            // Reverse both the face normal and its up direction, retaining the hole center.
            pose.rotation *= initialRotation;
            attachmentPose = pose;
            attachmentDepth = depth;
        }

        public PartPlacementData(PartGenerator partGenerator, PartType partType, Transform placementTransform) {
            this.partType = partType;
            this.partGenerator = partGenerator;
            this.placementTransform = placementTransform;
            RefreshAttachment();
        }

        public override Mesh GetDisplayMesh() => partGenerator.GetMesh().CombineSubmeshes();
    }
}
