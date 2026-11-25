using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Protobot.InputEvents;
using Protobot.Transformations;

namespace Protobot {
    public class ShaftAxisPlaceDisplacement : PlaceDisplacement {
        [SerializeField] private MouseCast mouseCast;
        [SerializeField] private InputEvent flipInput;
        private const float SeatSnapDistance = .325f;
        private Pose? collarRotationAnchor;
        public override bool ModifyRotation => true;
        internal override bool SupportsBodySnap(PlacementData source) => IsCollar(source) || source.objectId.Contains("Hole") || GetOffset(source) > 0;
        public override Pose GetRotationAnchor(Displacement displacement) => collarRotationAnchor ?? base.GetRotationAnchor(displacement);

        private static bool IsCollar(PlacementData source) => source.objectId == "CLMP" ||
            source.objectId.StartsWith("CLMP-", System.StringComparison.Ordinal);
        

        public bool IsMotionObject(PlacementData placementData) {
            if (placementData.TryParse(out GameObjectPlacementData objPlaceData)) { 
                return objPlaceData.GetGameObject().CompareTag("Motion");
            }
            else if (placementData.TryParse(out PartPlacementData partPlaceData)) {
                return partPlaceData.partType.group == PartType.PartGroup.Motion;
            }

            return false;
        }

        public float GetOffset(PlacementData placementData) {
            if (placementData.TryGetPartData(out var partData))
                return partData.PrimaryHoleDepth / 2;
            
            return 0;
        }

        public float GetInsertsOffset(PlacementData placementData, GameObject shaftObj) {
            if (!placementData.TryGetPartData(out var partData)) return 0;
            
            if (partData.allowCenterInserts && !shaftObj.name.Contains("HS"))
                return 0.05f;

            return 0;
        }

        public override bool TryGetDisplacement(PlacementData placementData, out Displacement displacement) {
            collarRotationAnchor = null;
            var hover = mouseCast.hit;
            if (hover.collider != null && SupportsBodySnap(placementData)) {
                var hovered = hover.collider.gameObject;
                if (hovered.CompareTag("Shaft")) {
                    CollaborationScene.Read(hovered);
                    Transform shaftTransform = hovered.transform;
                    Vector3 shaftMousePos = shaftTransform.position + Vector3.Project(hover.point - shaftTransform.position, shaftTransform.forward);

                    if (IsCollar(placementData) && hovered.TryGetComponent(out Shaft shaft))
                        return TryPlaceCollar(placementData, shaft, shaftMousePos, out displacement);

                    var rayForward = new Ray(shaftMousePos, shaftTransform.forward);
                    var rayBackward = new Ray(shaftMousePos, -shaftTransform.forward);

                    var forwardCast = Physics.Raycast(rayForward, out RaycastHit forwardHit, 12, HoleCollider.HOLE_COLLISIONS_MASK);
                    var backwardCast = Physics.Raycast(rayBackward, out RaycastHit backwardHit, 12, HoleCollider.HOLE_COLLISIONS_MASK);

                    float forwardDistance = 12;
                    float backwardDistance = 12;

                    if (forwardCast) forwardDistance = Vector3.Distance(forwardHit.point, shaftMousePos);
                    if (backwardCast) backwardDistance = Vector3.Distance(backwardHit.point, shaftMousePos);
                    if (forwardCast) CollaborationScene.Read(forwardHit.collider.gameObject);
                    if (backwardCast) CollaborationScene.Read(backwardHit.collider.gameObject);

                    Vector3 offset = shaftTransform.forward * (GetOffset(placementData) + GetInsertsOffset(placementData, hovered));

                    var rotation = shaftTransform.GetAlignmentRotation(flipInput.IsPressed);
                    var nut = placementData is PartPlacementData part && part.partType.id == "NUT" ? part : null;
                    if (nut != null) {
                        RaycastHit? mountingHit = forwardDistance < backwardDistance && forwardDistance < SeatSnapDistance ? forwardHit :
                            backwardDistance < forwardDistance && backwardDistance < SeatSnapDistance ? backwardHit : null;
                        if (mountingHit.HasValue) {
                            var hit = mountingHit.Value;
                            var hole = hit.collider.GetComponent<HoleCollider>();
                            var face = MoveTargeting.ForHole(hole.holeData, hit.normal, flipInput.IsPressed).face;
                            displacement = PartHolePlaceDisplacement.Align(nut, face.position, face.rotation);
                        }
                        else displacement = new Displacement(shaftMousePos, shaftTransform.GetAlignmentRotation(!flipInput.IsPressed));
                        return true;
                    }

                    if (forwardDistance < backwardDistance && forwardDistance < SeatSnapDistance) {
                        if (placementData.objectId.Contains("Hole")) {
                            rotation = Quaternion.LookRotation(forwardHit.normal, shaftTransform.up);
                        }
                        displacement = new Displacement(forwardHit.point - offset, rotation);
                        return true;
                    }

                    if (backwardDistance < forwardDistance && backwardDistance < SeatSnapDistance) {
                        if (placementData.objectId.Contains("Hole")) {
                            rotation = Quaternion.LookRotation(backwardHit.normal, shaftTransform.up);
                        }
                        displacement = new Displacement(backwardHit.point + offset, rotation);
                        return true;
                    }

                    displacement = new Displacement(shaftMousePos, rotation);
                    return true;
                }
            }

            displacement = null;
            return false;
        }

