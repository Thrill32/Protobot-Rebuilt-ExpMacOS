using UnityEngine;

namespace Protobot {
    public class PartPlacementData : PlacementData {
        public override Transform placementTransform { get; set; }

        public PartType partType { get; }
        public PartGenerator partGenerator { get; }
        public override string objectId => partType.id;

        public Pose attachmentPose { get; private set; }
        public float attachmentDepth { get; private set; }

        public void RefreshAttachment() {
            partGenerator.TryGetPlacementHole(out var pose, out var depth);
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
