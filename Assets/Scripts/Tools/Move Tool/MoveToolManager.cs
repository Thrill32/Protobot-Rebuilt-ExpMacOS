using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using Protobot.SelectionSystem;

namespace Protobot {
    public class MoveToolManager : MonoBehaviour {
        [SerializeField] private Placement placement;
        [SerializeField] private ObjectLink objectLink;

        private Image image;

        private void Start() {
            image = GetComponent<Image>();
        }

        private void Update() {
            if (image != null && objectLink.active)
                image.enabled = (objectLink.obj.layer != Placement.PLACEMENT_LAYER);
        }

        public void StartMoving() {
            TryStartMoving(objectLink.obj, objectLink.individual, null, null, null, null);
        }

        public bool StartGrabMove(ISelection selection, Action started, Action denied) {
            var anchor = GetSourceHole(selection);
            return TryStartMoving(selection?.gameObject, selection?.individual == true, anchor,
                () => CanMoveFromGrab(selection, anchor), started, denied);
        }

        public bool CanMoveFromGrab(ISelection selection) => CanMoveFromGrab(selection, GetSourceHole(selection));

        private static SelectedHoleAnchor GetSourceHole(ISelection selection) =>
            selection is HoleSelection hole ? SelectedHoleAnchor.From(hole) : (selection as MultiSelection)?.placementHole;

        private bool CanMoveFromGrab(ISelection selection, SelectedHoleAnchor anchor) {
            var source = selection?.gameObject;
            if (source == null || !source.activeInHierarchy) return false;
            if (selection is ObjectSelection)
                return !source.IsDeleted() && source.TryGetComponent<SavedObject>(out var part) && !string.IsNullOrEmpty(part.id) &&
                    placement.SupportsBodySnap(new GameObjectPlacementData(source, placement.transform, selection.individual));
            return AnchorBelongsToSelection(source, selection.individual, anchor);
        }

        private bool TryStartMoving(GameObject source, bool individual, SelectedHoleAnchor anchor, Func<bool> sourceIsValid,
            Action started, Action denied) {
            if (placement.placing || source == null || !source.activeInHierarchy || Tools.KeyboardTransformTool.IsCapturingInput ||
                sourceIsValid?.Invoke() == false) {
                denied?.Invoke();
                return false;
            }
            var bodies = source.GetConnectedObjects(true, false, !individual);
            if (CollaborationSession.IsShared && !CollaborationSession.HasPoseReservation(bodies)) {
                if (!CollaborationSession.CanEdit || CollaborationSession.HasReservation) {
                    denied?.Invoke();
                    return false;
                }
                CollaborationSession.ReservePose(bodies, () => {
                    if (source == objectLink.obj && !placement.placing &&
                        sourceIsValid?.Invoke() != false &&
                        CollaborationSession.HasPoseReservation(source.GetConnectedObjects(true, false, !individual)))
                        TryStartMoving(source, individual, anchor, sourceIsValid, started, denied);
                    else {
                        CollaborationSession.CancelPose();
                        denied?.Invoke();
                    }
                }, denied);
                return true;
            }
            // A grab can restore the body before the face's next Update.
            // Build the preview from the live source frame in either entry path.
            if (source.TryGetComponent<HoleFace>(out var face)) face.Refresh();
            placement.StartPlacing(new GameObjectPlacementData(source, placement.transform, individual, anchor));
            if (!placement.placing) {
                denied?.Invoke();
                return false;
            }
            started?.Invoke();
            return true;
        }

        private static bool AnchorBelongsToSelection(GameObject source, bool individual, SelectedHoleAnchor anchor) {
            if (source == null || !source.activeInHierarchy || anchor?.IsValid != true) return false;
            return source.GetConnectedObjects(true, true, !individual).Contains(anchor.Hole.part);
        }
    }
}

