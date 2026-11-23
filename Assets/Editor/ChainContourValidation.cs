using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Protobot.Builds;
using Protobot.Collaboration;
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
            CheckAdvanced();
            Debug.Log("Chain contours passed: unequal sizes, signed inner-idler wraps, tangent continuity, numerical nonintersection/clearance, baked ribbon coverage, reversed order/flipped faces/oblique planes/scale, signed simulation/texture travel, binary/JSON/legacy compatibility, canonical materialization and malformed-route rejection.");
        }

        internal static SimulationPart[] AdvancedFixture() {
            var points = new[] { new Vector3(-3, 0, 0), new Vector3(0, 1, 0), new Vector3(3, 0, 0), new Vector3(0, 4, 0) };
            var sizes = new[] { "24T", "10T", "40T", "15T" };
            return points.Select((point, i) => PartsManager.GeneratePart("SPKT-Normal-" + sizes[i], point, Quaternion.identity).GetComponent<SimulationPart>()).ToArray();
        }
        internal static ChainData Route(SimulationPart[] parts) => new() { routeVersion = 1, wraps = new[] { 0, 1, 0, 0 },
            members = parts.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray() };
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void CheckAdvanced() {
            var parts = AdvancedFixture(); var data = Route(parts);
            var loop = ChainLoop.FromData(data);
            Require(loop != null, "Concave inner-idler route was rejected");
            CheckContour(loop, parts); CheckNumerical(loop);
            var original = loop.Data;
            Require(loop.TryContour(out var firstContour, out _), "Inner contour is absent");
            var before = firstContour.points;
            Object.DestroyImmediate(loop.gameObject);
            var reversed = data.Remap(id => id); Array.Reverse(reversed.members); Array.Reverse(reversed.wraps);
            loop = ChainLoop.FromData(reversed); Require(loop != null, "Reversed explicit route was rejected");
            CheckContour(loop, parts); CheckNumerical(loop);
            loop.TryContour(out var reverseContour, out _);
            foreach (var point in before) Require(Enumerable.Range(0, reverseContour.points.Length).Min(i =>
                ChainRouteGeometry.DistanceToSegment(point, reverseContour.points[i], reverseContour.points[(i + 1) % reverseContour.points.Length])) < .002f,
                "Reversing the route changed its physical contour");
            Object.DestroyImmediate(loop.gameObject);
            // Move the complete mechanism into an oblique plane and independently flip two faces.
            var rotation = Quaternion.Euler(38, 71, 23);
            for (int i = 0; i < parts.Length; i++) {
                var center = parts[i].ContactCenter;
                parts[i].transform.rotation = rotation * (i % 2 == 0 ? Quaternion.Euler(0, 180, 0) : Quaternion.identity);
                parts[i].transform.position += rotation * center - parts[i].ContactCenter;
            }
            loop = ChainLoop.FromData(data); Require(loop != null, "Oblique/reversed sprocket faces were rejected");
            CheckContour(loop, parts); CheckNumerical(loop);
            var model = MechanismSimulation.Create(parts[0].gameObject);
            Require(model != null && !model.HasConflict, "Inner route caused a simulation conflict");
            loop.TryContour(out var contour, out _);
            var driver = model.tracks.Single(t => t.body == parts[0].gameObject);
            for (int i = 0; i < parts.Length; i++) {
                var track = model.tracks.Single(t => t.body == parts[i].gameObject);
                double worldRatio = track.ratio * Vector3.Dot(track.axis, contour.normal);
                double expected = (double)parts[0].teeth / parts[i].teeth * (data.wraps[i] == 1 ? -1 : 1) * Vector3.Dot(driver.axis, contour.normal);
                Require(Math.Abs(worldRatio - expected) < .0001, "Inner/outer mechanical ratio or flipped-axis direction is wrong");
            }
            model.Evaluate(1); loop.RefreshVisual();
            float expectedTravel = .25f * parts[0].teeth * contour.RotationSign(0);
            Require(Mathf.Abs(loop.GetComponent<LineRenderer>().sharedMaterial.mainTextureOffset.x + expectedTravel) < .0001f,
                "Texture travel disagrees with the signed drive direction");
            model.Evaluate(-1); model.Restore();
            var innerDrive = MechanismSimulation.Create(parts[1].gameObject); innerDrive.Evaluate(1); loop.RefreshVisual();
            Require(!innerDrive.HasConflict && Mathf.Abs(loop.GetComponent<LineRenderer>().sharedMaterial.mainTextureOffset.x +
                .25f * parts[1].teeth * contour.RotationSign(1)) < .0001f, "Driving an inner sprocket reversed texture travel incorrectly");
            innerDrive.Restore();
            foreach (var track in model.tracks) Require(Quaternion.Angle(track.start.rotation, track.body.transform.rotation) < .001f, "Signed chain evaluation did not restore");
            PivotCamera.Main = Object.FindFirstObjectByType<PivotCamera>();
            var snapshot = SceneBuild.ToBuildData();
            using (var stream = new MemoryStream()) {
                var formatter = new BinaryFormatter(); formatter.Serialize(stream, snapshot); stream.Position = 0;
                var restored = (BuildData)formatter.Deserialize(stream);
                Require(snapshot.CompareData(restored) && restored.chains.Single().id == loop.ConnectionId && restored.chains.Single().wraps.SequenceEqual(data.wraps),
                    "Binary persistence lost chain identity or routing metadata");
            }
            var json = JsonUtility.FromJson<BuildData>(JsonUtility.ToJson(snapshot));
            Require(snapshot.CompareData(json) && json.chains.Single().id == loop.ConnectionId, "JSON persistence lost routing metadata or identity");
            var modified = JsonUtility.FromJson<BuildData>(JsonUtility.ToJson(snapshot)); modified.chains[0].wraps[1] = 0;
            Require(!snapshot.CompareData(modified), "A wrap edit did not mark the build dirty");
            var record = loop.Capture(); var canonical = WireJson.Clone(record); DocumentModel.NormalizeRoute(canonical);
            ChainLoop.ApplyRecords(new[] { canonical }); loop = Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).Single();
            Require(loop.ConnectionId == record.id && DocumentModel.SameChain(record, loop.Capture()), "Remote canonical materialization changed route identity/content");
            CheckContour(loop, parts); CheckNumerical(loop);
            // Explicit metadata is strict; old optional members-only records still work.
            foreach (var malformed in new[] {
                new ChainData { members = data.members, routeVersion = 2, wraps = data.wraps },
                new ChainData { members = data.members, routeVersion = 1 },
                new ChainData { members = data.members, routeVersion = 1, wraps = new[] { 0, 1 } },
                new ChainData { members = data.members, routeVersion = 1, wraps = new[] { 0, 7, 0, 0 } },
                new ChainData { members = new[] { data.members[0], data.members[0] }, routeVersion = 1, wraps = new[] { 0, 0 } },
                new ChainData { members = data.members, routeVersion = 0, wraps = data.wraps } })
                Require(ChainLoop.FromData(malformed) == null, "Malformed route was materialized");
            var wrongWrap = data.Remap(id => id); wrongWrap.wraps[1] = 0;
            Require(ChainLoop.FromData(wrongWrap) == null, "An all-outer contour through a concave idler was accepted");
            var square = new[] { new Vector3(-3, -3, 0), new Vector3(3, -3, 0), new Vector3(2, 4, 0), new Vector3(-3, 3, 0) }
                .Select(v => PartsManager.GeneratePart("SPKT-Normal-10T", v, Quaternion.identity).GetComponent<SimulationPart>()).ToArray();
            var crossed = new ChainData { routeVersion = 1, wraps = new int[4],
                members = new[] { square[0], square[2], square[1], square[3] }.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray() };
            Require(ChainLoop.FromData(crossed) == null, "A crossed route was accepted");
            foreach (var part in square) Object.DestroyImmediate(part.gameObject);
            var position = parts[1].transform.position; parts[1].transform.position += contour.normal * .2f;
            Require(ChainLoop.FromData(data) == null, "A noncoplanar route was accepted"); parts[1].transform.position = position;
            var orientation = parts[1].transform.rotation; parts[1].transform.rotation = Quaternion.AngleAxis(15, parts[1].Tooth) * orientation;
            Require(ChainLoop.FromData(data) == null, "A skew-axis route was accepted"); parts[1].transform.rotation = orientation;
            float pitch = parts[1].chainPitch; parts[1].chainPitch *= 2;
            Require(ChainLoop.FromData(data) == null, "Incompatible chain sizes were accepted"); parts[1].chainPitch = pitch;
            float validPitch = parts[1].chainPitch; parts[1].chainPitch = float.NaN;
            Require(ChainLoop.FromData(data) == null, "A nonfinite chain pitch was accepted"); parts[1].chainPitch = validPitch;
            var validCenter = parts[1].localContactCenter; parts[1].localContactCenter = new Vector3(float.NaN, 0, 0);
            Require(ChainLoop.FromData(data) == null, "A nonfinite sprocket center was accepted"); parts[1].localContactCenter = validCenter;
            var extreme = new[] { Vector3.zero, Vector3.right * 1e8f }.Select(v =>
                PartsManager.GeneratePart("SPKT-Normal-10T", v, Quaternion.identity).GetComponent<SimulationPart>()).ToArray();
            Require(ChainLoop.Create(extreme) == null, "An overflowing straight-run sample count was accepted");
            foreach (var part in extreme) Object.DestroyImmediate(part.gameObject);
            var legacy = new ChainData { members = new[] { data.members[0], data.members[2] } };
            Require(ChainLoop.FromData(JsonUtility.FromJson<ChainData>("{\"members\":[\"" + legacy.members[0] + "\",\"" + legacy.members[1] + "\"]}")) != null,
                "A members-only legacy JSON chain failed to load");
            var selector = new SurrogateSelector(); selector.AddSurrogate(typeof(ChainData), new StreamingContext(StreamingContextStates.All), new LegacyChainSurrogate());
            using (var stream = new MemoryStream()) {
                new BinaryFormatter { SurrogateSelector = selector }.Serialize(stream, legacy); stream.Position = 0;
                var loaded = (ChainData)new BinaryFormatter().Deserialize(stream);
                Require(loaded.routeVersion == 0 && loaded.wraps == null && loaded.id == null && ChainLoop.FromData(loaded) != null,
                    "A legacy binary chain without the optional fields failed to load");
            }
            ChainLoop.ClearAll(); foreach (var part in parts) Object.DestroyImmediate(part.gameObject);
            // Adjacent inner sprockets exercise the negative/negative common
            // tangent branch, beyond a single idler on an outer loop.
            var multiPoints = new[] { new Vector3(-4, 0, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(4, 0, 0), new Vector3(0, 5, 0) };
            var sizes = new[] { "24T", "10T", "15T", "40T", "15T" };
            var multiple = multiPoints.Select((v, i) => PartsManager.GeneratePart("SPKT-Normal-" + sizes[i], v, Quaternion.identity).GetComponent<SimulationPart>()).ToArray();
            var multiRoute = new ChainData { routeVersion = 1, wraps = new[] { 0, 1, 1, 0, 0 }, members = multiple.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray() };
            var multiLoop = ChainLoop.FromData(multiRoute);
            Require(multiLoop != null, "Adjacent inner sprocket tangents were rejected"); CheckContour(multiLoop, multiple); CheckNumerical(multiLoop);
            var multiModel = MechanismSimulation.Create(multiple[0].gameObject);
            Require(!multiModel.HasConflict && multiModel.tracks.Count == 5, "Adjacent inner wraps broke transmission");
            for (int i = 0; i < multiple.Length; i++) Require(Math.Abs(multiModel.tracks.Single(t => t.body == multiple[i].gameObject).ratio -
                (double)multiple[0].teeth / multiple[i].teeth * (multiRoute.wraps[i] == 1 ? -1 : 1)) < .0001, "Adjacent inner ratio/direction is wrong");
            ChainLoop.ClearAll(); foreach (var part in multiple) Object.DestroyImmediate(part.gameObject);
        }
        private sealed class LegacyChainSurrogate : ISerializationSurrogate {
            public void GetObjectData(object obj, SerializationInfo info, StreamingContext context) => info.AddValue("members", ((ChainData)obj).members);
            public object SetObjectData(object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector) => throw new NotSupportedException();
        }
        private static void CheckNumerical(ChainLoop loop) {
            Require(loop.TryContour(out var contour, out var reason), reason);
            var p = contour.points; var n = contour.normal;
            Require(p.All(v => Mathf.Abs(Vector3.Dot(v - p[0], n)) < .0001f), "Chain contour left its plane");
            float Cross(Vector3 a, Vector3 b) => Vector3.Dot(Vector3.Cross(a, b), n);
            for (int i = 0; i < p.Length; i++) {
                var a = p[i]; var b = p[(i + 1) % p.Length];
                Require(Vector3.Distance(a, b) > 1e-6f, "Contour contains a zero-length edge");
                var before = (a - p[(i + p.Length - 1) % p.Length]).normalized; var after = (b - a).normalized;
                Require(Vector3.Dot(before, after) > .98f, "Tangent/arc join is not smooth");
                for (int j = 0; j < i - 1; j++) {
                    if (i == p.Length - 1 && j == 0) continue;
                    var c = p[j]; var d = p[j + 1]; float den = Cross(b - a, d - c);
                    if (i - j > 3 && p.Length - (i - j) > 3) {
                        float separation = Mathf.Min(ChainRouteGeometry.DistanceToSegment(a, c, d), ChainRouteGeometry.DistanceToSegment(b, c, d),
                            ChainRouteGeometry.DistanceToSegment(c, a, b), ChainRouteGeometry.DistanceToSegment(d, a, b));
                        Require(separation >= ChainRouteGeometry.Width - .0002f, "Nonlocal ribbon segments lack clearance");
                    }
                    if (Mathf.Abs(den) < 1e-9f) continue;
                    float t = Cross(c - a, d - c) / den, u = Cross(c - a, b - a) / den;
                    Require(!(t > 1e-5f && t < 1 - 1e-5f && u > 1e-5f && u < 1 - 1e-5f), "Numerical contour self-intersection");
                }
            }
            for (int k = 0; k < contour.parts.Length; k++) {
                float radius = (Mathf.Max(contour.parts[k].Radius, contour.parts[k].ToothTipRadius) + ChainRouteGeometry.Width / 2) / Mathf.Cos(4 * Mathf.Deg2Rad);
                int signed = 0;
                for (int i = 1; i < p.Length - 1; i++) if (Mathf.Abs(Vector3.Distance(p[i], contour.parts[k].ContactCenter) - radius) < .0001f &&
                    Mathf.Abs(Vector3.Distance(p[i + 1], contour.parts[k].ContactCenter) - radius) < .0001f) {
                    float turn = Cross(p[i] - p[i - 1], p[i + 1] - p[i]);
                    if (Mathf.Abs(turn) > 1e-8f) { Require(Mathf.Sign(turn) == (contour.wraps[k] == 1 ? -1 : 1), "Wrong wrap curvature"); signed++; }
                }
                Require(signed > 0, "A sprocket has no sampled wrap");
            }
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
            RenderAdvanced();
        }

        private static void RenderAdvanced() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single); PartsManager.LoadPartTypes();
            var parts = AdvancedFixture(); var chain = ChainLoop.FromData(Route(parts)); chain.RefreshVisual();
            foreach (var root in parts.Select(p => p.gameObject).Append(chain.gameObject))
                foreach (Transform t in root.GetComponentsInChildren<Transform>()) t.gameObject.layer = 30;
            var light = new GameObject("Inner route light").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 2; light.cullingMask = 1 << 30;
            light.transform.rotation = Quaternion.Euler(20, -20, 0); RenderSettings.ambientLight = Color.gray;
            var camera = new GameObject("Inner route camera").AddComponent<Camera>(); camera.cullingMask = 1 << 30; camera.orthographic = true;
            camera.orthographicSize = 3.3f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .18f, .21f);
            var rt = new RenderTexture(1400, 1000, 24); camera.targetTexture = rt;
            foreach (int side in new[] { -1, 1 }) {
                camera.transform.SetPositionAndRotation(new Vector3(0, 1.5f, side * 10), Quaternion.LookRotation(Vector3.forward * -side, Vector3.up));
                camera.Render(); RenderTexture.active = rt;
                var image = new Texture2D(1400, 1000, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, 1400, 1000), 0, 0); image.Apply();
                File.WriteAllBytes($"/tmp/protobot-chain-inner-{side}.png", image.EncodeToPNG()); Object.DestroyImmediate(image);
            }
            RenderTexture.active = null; camera.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt);
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
