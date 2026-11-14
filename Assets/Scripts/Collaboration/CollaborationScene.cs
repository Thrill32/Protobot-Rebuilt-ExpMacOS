using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using UnityEngine;

namespace Protobot {
    public static class CollaborationScene {
        // Tool scopes can contain selection/group pivots and hole markers. Only saved bodies own shared identity.
        public static List<GameObject> Bodies(IEnumerable<GameObject> roots) => roots.Where(root => root != null)
            .SelectMany(root => root.GetComponentsInChildren<SavedObject>())
            .Where(saved => saved.id != "Error" && !saved.gameObject.IsDeleted()).Select(saved => saved.gameObject).Distinct().ToList();
        private static Dictionary<string, VersionRef> reads;
        public static void BeginReads() { reads = CollaborationSession.IsShared ? new Dictionary<string, VersionRef>() : null; }
        public static void Read(GameObject target) {
            if (reads == null) return;
            var body = PartColors.BodyOf(target);
            if (body == null) return;
            foreach (var version in CollaborationSession.Versions(new[] { body.GetComponent<SavedObject>().ConnectionId })) reads[version.id] = version;
        }
        public static VersionRef[] EndReads() { var result = reads?.Values.ToArray() ?? Array.Empty<VersionRef>(); reads = null; return result; }
        public static CatalogManifest Catalog {
            get {
                var text = Resources.Load<TextAsset>("Collaboration/catalog");
                if (text == null) throw new InvalidOperationException("Export the collaboration catalog through Tools → Protobot → Collaboration before hosting.");
                return WireJson.Decode<CatalogManifest>(text.bytes);
            }
        }
        public static Part Capture(GameObject body, long version = 0) {
            return CapturePose(body, Protobot.Simulation.SimulationTool.SavedPose(body), version);
        }
        public static Part CaptureLive(GameObject body) => CapturePose(body, new Pose(body.transform.position, body.transform.rotation), 0);
        private static Part CapturePose(GameObject body, Pose pose, long version) {
            var saved = body.GetComponent<SavedObject>();
            var color = PartColors.GetColor(body);
            var custom = body.GetComponent<CustomModel>();
            var customData = custom != null ? custom.Snapshot() : null;
            var acceptedCustom = customData != null ? CollaborationSession.AcceptedPart(saved.ConnectionId) : null;
            var asset = customData != null && acceptedCustom == null ? CaptureAsset(customData) : null;
            return new Part { id = saved.ConnectionId, version = version, catalogId = saved.id, state = saved.state,
                position = new double[] { pose.position.x, pose.position.y, pose.position.z },
                rotation = new double[] { pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w },
                hasPaint = saved.hasPaint, color = new double[] { color.r, color.g, color.b }, assetHash = acceptedCustom?.assetHash ?? asset?.hash,
                mounts = acceptedCustom != null ? WireJson.Clone(DocumentModel.Mounts(acceptedCustom)) :
                    customData?.mounts?.Select(CaptureMount).ToArray() ?? Array.Empty<CustomMount>() };
        }
        public static GeometryAsset[] CaptureAssets(IEnumerable<GameObject> bodies) => Bodies(bodies)
            .Select(body => body.GetComponent<CustomModel>()).Where(model => model != null).Select(model => CaptureAsset(model.Snapshot()))
            .GroupBy(asset => asset.hash, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        private static GeometryAsset CaptureAsset(CustomModelData data) {
            var asset = new GeometryAsset { name = data.name,
                vertices = data.vertices.Select(value => (double)value).ToArray(), triangles = data.triangles.ToArray() };
            asset.hash = DocumentModel.GeometryHash(asset); return asset;
        }
        private static CustomMount CaptureMount(CustomMountData mount) => new CustomMount {
            position = new double[] { mount.x, mount.y, mount.z }, rotation = new double[] { mount.qx, mount.qy, mount.qz, mount.qw },
            diameter = mount.diameter, depth = mount.depth, threaded = mount.threaded
        };
        public static Part CaptureCustomMounts(CustomModel model) {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var id = model.GetComponent<SavedObject>().ConnectionId;
            var accepted = CollaborationSession.AcceptedPart(id);
            if (accepted == null || accepted.catalogId != CustomModel.PartId) throw new InvalidOperationException("The custom model is not in the accepted room document.");
            var write = WireJson.Clone(accepted);
            write.mounts = model.Snapshot().mounts.Select(CaptureMount).ToArray();
            return write;
        }
        public static Document CaptureDocument(CatalogManifest catalog) {
            var bodies = PartsManager.FindLoadedObjects();
            var loops = UnityEngine.Object.FindObjectsByType<Simulation.ChainLoop>(FindObjectsSortMode.None);
            var chainParts = loops.ToDictionary(loop => loop, loop => loop.ResolveParts());
            if (loops.Any(loop => !loop.TryContour(out _, out _)))
                throw new InvalidOperationException("Remove or repair invalid chains before hosting.");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var body in bodies) {
                var saved = body.GetComponent<SavedObject>();
                if (!DocumentModel.ValidId(saved.ConnectionId) || !used.Add(saved.ConnectionId)) {
                    saved.ConnectionId = Guid.NewGuid().ToString("N"); used.Add(saved.ConnectionId);
                }
            }
            var chains = new List<ChainRecord>();
            foreach (var loop in loops) {
                if (!used.Add(loop.ConnectionId)) { do loop.SetConnectionId(Guid.NewGuid().ToString("N")); while (!used.Add(loop.ConnectionId)); }
                chains.Add(loop.Capture(chainParts[loop], 1));
            }
            var document = new Document { catalogHash = catalog.hash, parts = bodies.Select(b => Capture(b, 1)).ToArray(), chains = chains.ToArray(),
                assets = CaptureAssets(bodies) };
            DocumentModel.Validate(document, catalog); return document;
        }
        public static ObjectData ToObjectData(Part part, IEnumerable<GeometryAsset> geometry = null) {
            var rotation = Rotation(part).eulerAngles;
            return new ObjectData { connectionId = part.id, partId = part.catalogId, states = part.state, hasPaint = part.hasPaint,
                xPos = part.position[0], yPos = part.position[1], zPos = part.position[2],
                xRot = rotation.x, yRot = rotation.y, zRot = rotation.z,
                rColor = part.color[0], gColor = part.color[1], bColor = part.color[2],
                customModel = part.catalogId == CustomModel.PartId ? ToCustomModel(part, geometry) : null };
        }
        private static CustomModelData ToCustomModel(Part part, IEnumerable<GeometryAsset> geometry) {
            var asset = (geometry ?? Array.Empty<GeometryAsset>()).FirstOrDefault(value => value.hash == part.assetHash);
            if (asset == null) throw new InvalidOperationException("The shared custom model geometry is unavailable.");
            return new CustomModelData { name = asset.name, vertices = asset.vertices.Select(value => (float)value).ToArray(), triangles = asset.triangles.ToArray(),
                mounts = DocumentModel.Mounts(part).Select(mount => new CustomMountData {
                    x = (float)mount.position[0], y = (float)mount.position[1], z = (float)mount.position[2],
                    qx = (float)mount.rotation[0], qy = (float)mount.rotation[1], qz = (float)mount.rotation[2], qw = (float)mount.rotation[3],
                    diameter = (float)mount.diameter, depth = (float)mount.depth, threaded = mount.threaded
                }).ToArray() };
        }
        public static BuildData ToBuild(Document document) {
            var local = SceneBuild.ToBuildData();
            local.parts = document.parts.Select(part => ToObjectData(part, DocumentModel.Assets(document))).ToArray();
            local.chains = DocumentModel.Chains(document).Select(c => new ChainData { id = c.id, members = c.members.ToArray(), routeVersion = c.routeVersion, wraps = c.wraps?.ToArray() }).ToArray(); return local;
        }
        public static void Load(Document document) {
            SceneBuild.GenerateBuild(ToBuild(document), validateRoutes: false);
            Simulation.ChainLoop.ApplyRecords(DocumentModel.Chains(document));
            // The file adapter uses Euler angles; restore the accepted quaternion without a round trip.
            ApplyParts(document.parts);
            StateSystems.StateSystem.instance?.ResetStates();
        }
        public static void Apply(AcceptedEdit edit, Document accepted) {
            var existing = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            var replacements = edit.transaction.writes.Where(p => existing.TryGetValue(p.id, out var body) && RequiresReplacement(body, p)).Select(p => p.id).ToHashSet();
            var ids = edit.transaction.deletes.ToHashSet();
            var reselect = new List<(SelectionManager manager, string[] ids, bool individual)>();
            foreach (var manager in UnityEngine.Object.FindObjectsByType<SelectionManager>(FindObjectsSortMode.None)) {
                if (manager.current?.gameObject == null) continue;
                var selected = BuildTransfer.SelectedBodies(manager.current).Select(b => b.GetComponent<SavedObject>().ConnectionId).ToArray();
                if (selected.Any(replacements.Contains) && !selected.Any(ids.Contains)) reselect.Add((manager, selected, manager.current.individual));
                if (selected.Any(id => ids.Contains(id) || replacements.Contains(id)))
                    manager.ClearCurrent(false, true, true);
            }
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            var changed = edit.transaction.writes.Select(p => p.id).Concat(edit.transaction.deletes).Where(bodies.ContainsKey).Select(id => bodies[id]).ToArray();
            var detectors = HoleDetector.GetAffectedDetectors(changed);
            foreach (var id in edit.transaction.deletes) if (bodies.TryGetValue(id, out var body)) { body.SetActive(false); UnityEngine.Object.Destroy(body); }
            foreach (var write in edit.transaction.writes) {
                var part = accepted.parts.First(p => p.id == write.id);
                bodies.TryGetValue(part.id, out var body);
                if (body != null && replacements.Contains(part.id)) { body.SetActive(false); UnityEngine.Object.Destroy(body); body = null; }
                if (body == null) body = SceneBuild.GenerateObject(ToObjectData(part, DocumentModel.Assets(accepted)), ToBuild(accepted));
                else ApplyMountLayout(body, part);
                ApplyAppearance(body, part);
                SetPose(body, part);
            }
            foreach (var selection in reselect) {
                var selected = PartsManager.FindLoadedObjects().Where(b => selection.ids.Contains(b.GetComponent<SavedObject>().ConnectionId)).ToList();
                if (selected.Count > 0) selection.manager.SetCurrent(selected.Count == 1 ? new ObjectSelection { gameObject = selected[0], individual = selection.individual } :
                    new MultiSelection(null, selected) { individual = selection.individual }, true, bypassResponseSelectors: true);
            }
            Physics.SyncTransforms();
            foreach (var write in edit.transaction.writes) {
                var body = PartsManager.FindLoadedObjects().FirstOrDefault(b => b.GetComponent<SavedObject>().ConnectionId == write.id);
                if (body != null) HoleDetector.RefreshPart(body, detectors);
            }
            foreach (var detector in detectors) if (detector != null && detector.isActiveAndEnabled) detector.RefreshOverlaps();
            foreach (var manager in UnityEngine.Object.FindObjectsByType<SelectionManager>(FindObjectsSortMode.None))
                if (manager.current is MultiSelection multi && multi.pivot != null && BuildTransfer.SelectedBodies(multi)
                    .Any(b => edit.transaction.writes.Any(p => p.id == b.GetComponent<SavedObject>().ConnectionId))) multi.pivot.Center();
            foreach (var outlines in UnityEngine.Object.FindObjectsByType<GroupOutlineSelectionResponse>(FindObjectsSortMode.None)) { outlines.ClearDisplay(); outlines.SetSelectionDisplay(); }
            UnityEngine.Object.FindFirstObjectByType<PartListOutput>()?.CalculatePartsList();
            if (DocumentModel.ChainWrites(edit.transaction).Length > 0 || DocumentModel.ChainDeletes(edit.transaction).Length > 0)
                Simulation.ChainLoop.ApplyRecords(DocumentModel.Chains(accepted));
        }
        public static void ApplyParts(IEnumerable<Part> parts) {
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            foreach (var part in parts) if (bodies.TryGetValue(part.id, out var body)) SetPose(body, part);
            Physics.SyncTransforms();
        }
        public static void ApplyCustomMounts(IEnumerable<Part> parts) {
            var layouts = (parts ?? Enumerable.Empty<Part>()).ToArray();
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            var changed = layouts.Where(part => bodies.ContainsKey(part.id)).Select(part => bodies[part.id]).Distinct().ToArray();
            var detectors = HoleDetector.GetAffectedDetectors(changed);
            foreach (var part in layouts) if (bodies.TryGetValue(part.id, out var body)) ApplyMountLayout(body, part);
            Physics.SyncTransforms();
            foreach (var body in changed) HoleDetector.RefreshPart(body, detectors);
            foreach (var detector in detectors) if (detector != null && detector.isActiveAndEnabled) detector.RefreshOverlaps();
        }
        private static void SetPose(GameObject body, Part part) {
            // Work in world space; selection and connection pivots remain temporary, local objects.
            body.transform.SetPositionAndRotation(new Vector3((float)part.position[0], (float)part.position[1], (float)part.position[2]), Rotation(part));
        }
        private static void ApplyAppearance(GameObject body, Part part) {
            body.GetComponent<SavedObject>().state = part.state;
            var color = new Color((float)part.color[0], (float)part.color[1], (float)part.color[2]);
            if (part.hasPaint) PartColors.ApplyPaint(body, color);
            else {
                PartColors.RestoreDefaults(body);
                var material = PartColors.SampleMaterial(body);
                if (material != null) { color.a = material.color.a; material.color = color; }
            }
        }
        private static bool RequiresReplacement(GameObject body, Part part) {
            if (body.GetComponent<SavedObject>().id != part.catalogId) return true;
            if (part.catalogId != CustomModel.PartId) return false;
            return CaptureAsset(body.GetComponent<CustomModel>().Snapshot()).hash != part.assetHash;
        }
        private static void ApplyMountLayout(GameObject body, Part part) {
            var model = body.GetComponent<CustomModel>();
            if (model == null || part.catalogId != CustomModel.PartId) return;
            var current = model.Snapshot().mounts.Select(CaptureMount).ToArray();
            var accepted = DocumentModel.Mounts(part);
            if (current.Length == accepted.Length && current.Zip(accepted, SameMount).All(equal => equal)) return;
            model.ApplyMountLayout(accepted.Select(mount => new CustomMountData {
                x = (float)mount.position[0], y = (float)mount.position[1], z = (float)mount.position[2],
                qx = (float)mount.rotation[0], qy = (float)mount.rotation[1], qz = (float)mount.rotation[2], qw = (float)mount.rotation[3],
                diameter = (float)mount.diameter, depth = (float)mount.depth, threaded = mount.threaded
            }).ToArray());
        }
        private static bool SameMount(CustomMount a, CustomMount b) {
            if (a == null || b == null || a.position?.Length != 3 || b.position?.Length != 3 || a.rotation?.Length != 4 || b.rotation?.Length != 4) return false;
            bool position = a.position.Zip(b.position, (x, y) => Math.Abs(x - y) < .000001).All(equal => equal);
            double dot = Math.Abs(a.rotation.Zip(b.rotation, (x, y) => x * y).Sum());
            return position && Math.Abs(1 - dot) < .000001 && Math.Abs(a.diameter - b.diameter) < .000001 &&
                Math.Abs(a.depth - b.depth) < .000001 && a.threaded == b.threaded;
        }
        private static Quaternion Rotation(Part part) => new Quaternion((float)part.rotation[0], (float)part.rotation[1], (float)part.rotation[2], (float)part.rotation[3]);
    }
}
