using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using Protobot.Transformations;

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
            if (CollaborationSession.BlocksLegacyMutation) return;
            if (placement.placing || !objectLink.active || Tools.KeyboardTransformTool.IsCapturingInput)
                return;
            // A grab can restore the body before the face's next Update.
            // Build the preview from the live source frame in either entry path.
            if (objectLink.obj.TryGetComponent<HoleFace>(out var face)) face.Refresh();
            placement.StartPlacing(new GameObjectPlacementData(objectLink.obj, placement.transform, objectLink.individual));
        }
    }
}

