using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Protobot {
    public abstract class ConnectingPart : MonoBehaviour {
        [SerializeField] public HoleDetector holeDetector;

        private int refreshFrame = -1;

        // isActiveAndEnabled can still be true during a child's OnDisable,
        // before Unity has delivered OnDisable to this component.
        private bool Live => this != null && isActiveAndEnabled && gameObject.activeInHierarchy && !gameObject.IsDeleted();
        
        public abstract bool CanFormConnection { get; }

        public abstract HoleDetector CreateHoleDetector();

        public abstract List<GameObject> GetAttachedParts();
        
        public void Awake() {
            if (holeDetector == null)
                holeDetector = CreateHoleDetector();
        }

        public void FormConnection() {
            if (this == null) return;
            GroupManager.FormConnection(this, Live && holeDetector != null && holeDetector.CanDetect && CanFormConnection
                ? GetAttachedParts() : null);
        }

        public void BreakConnection() => GroupManager.Remove(gameObject);

        private void RefreshConnections() {
            if (Live && refreshFrame < 0) refreshFrame = Time.frameCount;
        }

        private void LateUpdate() {
            if (refreshFrame < 0 || Time.frameCount <= refreshFrame) return;
            refreshFrame = -1;
            if (!Live) return;
            // Re-enabled stationary geometry need not receive a physics callback
            // before its group is available again.
            if (holeDetector != null) holeDetector.RefreshOverlaps();
            FormConnection();
        }

        public void OnEnable() {
            holeDetector.OnAddHole += RefreshConnections;
            holeDetector.OnRemoveHole += RefreshConnections;
            RefreshConnections();
        }

        public void OnDisable() {
            refreshFrame = -1;
            if (holeDetector != null) {
                holeDetector.OnAddHole -= RefreshConnections;
                holeDetector.OnRemoveHole -= RefreshConnections;
            }
            BreakConnection();
        }

        public void OnDestroy() {
            BreakConnection();
        }
    }
}
