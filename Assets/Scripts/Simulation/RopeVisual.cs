using UnityEngine;
using UnityEngine.Rendering;

namespace Protobot.Simulation {
    internal sealed class RopeVisual : MonoBehaviour {
        private const int Sides = 8;
        private Mesh mesh;
        private MeshRenderer meshRenderer;
        private Material material;
        private RopeRouteGeometry.Contour displayed;
        private bool hidden;

        public void SetHidden(bool value) {
            hidden = value;
            if (meshRenderer != null) meshRenderer.forceRenderingOff = value;
        }

        public void Show(RopeRouteGeometry.Contour contour, bool preview = false, float availableLength = 0) {
            if (displayed == contour) return;
            displayed = contour;
            if (contour == null) {
                if (meshRenderer != null) meshRenderer.enabled = false;
                return;
            }
            if (mesh == null) {
                mesh = new Mesh { name = "Rope tube" };
                mesh.MarkDynamic();
                gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                meshRenderer = gameObject.AddComponent<MeshRenderer>();
                meshRenderer.forceRenderingOff = hidden;
                meshRenderer.shadowCastingMode = preview ? ShadowCastingMode.Off : ShadowCastingMode.On;
                material = new Material(Shader.Find(preview ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit"));
                material.color = preview ? new Color(.2f, .8f, 1f) : new Color(.79f, .68f, .5f);
                meshRenderer.sharedMaterial = material;
            }
            var points = preview ? contour.points : contour.WoundPoints(availableLength);
            float radius = RopeRouteGeometry.Diameter * .5f;
            var vertices = new Vector3[points.Length * Sides + 2];
            var normals = new Vector3[vertices.Length];
            var indices = new int[(points.Length - 1) * Sides * 6 + Sides * 6];
            int index = 0;
            for (int i = 0; i < points.Length; i++) {
                var before = i > 0 ? (points[i] - points[i - 1]).normalized : Vector3.zero;
                var after = i + 1 < points.Length ? (points[i + 1] - points[i]).normalized : Vector3.zero;
                var tangent = (before + after).normalized;
                var across = Vector3.Cross(tangent, contour.normal).normalized;
                var up = Vector3.Cross(across, tangent);
                for (int side = 0; side < Sides; side++) {
                    float angle = 2 * Mathf.PI * side / Sides;
                    int vertex = i * Sides + side;
                    var radial = Mathf.Cos(angle) * up + Mathf.Sin(angle) * across;
                    vertices[vertex] = points[i] + radius * radial;
                    normals[vertex] = radial;
                    int adjacent = i * Sides + (side + 1) % Sides;
                    if (i > 0) {
                        indices[index++] = vertex - Sides;
                        indices[index++] = adjacent;
                        indices[index++] = vertex;
                        indices[index++] = vertex - Sides;
                        indices[index++] = adjacent - Sides;
                        indices[index++] = adjacent;
                    }
                }
            }
            int firstCap = vertices.Length - 2;
            vertices[firstCap] = points[0];
            vertices[firstCap + 1] = points[^1];
            normals[firstCap] = (points[0] - points[1]).normalized;
            normals[firstCap + 1] = (points[^1] - points[^2]).normalized;
            int lastRing = (points.Length - 1) * Sides;
            for (int side = 0; side < Sides; side++) {
                int next = (side + 1) % Sides;
                indices[index++] = firstCap;
                indices[index++] = next;
                indices[index++] = side;
                indices[index++] = firstCap + 1;
                indices[index++] = lastRing + side;
                indices[index++] = lastRing + next;
            }
            mesh.Clear();
            mesh.indexFormat = vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = indices;
            meshRenderer.enabled = true;
        }

        private void OnDestroy() {
            Release(mesh);
            Release(material);
        }

        internal static void Release(Object obj) {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
        }
    }
}
