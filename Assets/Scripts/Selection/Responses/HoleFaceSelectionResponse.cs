using System;
using UnityEngine;
using Protobot.InputEvents;

namespace Protobot.SelectionSystem {
    public class HoleFaceSelectionResponse : SelectionResponse {
        public override bool RespondOnlyToSelectors => false;

        [SerializeField] private HoleFace holeFace = null;
        [SerializeField] private MouseCast mouseCast = null;
        [SerializeField] private InputEvent flipInput;

        private bool flipFaceDir => flipInput.IsPressed;

        public override void OnSet(ISelection holeColliderSel) {
            if (holeColliderSel.gameObject.TryGetComponent(out HoleCollider holeCollider)) {
                holeFace.gameObject.SetActive(true);
                var target = MoveTargeting.ForHole(holeCollider.holeData, mouseCast.hit.normal, flipFaceDir);
                holeFace.Set(target.hole, target.face.rotation * Vector3.forward);
            }
        }

        public override void OnClear(ClearInfo info) {
            holeFace.gameObject.SetActive(false);
        }
    }
}
