using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using Protobot.Transformations;
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
            TryStartMoving(objectLink.obj, objectLink.individual, null, false, null, null);
        }

        public bool StartHoleMove(ISelection selection, Action started, Action denied) {
            var anchor = selection is HoleSelection hole ? SelectedHoleAnchor.From(hole) :
                (selection as MultiSelection)?.placementHole;
            return TryStartMoving(selection?.gameObject, selection?.individual == true, anchor, true, started, denied);
        }

        public static bool HasConcreteSourceHole(ISelection selection) {
            var anchor = selection is HoleSelection hole ? SelectedHoleAnchor.From(hole) :
                (selection as MultiSelection)?.placementHole;
            return AnchorBelongsToSelection(selection?.gameObject, selection?.individual == true, anchor);
        }

        private bool TryStartMoving(GameObject source, bool individual, SelectedHoleAnchor anchor, bool requireHole,
            Action started, Action denied) {
            if (placement.placing || source == null || !source.activeInHierarchy || Tools.KeyboardTransformTool.IsCapturingInput ||
                requireHole && !AnchorBelongsToSelection(source, individual, anchor)) {
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
                        (!requireHole || AnchorBelongsToSelection(source, individual, anchor)) &&
                        CollaborationSession.HasPoseReservation(source.GetConnectedObjects(true, false, !individual)))
                        TryStartMoving(source, individual, anchor, requireHole, started, denied);
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

