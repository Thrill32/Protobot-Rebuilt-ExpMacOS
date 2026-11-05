using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System;
using Protobot.UI;

namespace Protobot.SelectionSystem {
    public class BoxSelector : Selector {
        public override event Action<ISelection> setEvent;
        public override event Action clearEvent;

        [SerializeField] private BoxSelectUI boxSelectUI;
        [SerializeField] private Camera cam;

        void Start() {
            boxSelectUI.OnReleaseBox += SetBoxSelection;
        }

        void SetBoxSelection(Vector2 startPos, Vector2 finalPos) {
            if (Vector2.Distance(startPos, finalPos) <= 5) return;

            List<GameObject> objs = new List<GameObject>();
            Bounds bounds = GetViewportBounds(cam, startPos, finalPos);

            foreach (GameObject loadedObj in PartsManager.FindLoadedObjects()) {
                if (bounds.Contains(cam.WorldToViewportPoint(loadedObj.transform.position))) {
                    objs.Add(loadedObj);
                }
            }

            bool additive = GetComponent<MultiSelector>()?.IsMultiSelectHeld == true;
            if (objs.Count == 0) {
                if (!additive) clearEvent?.Invoke();
                return;
            }

            var previous = additive ? GetComponent<SelectionManager>()?.current : null;
            if (previous?.gameObject != null) {
                // Capture the current bodies before selection dispatch detaches
                // its pivot. Expand new hits separately to retain the old scope.
                var combined = new HashSet<GameObject>();
                foreach (var obj in objs) AddBodies(combined, obj, true);
                AddBodies(combined, previous.gameObject, !previous.individual);
                objs = combined.ToList();
            }

            var selection = new MultiSelection(this, objs) {
                individual = previous?.individual == true
            };
            setEvent?.Invoke(selection);
        }

        private static void AddBodies(HashSet<GameObject> bodies, GameObject root, bool includeConnections) {
            foreach (var connected in root.GetConnectedObjects(true, true, includeConnections))
                foreach (var saved in connected.GetComponentsInChildren<SavedObject>())
                    if (!saved.gameObject.IsDeleted()) bodies.Add(saved.gameObject);
        }

        public static Bounds GetViewportBounds(Camera camera, Vector3 screenPosition1, Vector3 screenPosition2) {
            var v1 = camera.ScreenToViewportPoint(screenPosition1);
            var v2 = camera.ScreenToViewportPoint(screenPosition2);
            var min = Vector3.Min(v1, v2);
            var max = Vector3.Max(v1, v2);
            min.z = camera.nearClipPlane;
            max.z = camera.farClipPlane;

            var bounds = new Bounds();

            bounds.SetMinMax(min, max);

            return bounds;
        }
    }
}
