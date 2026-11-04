using System;
using System.Linq;
using System.IO;
using Protobot;
using Protobot.Simulation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class ChainContourValidation {
        public static void Validate() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            PartsManager.LoadPartTypes();
            foreach (var size in new[] { "15T", "24T", "40T", "48T" }) {
                var small = PartsManager.GeneratePart("SPKT-Normal-10T", Vector3.zero, Quaternion.identity).GetComponent<SimulationPart>();
                var big = PartsManager.GeneratePart("SPKT-Normal-" + size, new Vector3(4, 0, 0), Quaternion.identity).GetComponent<SimulationPart>();
                var chain = ChainLoop.Create(new[] { small, big });
                CheckContour(chain, small, big);
                Object.DestroyImmediate(chain.gameObject);
                chain = ChainLoop.Create(new[] { big, small });
                var rotation = Quaternion.Euler(27, 42, 19);
                foreach (var part in new[] { small, big }) {
                    part.transform.position = rotation * part.transform.position;
                    part.transform.rotation = rotation;
                    part.transform.localScale = Vector3.one * 1.3f;
                }
                CheckContour(chain, small, big);
                Object.DestroyImmediate(chain.gameObject); Object.DestroyImmediate(small.gameObject); Object.DestroyImmediate(big.gameObject);
            }
            Debug.Log("Chain contours passed: unequal normal sprockets, rounded arcs, tooth/line clearance, baked ribbon coverage, reversed selection, oblique planes and scale.");
        }

        private static void CheckContour(ChainLoop chain, params SimulationPart[] parts) {
            if (chain == null) throw new InvalidOperationException("Chain fixture is incompatible");
            chain.RefreshVisual();
            var line = chain.GetComponent<LineRenderer>();
            var points = new Vector3[line.positionCount]; line.GetPositions(points);
            foreach (var part in parts) {
                float outer = part.GetComponent<MeshFilter>().sharedMesh.vertices.Max(v =>
                    Vector3.ProjectOnPlane(part.transform.TransformPoint(v) - part.ContactCenter, part.Axis).magnitude);
                float clearance = float.PositiveInfinity;
                for (int i = 0; i < points.Length; i++) {
                    var start = points[i]; var delta = points[(i + 1) % points.Length] - start;
                    if (delta.sqrMagnitude < 1e-10f) continue;
                    var closest = start + delta * Mathf.Clamp01(Vector3.Dot(part.ContactCenter - start, delta) / delta.sqrMagnitude);
                    clearance = Mathf.Min(clearance, Vector3.Distance(closest, part.ContactCenter) - outer - line.widthMultiplier / 2);
                }
                if (clearance < -.0001f || clearance > .005f)
                    throw new InvalidOperationException($"Chain should hug the tooth tips with its full width clear: {part.teeth}T clearance={clearance:F5}");
            }
            var normal = parts[0].Axis;
            var camera = new GameObject("Chain coverage camera").AddComponent<Camera>(); camera.orthographic = true;
            var mesh = new Mesh();
            try {
                foreach (int side in new[] { -1, 1 }) {
                    camera.transform.SetPositionAndRotation(parts[0].Center + normal * side * 10, Quaternion.LookRotation(-normal * side, parts[0].Tooth));
                    line.BakeMesh(mesh, camera, true);
                    var vertices = mesh.vertices; var triangles = mesh.triangles;
                    float Cross(Vector3 a, Vector3 b) => Vector3.Dot(Vector3.Cross(a, b), normal);
                    float area = 0;
                    for (int t = 0; t < triangles.Length; t += 3)
                        area += Cross(vertices[triangles[t + 1]] - vertices[triangles[t]], vertices[triangles[t + 2]] - vertices[triangles[t]]);
                    bool Covered(Vector3 point) {
                        for (int t = 0; t < triangles.Length; t += 3) {
                            var a = vertices[triangles[t]]; var b = vertices[triangles[t + 1]]; var c = vertices[triangles[t + 2]];
                            float triangleArea = Cross(b - a, c - a);
                            if (triangleArea * area <= 0 || Mathf.Abs(triangleArea) < 1e-9f) continue;
                            float u = Cross(b - point, c - point) / triangleArea;
                            float v = Cross(c - point, a - point) / triangleArea;
                            if (u >= -.001f && v >= -.001f && u + v <= 1.001f) return true;
                        }
                        return false;
                    }
                    for (int i = 0; i < points.Length; i++) {
                        var next = points[(i + 1) % points.Length];
                        if ((next - points[i]).sqrMagnitude < 1e-10f) continue;
                        var midpoint = (points[i] + next) / 2;
                        var width = Vector3.Cross(normal, next - points[i]).normalized * line.widthMultiplier * .3f;
                        if (!Covered(midpoint + width) || !Covered(midpoint - width))
                            throw new InvalidOperationException($"Chain ribbon folds or narrows at segment {i}/{points.Length}, viewed from side {side}: parts={string.Join(",", parts.Select(p => p.teeth + "T@" + p.Center))} point={midpoint:F5} normal={normal:F5} area={area:F5}");
                    }
                }
            }
            finally { Object.DestroyImmediate(mesh); Object.DestroyImmediate(camera.gameObject); }
        }

        public static void Render() {
            RenderPair("40T", 1);
            RenderPair("15T", 2);
        }

        private static void RenderPair(string size, float spacing) {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            PartsManager.LoadPartTypes();
            var small = PartsManager.GeneratePart("SPKT-Normal-10T", new Vector3(-spacing, 0, 0), Quaternion.identity).GetComponent<SimulationPart>();
            var big = PartsManager.GeneratePart("SPKT-Normal-" + size, new Vector3(spacing, 0, 0), Quaternion.identity).GetComponent<SimulationPart>();
            var chain = ChainLoop.Create(new[] { small, big }); chain.RefreshVisual();
            foreach (var root in new[] { small.gameObject, big.gameObject, chain.gameObject })
                foreach (Transform t in root.GetComponentsInChildren<Transform>()) t.gameObject.layer = 30;
            var light = new GameObject("Chain render light").AddComponent<Light>(); light.type = LightType.Directional;
            light.intensity = 2; light.cullingMask = 1 << 30; light.transform.rotation = Quaternion.Euler(20, -20, 0);
            RenderSettings.ambientLight = Color.gray;
            var camera = new GameObject("Chain render camera").AddComponent<Camera>();
            camera.cullingMask = 1 << 30; camera.orthographic = true; camera.orthographicSize = 1.35f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .18f, .21f);
            var rt = new RenderTexture(1500, 800, 24); camera.targetTexture = rt;
            foreach (int side in new[] { -1, 1 }) {
                camera.transform.SetPositionAndRotation(new Vector3(0, 0, side * 6), Quaternion.LookRotation(Vector3.forward * -side, Vector3.up));
                camera.Render(); RenderTexture.active = rt;
                var image = new Texture2D(1500, 800, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, 1500, 800), 0, 0); image.Apply();
                File.WriteAllBytes($"/tmp/protobot-chain-{size}-{side}.png", image.EncodeToPNG()); Object.DestroyImmediate(image);
            }
            RenderTexture.active = null; camera.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt);
            Debug.Log($"Chain contour render saved to /tmp/protobot-chain-{size}-{{1,-1}}.png");
        }
    }
}
