using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationValidation {
        private const string Key = "Protobot.CollaborationSmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try {
                await Check();
                Debug.Log("Collaboration validation passed: actual managed helper launch, Unity shared-client WebSocket/HTTPS-request transports, catalog/custom materialization and import, silent custom-mount leases/confirmation/history, reservation/ghost/cancel, exact G/R confirmation, preview-safe snapshots, remote delete, path isolation and helper shutdown.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var a = PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            var b = PartsManager.GeneratePart("PLTE-3-3", Vector3.right * 10, Quaternion.Euler(17, 31, 12));
            b.GetComponent<SavedObject>().state = "collaboration-fixture";
            PartColors.ApplyPaint(b, new Color(.2f, .3f, .4f));
            string paintedId = b.GetComponent<SavedObject>().ConnectionId;
            var catalog = CollaborationScene.Catalog;
            var initial = CollaborationScene.CaptureDocument(catalog);
            Require(initial.parts.Length == 2, "Host capture must include exactly two saved parts.");
            var savedPath = "collaboration-local-path.pbb";
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            manager.buildPath = savedPath;
            Set(manager, "savedBuildData", SceneBuild.ToBuildData());
            CollaborationSession.EnableDevelopment();
            var session = Object.FindFirstObjectByType<CollaborationSession>();
            Set(session, "port", "0"); Set(session, "displayName", "Hosting editor");
            // The scene uses exactly the same private helper lifecycle as players.
            await (Task)Invoke(session, "HostAsync");
            Require(CollaborationSession.IsShared && manager.buildPath == savedPath, "Hosting must retain local file association.");
            Require(manager.CurrentBuildIsSaved, "Hosting changed the current local file's saved marker.");
            var client = Get<CollaborationClient>(session, "client");
            Require(client != null && client.IsConnected, "Hosting editor did not connect.");
            var url = Get<string>(session, "endpoint");
            using var bob = new CollaborationClient(url, "team", catalog, CollaborationTransport.LongPolling);
            await bob.JoinAsync("Second editor");
            var added = WireJson.Clone(initial.parts[0]); added.id = Guid.NewGuid().ToString("N"); added.position[0] = 30;
            Require((await bob.SubmitAsync(bob.CreateTransaction("AddParts", new[] { added }))).accepted, "Remote add rejected.");
            await Until(() => PartsManager.FindLoadedObjects().Count == 3);
            Require(manager.buildPath == savedPath && CollaborationSession.SaveSnapshot().parts.Length == 3, "Remote events changed local path or omitted committed parts.");
            Require(!manager.CurrentBuildIsSaved, "Remote create did not dirty the host's existing local file marker.");
            var id = added.id;
            var body = PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == id);
            var ghostLease = await bob.ReserveAsync(new[] { id });
            var ghostPart = WireJson.Clone(added); ghostPart.position[0] += 5;
            await bob.PreviewAsync(new Preview { lease = ghostLease.token, sequence = 1, parts = new[] { ghostPart } });
            await Until(() => Get<System.Collections.Generic.Dictionary<string, GameObject>>(session, "ghosts").Count == 1);
            Require(PartsManager.FindLoadedObjects().Count == 3 && body.transform.position.x == 30 &&
                CollaborationSession.SaveSnapshot().parts.Single(p => p.connectionId == id).xPos == 30, "Remote ghost changed colliders/saved bodies or accepted snapshot.");
            await bob.ReleaseAsync(ghostLease.token);
            await Until(() => Get<System.Collections.Generic.Dictionary<string, GameObject>>(session, "ghosts").Count == 0);
            var selection = Object.FindFirstObjectByType<SelectionManager>();
            // Locate the same primary selection used by the keyboard transform controller.
            var keyboard = Object.FindFirstObjectByType<KeyboardTransformTool>();
            // Batch Mode has no focused game window. Drive the real commit/cancel methods explicitly.
            keyboard.enabled = false;
            selection = Get<SelectionManager>(keyboard, "selectionManager");
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            var baseline = body.transform.position;
            Set(keyboard, "displayedOffset", Vector3.up * 2);
            Invoke(keyboard, "ApplyDisplayedTransform");
            Require(Vector3.Distance(body.transform.position, baseline + Vector3.up * 2) < .001f, "G preview did not use actual Unity roots.");
            var savedPreview = CollaborationSession.SaveSnapshot().parts.Single(p => p.connectionId == id);
            Require(Math.Abs(savedPreview.yPos - baseline.y) < .001, "Save included uncommitted transform preview.");
            Invoke(keyboard, "Finish", false);
            Require(Vector3.Distance(body.transform.position, baseline) < .001f, "Escape/cancel did not restore pose.");
            await Task.Delay(150);
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            Invoke(keyboard, "SetTarget", Vector3.up * 3, Quaternion.identity);
            Invoke(keyboard, "CompleteConfirmation");
            Require(await CollaborationSession.WaitForSaveAsync(), "Save barrier did not resolve this participant's accepted edit.");
            Require((await WaitPart(bob, id, 3)).position[1] == 3, "G confirmation did not reach second client.");
            var acceptedSave = CollaborationSession.SaveSnapshot();
            Require(acceptedSave.parts.Single(p => p.connectionId == id).yPos == 3, "Accepted pose omitted from save snapshot.");
            savedPath = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "accepted-collaboration-" + Guid.NewGuid().ToString("N") + ".pbb");
            manager.buildPath = savedPath;
            Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Accepted local save failed.");
            Require(BuildsManager.ParsePath(savedPath).parts.Single(p => p.connectionId == id).yPos == 3 &&
                !(bool)Invoke(manager, "HasUnsavedChanges"), "Local file/marker omitted accepted revision.");
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            Invoke(keyboard, "SetTarget", Vector3.up, Quaternion.identity);
            client.Suspend("Development test: disconnect immediately before confirmation.");
            Invoke(keyboard, "CompleteConfirmation");
            Require(body.transform.position.y == 3, "Late confirmation after disconnect did not restore the accepted pose.");
            await (Task)Invoke(session, "ReconnectAsync");
            Require(client.IsConnected && manager.buildPath == savedPath, "Reconnect lost membership or local file association.");
            // Verify render/material mapping independently of the accepted network hash.
            var painted = PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == paintedId);
            Require(painted.GetComponent<SavedObject>().state == "collaboration-fixture" && painted.GetComponent<SavedObject>().hasPaint &&
                Math.Abs(PartColors.GetColor(painted).g - .3f) < .001f, "Materialization lost paint/state.");
            await CheckTools(session, bob, selection, keyboard, id, savedPath);
            var reservation = await bob.ReserveAsync(new[] { id });
            Require((await bob.SubmitAsync(bob.CreateTransaction("DeleteParts", Array.Empty<Part>(), new[] { id }, lease: reservation.token))).accepted, "Remote delete rejected.");
            await Until(() => PartsManager.FindLoadedObjects().Count == 2);
            Require(selection.current == null, "Remote deletion left invalid selection.");
            Require(manager.buildPath == savedPath, "A participant changed the host's local save path.");
            Require((bool)Invoke(manager, "HasUnsavedChanges"), "A newer remote edit did not leave the saved participant dirty.");
            Require(BuildsManager.ParsePath(savedPath).parts.Length == 3, "Remote edit modified an already saved local file.");
            File.Delete(savedPath);
            // Check Unity's WebSocket runtime separately from the forced request client.
            await Until(() => client.ActiveTransport == "websocket");
            await bob.LeaveAsync();
            var owned = Get<CollaborationHost>(session, "host");
            Invoke(session, "Detach");
            await Get<Task>(session, "hostShutdown");
            await owned.StopAsync();
            Require(!CollaborationSession.IsShared && PartsManager.FindLoadedObjects().Count == 2, "Leave must preserve last accepted local document.");
            // Join path uses normal file-save handling and clears path only for newly joined content.
            await CheckJoinAsync(catalog, initial, session, manager);
        }
        private static async Task CheckTools(CollaborationSession session, CollaborationClient peer, SelectionManager selection, KeyboardTransformTool keyboard, string id, string savedPath) {
            GameObject Body() => PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == id);
            Document Document() => Get<Document>(session, "committed");
            async Task Changed(long revision) {
                await Until(() => Document().revision > revision && !CollaborationSession.HasPending && CollaborationSession.CanEdit);
                await peer.WaitForRevisionAsync(Document().revision);
                Require(DocumentModel.Hash(peer.Snapshot) == DocumentModel.Hash(Document()), "Tool transaction diverged between participants.");
            }
            async Task History(bool redo = false) {
                long revision = Document().revision;
                if (redo) Protobot.StateSystems.StateSystem.instance.Redo(); else Protobot.StateSystems.StateSystem.instance.Undo();
                await Changed(revision);
            }
            long rev = Document().revision;
            ColorToolActiveCheck.colorToolActive = true;
            ColorTool.ColorToSet = new Color(.7f, .2f, .1f);
            Object.FindFirstObjectByType<ColorSelectionResponse>().ChangeColor(Body());
            await Changed(rev);
            Require(PartColors.Materials(Body()).All(m => Math.Abs(m.color.r - .7f) < .001f) && Body().GetComponent<SavedObject>().hasPaint, "Shared paint did not update actual material slots.");
            rev = Document().revision;
            Object.FindFirstObjectByType<ColorTool>(FindObjectsInactive.Include).HandleInput(0);
            ColorToolActiveCheck.colorToolActive = true;
            Object.FindFirstObjectByType<ColorSelectionResponse>().ChangeColor(Body());
            await Changed(rev);
            Require(!Body().GetComponent<SavedObject>().hasPaint && PartColors.IsDefault(Body()), "Shared Default did not restore generated colors.");
            ColorToolActiveCheck.colorToolActive = false;

            selection.SetCurrent(new ObjectSelection { gameObject = Body(), individual = true }, true, true);
            var properties = Object.FindFirstObjectByType<Protobot.UI.AddPropertiesUI>();
            Invoke(properties, "ObserveSelection");
            rev = Document().revision;
            properties.UpdateParam2("7");
            await Changed(rev);
            Require(Body().GetComponent<SavedObject>().id == "CCHL-1x2-7" && Body().GetComponentsInChildren<Collider>().Length > 0 &&
                BuildTransfer.SelectedBodies(selection.current).Contains(Body()), "Shared parameter replacement lost geometry, identity or selection.");
            await History(); Require(Body().GetComponent<SavedObject>().id == "CCHL-1x2-5", "Property undo did not regenerate old geometry.");
            await History(true); Require(Body().GetComponent<SavedObject>().id == "CCHL-1x2-7", "Property redo did not regenerate new geometry.");

            var placement = Object.FindFirstObjectByType<Placement>();
            var moveHole = Body().GetComponentInChildren<HoleCollider>();
            var moveFace = Get<HoleFace>(selection.GetComponent<HoleFaceResponseSelector>(), "selectHoleFace");
            moveFace.Set(moveHole.holeData, moveHole.transform.forward);
            selection.SetCurrent(new HoleSelection { gameObject = moveFace.gameObject, holeData = moveHole.holeData,
                holeFace = moveFace, faceDirection = moveHole.transform.forward, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            Invoke(keyboard, "MoveFromGrab");
            await Until(() => placement.placing);
            placement.CancelPlacement();
            await Until(() => CollaborationSession.CanEdit);
            int handoffFrame = Time.frameCount;
            await Until(() => Time.frameCount > handoffFrame);
            selection.SetCurrent(new ObjectSelection { gameObject = Body(), individual = true }, true, true);
            rev = Document().revision;
            Get<MoveToolManager>(keyboard, "moveToolManager").StartMoving();
            await Until(() => placement.placing);
            var target = Body().transform.position + Vector3.up * 2;
            Set(placement, "currentDisplacement", new Protobot.Transformations.Displacement(target, Body().transform.rotation));
            Invoke(placement, "Place");
            await Changed(rev);
            Require(Vector3.Distance(Body().transform.position, target) < .001f && Body().layer == 0 && MeshCombiner.GetRenderers(new[] { Body() }).All(r => r.enabled),
                "Shared Move did not restore visible accepted geometry: pose=" + Body().transform.position + ", expected=" + target + ", layer=" + Body().layer + ", renderers=" + string.Join(",", MeshCombiner.GetRenderers(new[] { Body() }).Select(r => r.name + ":" + r.enabled)));

            var transformTool = Object.FindObjectsByType<ToolToggle>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .First(t => Get<Protobot.InputEvents.InputEvent>(t, "toggleOnInput")?.name == "Enable Transform Tool");
            transformTool.Toggle(true);
            await Until(() => Object.FindObjectsByType<PositionAxis>(FindObjectsSortMode.None).Any(t => t.isActiveAndEnabled));
            var handle = Object.FindObjectsByType<PositionAxis>(FindObjectsSortMode.None).First(t => t.isActiveAndEnabled);
            // Release before a grant, then immediately retry: the obsolete grant must release before the next request.
            handle.OnPointerDown(new PointerEventData(EventSystem.current));
            handle.OnPointerUp(new PointerEventData(EventSystem.current));
            handle.OnPointerDown(new PointerEventData(EventSystem.current));
            Require(CollaborationSession.AllowsInput(handle.dragInput), "Reservation wait suppressed the held handle pointer.");
            await Until(() => (bool)typeof(ClickDragTool).GetField("dragging", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handle));
            target = Body().transform.position + Vector3.right * 2;
            rev = Document().revision;
            handle.movementManager.MoveTo(target);
            handle.OnPointerUp(new PointerEventData(EventSystem.current));
            await Changed(rev);
            Require(Vector3.Distance(Body().transform.position, target) < .001f, "Reserved transform handle did not commit.");

            var connect = Object.FindFirstObjectByType<ConnectTool>();
            connect.SetActive(true);
            Set(connect, "sourcePosition", Body().transform.position);
            Set(connect, "sourceVersions", CollaborationSession.Versions(new[] { id }));
            target = Body().transform.position + Vector3.forward * 2;
            rev = Document().revision;
            Invoke(connect, "MoveSelection", target);
            await Changed(rev); connect.SetActive(false);
            Require(Vector3.Distance(Body().transform.position, target) < .001f, "Shared Connect translation did not commit.");

            // Real duplicate modifier and keyboard tool; copies must be part of the same accepted edit.
            int count = PartsManager.FindLoadedObjects().Count;
            foreach (var device in InputSystem.devices.OfType<Keyboard>()) InputSystem.QueueStateEvent(device, new KeyboardState(UnityEngine.InputSystem.Key.LeftAlt));
            UpdateInput();
            var duplicateInput = Get<Protobot.InputEvents.InputEvent>(Get<ObjectDuplicator>(keyboard, "duplicator"), "input");
            bool duplicateHeld = Get<ObjectDuplicator>(keyboard, "duplicator").IsDuplicateHeld;
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            Require(Get<System.Collections.Generic.List<GameObject>>(keyboard, "duplicates").Count == 1,
                "Alt did not create a private gesture copy: count=" + Get<System.Collections.Generic.List<GameObject>>(keyboard, "duplicates").Count +
                ", heldBefore=" + duplicateHeld + ", latched=" + Get<bool>(keyboard, "duplicateForGesture") + ", enabled=" + duplicateInput.defaultAction.enabled +
                ", controls=" + string.Join(",", duplicateInput.defaultAction.controls.Select(c => c.path + ":" + c.IsPressed())));
            rev = Document().revision;
            Invoke(keyboard, "SetTarget", Vector3.up, Quaternion.identity);
            Invoke(keyboard, "CompleteConfirmation");
            foreach (var device in InputSystem.devices.OfType<Keyboard>()) InputSystem.QueueStateEvent(device, new KeyboardState());
            UpdateInput();
            await Changed(rev);
            Require(PartsManager.FindLoadedObjects().Count == count + 1, "Compound transform-copy lost its create.");
            await History(); Require(PartsManager.FindLoadedObjects().Count == count, "Compound undo left a duplicate behind.");
            await History(true); Require(PartsManager.FindLoadedObjects().Count == count + 1, "Compound redo lost a duplicate.");
            await History();

            selection.SetCurrent(new ObjectSelection { gameObject = Body(), individual = true }, true, true);
            Invoke(properties, "PlacePickedPart");
            Require(placement.CurrentSelectionPlacement != null, "P did not start shared copy placement.");
            Set(placement, "currentDisplacement", new Protobot.Transformations.Displacement(Vector3.right * 60, Quaternion.identity));
            rev = Document().revision; Invoke(placement, "Place");
            await Changed(rev); placement.CancelPlacement();
            Require(PartsManager.FindLoadedObjects().Count == count + 1, "P stamp was not accepted.");
            await History();

            var transfer = Object.FindFirstObjectByType<BuildTransferTool>();
            string clipboard = GUIUtility.systemCopyBuffer;
            try {
                Require(await transfer.CopySelectedAsync() && await transfer.PasteAsync(), "Shared clipboard workflow did not start.");
                rev = Document().revision; Invoke(placement, "Place"); await Changed(rev);
                Require(PartsManager.FindLoadedObjects().Count == count + 1 && BuildTransfer.SelectedBodies(selection.current).Count == 1, "Paste did not select accepted parts.");
                await History();
            }
            finally { GUIUtility.systemCopyBuffer = clipboard; }
            Require(await transfer.ImportBuildAsync(savedPath), "Catalog build import did not start.");
            rev = Document().revision; Invoke(placement, "Place"); await Changed(rev);
            Require(PartsManager.FindLoadedObjects().Count == count + 3 && BuildTransfer.SelectedBodies(selection.current).Count == 3, "Imported set was not accepted/selected atomically.");
            await History();

            var asset = new GeometryAsset { name = "Shared custom triangle", vertices = new[] { -1.0, -1.0, 0.0, 1.0, -1.0, 0.0, 0.0, 1.0, 0.0 },
                triangles = new[] { 0, 1, 2 } };
            asset.hash = DocumentModel.GeometryHash(asset);
            string customId = Guid.NewGuid().ToString("N");
            var customPart = new Part { id = customId, catalogId = CustomModel.PartId, assetHash = asset.hash,
                position = new[] { 70.0, 0.0, 0.0 }, mounts = new[] { new CustomMount { position = new[] { 0.0, 0.0, -.05 },
                    rotation = new[] { 0.0, 0.0, 0.0, 1.0 }, diameter = .169, depth = .1, threaded = true } } };
            rev = Document().revision;
            Require((await peer.SubmitAsync(peer.CreateTransaction("AddParts", new[] { customPart }, assetWrites: new[] { asset }))).accepted,
                "Remote custom geometry add rejected.");
            await Changed(rev);
            var customBody = PartsManager.FindLoadedObjects().Single(part => part.GetComponent<SavedObject>().ConnectionId == customId);
            Require(customBody.GetComponent<CustomModel>() != null && customBody.GetComponent<MeshFilter>().sharedMesh.vertexCount == 3 &&
                customBody.GetComponentsInChildren<CustomMountPoint>().Length == 1 && CollaborationSession.SaveSnapshot().parts.Single(p => p.connectionId == customId).customModel.mounts.Length == 1,
                "Shared custom geometry or mounting layout did not materialize/save.");
            selection.SetCurrent(new ObjectSelection { gameObject = customBody, individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false); await Until(() => Get<bool>(keyboard, "active"));
            rev = Document().revision; Invoke(keyboard, "SetTarget", Vector3.up * 2, Quaternion.identity); Invoke(keyboard, "CompleteConfirmation"); await Changed(rev);
            Require(customBody != null && Math.Abs(customBody.transform.position.y - 2) < .001f && customBody.GetComponentsInChildren<CustomMountPoint>().Length == 1,
                "Custom G movement changed or regenerated immutable geometry/mounts.");
            var customTool = Object.FindFirstObjectByType<CustomModelTool>(); customTool.ToggleMounts();
            Require(CustomModelTool.IsEditingMounts, "Connected mode did not expose shared mounting edits.");
            var customModel = customBody.GetComponent<CustomModel>();
            bool mountReserved = false;
            long mountRevision = Document().revision;
            CollaborationSession.ReserveCustomMounts(new[] { customModel }, () => mountReserved = true);
            await Until(() => mountReserved);
            await Task.Delay(175);
            Require(CollaborationSession.HasReservation && Get<System.Collections.Generic.Dictionary<string, GameObject>>(session, "ghosts").Count == 0 &&
                peer.Snapshot.revision == mountRevision, "A canceled mount reservation broadcast a ghost or document edit.");
            CollaborationSession.CancelCustomMounts();
            await Until(() => !CollaborationSession.HasReservation);

            mountReserved = false;
            CollaborationSession.ReserveCustomMounts(new[] { customModel }, () => mountReserved = true);
            await Until(() => mountReserved);
            var localMount = customModel.AddMount(new Pose(customBody.transform.TransformPoint(new Vector3(.5f, 0, 0)), customBody.transform.rotation), .2f, .08f, false);
            Require(localMount != null, "Shared mount fixture could not create its local confirmed point.");
            await Task.Delay(175);
            Require(CollaborationSession.SaveSnapshot().parts.Single(p => p.connectionId == customId).customModel.mounts.Length == 1 &&
                DocumentModel.Mounts(peer.Snapshot.parts.Single(p => p.id == customId)).Length == 1 &&
                Get<System.Collections.Generic.Dictionary<string, GameObject>>(session, "ghosts").Count == 0,
                "Unconfirmed mounting layout entered a save, remote document or ghost preview.");
            mountRevision = Document().revision;
            CollaborationSession.ConfirmCustomMounts(new[] { customModel });
            await Changed(mountRevision);
            await Until(() => DocumentModel.Mounts(peer.Snapshot.parts.Single(p => p.id == customId)).Length == 2);
            Require(customBody.GetComponentsInChildren<CustomMountPoint>().Length == 2 && DocumentModel.Assets(Document()).Single().hash == asset.hash,
                "Confirmed mounting layout did not materialize without changing immutable geometry.");
            await History();
            Require(customBody.GetComponentsInChildren<CustomMountPoint>().Length == 1 && Document().parts.Single(p => p.id == customId).assetHash == asset.hash,
                "Mount layout undo changed geometry or failed to restore the prior component.");
            await History(true);
            Require(customBody.GetComponentsInChildren<CustomMountPoint>().Length == 2, "Mount layout redo did not restore the confirmed component.");

            var remoteMounts = WireJson.Clone(peer.Snapshot.parts.Single(p => p.id == customId));
            Array.Reverse(remoteMounts.mounts); remoteMounts.mounts[0].depth = .12;
            var remoteMountLease = await peer.ReserveAsync(new[] { customId });
            mountRevision = Document().revision;
            Require((await peer.SubmitAsync(peer.CreateTransaction("SetCustomMounts", new[] { remoteMounts }, lease: remoteMountLease.token))).accepted,
                "Remote mounting layout edit rejected.");
            await Changed(mountRevision);
            Require(customModel.Snapshot().mounts.Length == 2 && Math.Abs(customModel.Snapshot().mounts[0].depth - .12f) < .001f &&
                CollaborationSession.SaveSnapshot().parts.Single(p => p.connectionId == customId).customModel.mounts.Length == 2,
                "Remote ordered mounting layout did not materialize or persist locally.");
            if (CustomModelTool.IsEditingMounts) customTool.ToggleMounts();
            Require(!CustomModelTool.IsEditingMounts, "Mounting mode did not close after shared edits.");
            rev = Document().revision; CollaborationSession.Paint(new[] { customBody }, new Color(.15f, .45f, .75f), false); await Changed(rev);
            Require(customBody.GetComponent<SavedObject>().hasPaint && Math.Abs(PartColors.GetColor(customBody).b - .75f) < .001f,
                "Shared custom-model paint did not apply.");
            selection.SetCurrent(new ObjectSelection { gameObject = customBody, individual = true }, true, true);
            string customClipboard = GUIUtility.systemCopyBuffer;
            try {
                Require(await transfer.CopySelectedAsync() && await transfer.PasteAsync(), "Shared custom-model clipboard workflow did not start.");
                rev = Document().revision; Invoke(placement, "Place"); await Changed(rev);
                Require(PartsManager.FindLoadedObjects().Count(part => part.GetComponent<CustomModel>() != null) == 2 && DocumentModel.Assets(Document()).Length == 1,
                    "Custom paste duplicated or lost immutable geometry.");
                await History();
            }
            finally { GUIUtility.systemCopyBuffer = customClipboard; }
            rev = Document().revision; CollaborationSession.Delete(new[] { customBody }); await Changed(rev);
            Require(PartsManager.FindLoadedObjects().All(part => part.GetComponent<SavedObject>().ConnectionId != customId), "Shared custom delete did not materialize.");
            await History(); Require(PartsManager.FindLoadedObjects().Any(part => part.GetComponent<SavedObject>().ConnectionId == customId), "Custom delete undo lost geometry.");
            await History(true); Require(PartsManager.FindLoadedObjects().Count == count && DocumentModel.Assets(Document()).Length == 1,
                "Custom delete redo did not restore the custom-free scene while retaining immutable geometry.");

            string modelPath = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "shared-import-" + Guid.NewGuid().ToString("N") + ".obj");
            try {
                File.WriteAllText(modelPath, "v -1 -1 0\nv 1 -1 0\nv 0 1 0\nf 1 2 3\n");
                rev = Document().revision;
                Require(await customTool.ImportPathAsync(modelPath, 1) == null, "Connected custom import must submit rather than retain a private object.");
                await Changed(rev);
                var importedCustom = PartsManager.FindLoadedObjects().Single(part => part.GetComponent<CustomModel>() != null);
                Require(DocumentModel.Assets(Document()).Length == 2 && importedCustom.GetComponent<MeshFilter>().sharedMesh.vertexCount == 3,
                    "Custom import did not publish its immutable geometry.");
                rev = Document().revision; CollaborationSession.Delete(new[] { importedCustom }); await Changed(rev);
                Require(PartsManager.FindLoadedObjects().Count == count, "Custom import cleanup did not return to the accepted catalog scene.");
            }
            finally { if (File.Exists(modelPath)) File.Delete(modelPath); }

            // Leave an operation on the catalog body at the tip of this participant's
            // history so the following remote edit specifically fences that inverse.
            rev = Document().revision; CollaborationSession.Paint(new[] { Body() }, new Color(.31f, .41f, .51f), false); await Changed(rev);
            var staleTarget = CollaborationSession.Versions(new[] { id });
            var remote = WireJson.Clone(peer.Snapshot.parts.Single(p => p.id == id)); remote.position[1] += 1;
            var reserved = await peer.ReserveAsync(new[] { id });
            var accepted = await peer.SubmitAsync(peer.CreateTransaction("SetPoses", new[] { remote }, lease: reserved.token));
            await Until(() => Document().revision == accepted.revision);
            CollaborationSession.PlaceCatalog(PartsManager.GeneratePart("PLTE-3-3", Vector3.one * 80, Quaternion.identity), staleTarget);
            await Until(() => !CollaborationSession.HasPending && Get<string>(session, "status").StartsWith("Edit rejected:"));
            Require(Document().revision == accepted.revision && PartsManager.FindLoadedObjects().Count == count, "Stale target placement changed accepted geometry.");
            Protobot.StateSystems.StateSystem.instance.Undo();
            await Until(() => !CollaborationSession.HasPending && Get<string>(session, "status").Contains("history-changed-by-another-participant"));
            Require(Document().revision == accepted.revision && Math.Abs(Body().transform.position.y - remote.position[1]) < .001, "Personal undo overwrote a remote edit.");
            selection.SetCurrent(new ObjectSelection { gameObject = Body(), individual = true }, true, true);
            Debug.Log("Collaboration tool validation passed: paint/Default, parameter replacement/selection, personal history, G-to-H, Move, pointer handle/early release, Connect, Alt copies, P, clipboard/build import, custom geometry/import/dedup/history and stale conflict refusal.");
        }
        private static async Task CheckJoinAsync(CatalogManifest catalog, Document initial, CollaborationSession session, BuildsManager manager) {
            using var host = new CollaborationHost();
            try {
                await host.StartAsync(new Bootstrap { catalog = catalog, document = initial }, 0, false);
                Set(session, "endpoint", host.Url); Set(session, "transport", CollaborationTransport.LongPolling);
                await (Task)Invoke(session, "JoinAsync");
                Require(CollaborationSession.IsShared && manager.buildPath == "", "Newly joined document must default to Save As.");
                Require(PartsManager.FindLoadedObjects().Count == initial.parts.Length, "Join did not materialize complete snapshot.");
                Invoke(session, "Detach");
            }
            finally { await host.StopAsync(); }
        }
        private static async Task<Part> WaitPart(CollaborationClient client, string id, double y) {
            await Until(() => client.Snapshot.parts.Single(p => p.id == id).position[1] == y);
            return client.Snapshot.parts.Single(p => p.id == id);
        }
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        private static async Task Until(Func<bool> predicate) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate()) { if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("Unity collaboration scenario timed out."); await Task.Delay(20); }
        }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
