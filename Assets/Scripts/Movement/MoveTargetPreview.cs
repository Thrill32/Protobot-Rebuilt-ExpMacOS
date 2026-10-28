using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Protobot {
    /// <summary>Shared target presentation: a hole face or a screw endpoint tracker.</summary>
    internal sealed class MoveTargetPreview : IDisposable {
        private readonly HoleFace holePreview;
        private readonly Material templateMaterial;
        private GameObject screwTracker;
        private Material material;
        private bool showingHole;

        public MoveTargetPreview(HoleFace holePreview, Material templateMaterial) {
            this.holePreview = holePreview;
            this.templateMaterial = templateMaterial;
        }

        public void Show(MoveTarget target) {
            Hide();
            if (target.hole != null && holePreview != null) {
                holePreview.Set(target.hole, target.face.rotation * Vector3.forward);
                holePreview.gameObject.SetActive(true);
                showingHole = true;
            }
            else if (target.screw != null) {
                if (screwTracker == null) CreateScrewTracker();
                screwTracker.transform.SetPositionAndRotation(target.face.position, target.face.rotation);
                screwTracker.SetActive(true);
            }
        }

        private void CreateScrewTracker() {
            screwTracker = new GameObject("Move Screw Target") { hideFlags = HideFlags.DontSave,
                layer = LayerMask.NameToLayer("Ignore Raycast") };
            material = new Material(templateMaterial) { name = "Move screw tracker" };
            material.SetColor("_BaseColor", new Color(.8f, .95f, 1f, 1f));
            var ring = AddLine(screwTracker, true);
            const int segments = 32;
            ring.positionCount = segments;
            for (int i = 0; i < segments; i++) {
                float angle = i * Mathf.PI * 2 / segments;
                ring.SetPosition(i, new Vector3(Mathf.Cos(angle) * .22f, Mathf.Sin(angle) * .22f, .006f));
            }
            var axisObject = new GameObject("Endpoint direction") { layer = screwTracker.layer };
            axisObject.transform.SetParent(screwTracker.transform, false);
            var axis = AddLine(axisObject, false);
            axis.positionCount = 2;
            axis.SetPosition(0, Vector3.zero);
            axis.SetPosition(1, Vector3.forward * .3f);
        }

        private LineRenderer AddLine(GameObject obj, bool loop) {
            var line = obj.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = false;
            line.loop = loop;
            line.widthMultiplier = .025f;
            line.generateLightingData = true;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        public void Hide() {
            if (showingHole && holePreview != null) holePreview.gameObject.SetActive(false);
            showingHole = false;
            if (screwTracker != null) screwTracker.SetActive(false);
        }

        public void Dispose() {
            Hide();
            if (Application.isPlaying) {
                if (screwTracker != null) Object.Destroy(screwTracker);
                if (material != null) Object.Destroy(material);
            }
            else {
                if (screwTracker != null) Object.DestroyImmediate(screwTracker);
                if (material != null) Object.DestroyImmediate(material);
            }
        }
    }
}
