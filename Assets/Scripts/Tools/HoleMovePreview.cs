using System;
using System.Collections.Generic;
using EPOOutline;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Protobot.Tools {
    /// <summary>A render-only Move preview. The real rigid set keeps its existing parents and colliders.</summary>
    internal sealed class HoleMovePreview : IDisposable {
        private readonly MovePreviewVisibility visibility;
        private readonly GameObject preview;
        private readonly GameObject canvasObject;
        private readonly Mesh mesh;
        private readonly Material material;
        private readonly Outlinable outline;
        private readonly Image sourceIcon;

        public HoleMovePreview(IEnumerable<GameObject> bodies, Placement placement, Material previewMaterial, Image icon,
            Camera camera, Vector3 position, Quaternion rotation) {
            var bodyRenderers = MeshCombiner.GetRenderers(bodies);
            mesh = MeshCombiner.CombineMeshes(bodyRenderers, position, rotation);
            preview = new GameObject("Grab Hole Move Preview", typeof(MeshFilter), typeof(MeshRenderer));
            preview.hideFlags = HideFlags.DontSave;
            preview.GetComponent<MeshFilter>().sharedMesh = mesh;
            var previewRenderer = preview.GetComponent<MeshRenderer>();
            previewRenderer.shadowCastingMode = ShadowCastingMode.Off;
            previewRenderer.receiveShadows = false;
            // A serialized material also keeps this URP variant in desktop builds.
            material = new Material(previewMaterial) { name = "Grab hole translucent Move material" };
            previewRenderer.sharedMaterial = material;

            // Keep Move's authored outline and animated through-surface fill in one place.
            var templateOutline = placement.GetComponent<Outlinable>();
            if (templateOutline != null) {
                outline = preview.AddComponent<Outlinable>();
                outline.RenderStyle = templateOutline.RenderStyle;
                outline.DrawingMode = templateOutline.DrawingMode;
                outline.OutlineLayer = templateOutline.OutlineLayer;
                CopyStyle(templateOutline.OutlineParameters, outline.OutlineParameters);
                CopyStyle(templateOutline.FrontParameters, outline.FrontParameters);
                CopyStyle(templateOutline.BackParameters, outline.BackParameters);
                outline.AddAllChildRenderersToRenderingList();
            }
            visibility = new MovePreviewVisibility(bodies, bodyRenderers);

            if (icon != null && icon.sprite != null) {
                canvasObject = new GameObject("Grab Hole Source Icon", typeof(RectTransform), typeof(Canvas));
                canvasObject.hideFlags = HideFlags.DontSave;
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 90;
                var imageObject = new GameObject("Move Icon", typeof(RectTransform), typeof(Image));
                imageObject.transform.SetParent(canvasObject.transform, false);
                sourceIcon = imageObject.GetComponent<Image>();
                sourceIcon.sprite = icon.sprite;
                sourceIcon.color = icon.color;
                sourceIcon.preserveAspect = true;
                sourceIcon.raycastTarget = false;
                sourceIcon.rectTransform.anchorMin = sourceIcon.rectTransform.anchorMax = Vector2.zero;
            }
            UpdatePose(camera, position, rotation);
        }

        private static void CopyStyle(Outlinable.OutlineProperties source, Outlinable.OutlineProperties target) {
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), target);
            target.FillPass.OnAfterDeserialize();
        }

        public void UpdatePose(Camera camera, Vector3 position, Quaternion rotation) {
            preview.transform.SetPositionAndRotation(position, rotation);
            if (sourceIcon == null || camera == null) return;
            var point = camera.WorldToScreenPoint(position);
            sourceIcon.gameObject.SetActive(point.z > 0 && camera.pixelRect.Contains(point));
            sourceIcon.rectTransform.anchoredPosition = point;
            sourceIcon.rectTransform.sizeDelta = Vector2.one * (34f * Mathf.Clamp(Screen.height / 900f, .75f, 2f));
        }

        public void Dispose() {
            visibility.Dispose();
            if (preview != null) preview.SetActive(false);
            if (canvasObject != null) canvasObject.SetActive(false);
            if (outline != null) {
                foreach (var style in new[] { outline.OutlineParameters, outline.FrontParameters, outline.BackParameters })
                    if (style.FillPass.Shader != null) Release(style.FillPass.Material);
            }
            Release(preview);
            Release(canvasObject);
            Release(mesh);
            Release(material);
        }

        private static void Release(Object obj) {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }
    }
}