        private bool TryPlaceCollar(PlacementData source, Shaft shaft, Vector3 pointer, out Displacement displacement) {
            displacement = null;
            var initialRotation = Quaternion.identity;
            Pose localBore;
            float halfDepth;
            if (source is PartPlacementData part) {
                initialRotation = part.initialRotation;
                localBore = new Pose(part.attachmentPose.position, part.attachmentPose.rotation * Quaternion.Inverse(initialRotation));
                halfDepth = part.attachmentDepth * .5f;
            } else if (source is GameObjectPlacementData moving) {
                var body = moving.GetGameObject();
                var bore = body.GetComponentsInChildren<HoleCollider>().FirstOrDefault(hole => hole.holeType == HoleCollider.HoleType.Clamp);
                if (bore == null) return false;
                var root = body.transform;
                var inverse = Quaternion.Inverse(root.rotation);
                localBore = new Pose(inverse * (bore.transform.position - root.position), inverse * bore.transform.rotation);
                halfDepth = bore.WorldHalfDepth;
            } else return false;

            var axis = shaft.transform.forward;
            var center = pointer;
            float nearest = SeatSnapDistance;
            HoleCollider seat = null;
            FindSeat(axis);
            FindSeat(-axis);
            if (seat != null) CollaborationScene.Read(seat.gameObject);

            var shaftRotation = shaft.transform.GetAlignmentRotation(flipInput.IsPressed);
            var bodyRotation = shaftRotation * Quaternion.Inverse(localBore.rotation);
            collarRotationAnchor = new Pose(center, shaftRotation);
            // Placement applies the catalog's initial rotation to the returned orientation.
            displacement = new Displacement(center - bodyRotation * localBore.position, bodyRotation * Quaternion.Inverse(initialRotation));
            return true;

            void FindSeat(Vector3 direction) {
                foreach (var hit in Physics.RaycastAll(pointer, direction, SeatSnapDistance, HoleCollider.HOLE_COLLISIONS_MASK,
                             QueryTriggerInteraction.Ignore)) {
                    if (!hit.collider.TryGetComponent(out HoleCollider hole) || !hole.isActiveAndEnabled ||
                        hole.holeData?.part == null || MoveTargeting.Excluded(hole.transform) || !shaft.IsCoaxial(hole)) continue;
                    float distance = Vector3.Dot(hole.transform.position - pointer, direction) - hole.WorldHalfDepth;
                    if (distance < 0 || distance >= nearest) continue;
                    nearest = distance;
                    seat = hole;
                    center = pointer + direction * (distance - halfDepth);
                }
            }
        }
    }
}
