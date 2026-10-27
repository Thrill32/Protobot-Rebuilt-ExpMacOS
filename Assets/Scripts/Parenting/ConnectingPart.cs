using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Protobot {
    public abstract class ConnectingPart : MonoBehaviour {
        [SerializeField] public HoleDetector holeDetector;

        private Coroutine pendingRefresh;
        
        public abstract bool CanFormConnection { get; }

        public abstract HoleDetector CreateHoleDetector();

        public abstract List<GameObject> GetAttachedParts();
        
        public void Awake() {
            if (holeDetector == null)
                holeDetector = CreateHoleDetector();
        }

        public void FormConnection() {
            GroupManager.FormConnection(this, CanFormConnection ? GetAttachedParts() : null);
        }

        public void BreakConnection() => GroupManager.Remove(gameObject);

        private void RefreshConnections() {
            if (isActiveAndEnabled && pendingRefresh == null)
                pendingRefresh = StartCoroutine(WaitRefresh());
        }

        private IEnumerator WaitRefresh() {
            yield return new WaitForEndOfFrame();
            pendingRefresh = null;
            FormConnection();
        }
        
        public void OnEnable() {
            holeDetector.OnAddHole += RefreshConnections;
            holeDetector.OnRemoveHole += RefreshConnections;
            RefreshConnections();
        }

        public void OnDisable() {
            if (pendingRefresh != null) StopCoroutine(pendingRefresh);
            pendingRefresh = null;
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
