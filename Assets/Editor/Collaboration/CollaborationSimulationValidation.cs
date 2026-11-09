using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationSimulationValidation {
        private const string Key = "Protobot.CollaborationSimulationSmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true);
            EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(Key, false);
            try {
                await Check();
                Debug.Log("Collaboration simulation validation passed: rigid-set Move, private mouse/timeline/playback, no preview ghosts or reservations, accepted file saves, mouse and Set pose commits, compound personal undo/redo, remote edit invalidation, stale graph rejection, contention and disconnect recovery.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            GameObject.Find("Splash Screen")?.SetActive(false);
            var center = new Vector3(100, 30, -50);
            var rotation = Quaternion.Euler(23, 37, 11);
            var axis = rotation * Vector3.forward;
            var parts = new List<GameObject>();
            GameObject Part(string id, Vector3 point) {
                var body = PartsManager.GeneratePart(id, point, rotation); parts.Add(body); return body;
            }
            var shaft = Part("SHFT-Normal-6", center);
            var gear = Part("GEAR-Normal-12T", center).GetComponent<SimulationPart>();
            gear.transform.position += center - gear.Center;
            var output = Part("GEAR-High Strength v2-36T", center).GetComponent<SimulationPart>();
            var outputCenter = center + rotation * Vector3.right * (gear.Radius + output.Radius);
            output.transform.position += outputCenter - output.Center;
            Part("SHFT-Normal-6", outputCenter);
            var collar = Part("CLMP-Normal-Normal", center + axis);
            var clamp = collar.GetComponentsInChildren<HoleCollider>().First(h => h.holeType == HoleCollider.HoleType.Clamp);
            collar.transform.position += center + axis - clamp.transform.position;
            var bearing = Part("BRNG-Normal", center - axis);
            var motor = Part("MOTR-11W", center - axis * 2);
            motor.transform.rotation = Quaternion.FromToRotation(motor.GetComponent<Motor>().ShaftHole.transform.forward, axis) * motor.transform.rotation;
            motor.transform.position += center - axis * 2 - motor.GetComponent<Motor>().ShaftHole.transform.position;
            Physics.SyncTransforms();
            string shaftId = shaft.GetComponent<SavedObject>().ConnectionId;
            string gearId = gear.GetComponent<SavedObject>().ConnectionId;
            string outputId = output.GetComponent<SavedObject>().ConnectionId;
            string motorId = motor.GetComponent<SavedObject>().ConnectionId;
            string bearingId = bearing.GetComponent<SavedObject>().ConnectionId;
            var catalog = CollaborationScene.Catalog;
            CollaborationSession.EnableDevelopment();
            var session = Object.FindFirstObjectByType<CollaborationSession>();
            Set(session, "port", "0");
            await (Task)Invoke(session, "HostAsync");
            var connection = Get<CollaborationClient>(session, "client");
            Require(connection?.IsConnected == true, "Could not host simulation fixture.");
            using var peer = new CollaborationClient(Get<string>(session, "endpoint"), "team", catalog, CollaborationTransport.LongPolling);
            await peer.JoinAsync("Simulation observer");
            var selection = Get<SelectionManager>(Object.FindFirstObjectByType<KeyboardTransformTool>(), "selectionManager");
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            GameObject Body(string id) => PartsManager.FindLoadedObjects().Single(b => b.GetComponent<SavedObject>().ConnectionId == id);
            Document Document() => Get<Document>(session, "committed");
            async Task Accepted(long revision) {
                await Until(() => Document().revision == revision && !CollaborationSession.HasPending && CollaborationSession.CanPreview);
                await peer.WaitForRevisionAsync(revision);
                Require(DocumentModel.Hash(Document()) == DocumentModel.Hash(peer.Snapshot), "Simulation transaction diverged.");
            }
            async Task History(bool redo = false) {
                long revision = Document().revision + 1;
                if (redo) Protobot.StateSystems.StateSystem.instance.Redo(); else Protobot.StateSystems.StateSystem.instance.Undo();
                await Accepted(revision);
            }
            void Begin() {
                SimulationTool.ExitForEdit();
                selection.SetCurrent(new ObjectSelection { gameObject = Body(shaftId), individual = true }, true, true);
                EventSystem.current.SetSelectedGameObject(null);
                tool.Activate();
                Require(tool.Current != null && tool.Current.tracks.Count >= 4, "Tool 5 did not discover the whole drivetrain.");
            }
            void Matches(Document expected) {
                foreach (var part in expected.parts) {
                    var live = CollaborationScene.CaptureLive(Body(part.id));
                    Require(Vector3.Distance(Body(part.id).transform.position, Position(part)) < .001f && Quaternion.Angle(Rotation(live), Rotation(part)) < .001f, "Incorrect materialized mechanism pose: " + part.catalogId);
                }
            }

            // Existing Move math publishes a selected rigid set as one edit, without moving the motor.
            var start = WireJson.Clone(Document());
            selection.SetCurrent(new MultiSelection(null, new List<GameObject> { Body(shaftId), Body(gearId) }) { individual = true }, true, true);
            var keyboard = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var placement = Object.FindFirstObjectByType<Placement>();
            Get<MoveToolManager>(keyboard, "moveToolManager").StartMoving();
            await Until(() => placement.placing);
            var source = selection.current.gameObject.transform;
            var pivotPosition = source.position;
            var turn = Quaternion.AngleAxis(90, Vector3.forward);
            Set(placement, "currentDisplacement", new Protobot.Transformations.Displacement(pivotPosition + Vector3.up * 3, turn * source.rotation));
            long next = Document().revision + 1;
            Invoke(placement, "Place");
            await Accepted(next);
            foreach (var id in new[] { shaftId, gearId }) {
                var original = start.parts.Single(p => p.id == id);
                Require(Vector3.Distance(Body(id).transform.position, pivotPosition + Vector3.up * 3 + turn * (Position(original) - pivotPosition)) < .001f &&
                    Quaternion.Angle(Body(id).transform.rotation, turn * Rotation(original)) < .001f, "Move applied a selection pivot twice or omitted a body.");
            }
            Require(DocumentModel.SamePart(start.parts.Single(p => p.id == motorId), Document().parts.Single(p => p.id == motorId)), "Move changed the stationary motor.");
            await History(); Matches(start);

            var selectedFace = Get<HoleFace>(selection.GetComponent<HoleFaceResponseSelector>(), "selectHoleFace");
            var hole = Body(gearId).GetComponentsInChildren<HoleCollider>().First();
            selection.SetCurrent(new HoleSelection { gameObject = selectedFace.gameObject, holeFace = selectedFace, holeData = hole.holeData,
                faceDirection = hole.transform.forward, individual = true }, true, true);
            Get<MoveToolManager>(keyboard, "moveToolManager").StartMoving();
            await Until(() => placement.placing);
            selectedFace.Refresh();
            Set(placement, "currentDisplacement", new Protobot.Transformations.Displacement(selectedFace.position + Vector3.up * 2, selectedFace.transform.rotation));
            next = Document().revision + 1; Invoke(placement, "Place"); await Accepted(next);
            Require(Vector3.Distance(Body(gearId).transform.position, Position(start.parts.Single(p => p.id == gearId)) + Vector3.up * 2) < .001f, "Hole Move tried to publish its marker as a part.");
            await History(); Matches(start);

            // The legacy shaft-position controller must use the same reserved, resolved-pose path.
            var holder = new GameObject("Shared shaft button validation");
            var shaftLink = holder.AddComponent<SerializedObjectLink>(); Set(shaftLink, "serializedObj", Body(shaftId));
            var motorLink = holder.AddComponent<SerializedObjectLink>(); Set(motorLink, "serializedObj", Body(motorId));
            var shaftButtons = holder.AddComponent<NewMoveShaftTool>();
            Set(shaftButtons, "shaftObjLink", shaftLink); Set(shaftButtons, "targetObjLink", motorLink); Set(shaftButtons, "curShaft", Body(shaftId));
            var motorTransform = Body(motorId).transform;
            var shaftTarget = motorTransform.position - motorTransform.right * .5f + motorTransform.forward * (shaftButtons.GetShaftLength() + .85f);
            next = Document().revision + 1; shaftButtons.SetShaftPosition(false); await Accepted(next);
            Require(Vector3.Distance(Body(shaftId).transform.position, shaftTarget) < .001f && Vector3.Dot(Body(shaftId).transform.forward, motorTransform.forward) > .9999f,
                "Shaft button did not publish its resolved motor attachment pose.");
            await History(); Matches(start);
            next = Document().revision + 1; shaftButtons.SetShaftPosition(true); await Accepted(next);
            Require(Vector3.Dot(Body(shaftId).transform.forward, motorTransform.forward) < -.9999f, "Reverse shaft button did not publish its direction.");
            await History(); Matches(start); Object.Destroy(holder);

            int ghostFrames = 0;
            peer.Presence += batch => { if (batch.previews.Any(p => p.author == connection.Participant.id)) Interlocked.Increment(ref ghostFrames); };
            Begin();
            var baseline = WireJson.Clone(Document());
            tool.Seek(1);
            Require(Quaternion.Angle(Body(shaftId).transform.rotation, Rotation(baseline.parts.Single(p => p.id == shaftId))) > 80, "Mouse preview did not rotate the shaft locally.");
            Require(Quaternion.Angle(Body(outputId).transform.rotation, Rotation(baseline.parts.Single(p => p.id == outputId))) > 20, "Gear ratio did not propagate locally.");
            Require(!CollaborationSession.HasReservation && BuildTransfer.Capture(selection.current) == null, "Private simulation took a lease or leaked into clipboard export.");
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            string savedPath = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "private-animation-" + Guid.NewGuid().ToString("N") + ".pbb");
            manager.buildPath = savedPath;
            Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Save during local animation failed.");
            Require(manager.CurrentBuildIsSaved, "Private animation dirtied the saved accepted document.");
            var saved = BuildsManager.ParsePath(savedPath);
            Require(Quaternion.Angle(saved.parts.Single(p => p.connectionId == shaftId).GetRot(), Rotation(baseline.parts.Single(p => p.id == shaftId))) < .001f,
                "File save included a private animation pose.");
            await Task.Delay(350);
            Require(peer.Snapshot.revision == baseline.revision && DocumentModel.Hash(peer.Snapshot) == DocumentModel.Hash(baseline) && ghostFrames == 0,
                "Animation preview was published before confirmation.");
            tool.Cancel(); Matches(baseline);
            Require(Document().revision == baseline.revision, "Cancel changed shared history.");

            // Confirm through the actual mouse-input path, rather than calling the network API directly.
            Begin(); tool.Seek(1);
            var expected = tool.Current.tracks.Where(t => t.rotates).Select(t => CollaborationScene.CaptureLive(t.body)).ToArray();
            var point = new Vector2(Screen.width * .6f, Screen.height * .55f);
            Point(point); Set(tool, "lastPointer", point); Set(tool, "beganFrame", -1);
            Require(!MouseInput.overUI, "Mouse confirmation point is covered by UI.");
            Point(point, true);
            Invoke(tool, "ReadInput", Mouse.current, Keyboard.current);
            Point(point);
            await Accepted(baseline.revision + 1);
            Require(tool.Current == null && !tool.IsCommitting, "Mouse confirmation kept an active private preview.");
            foreach (var part in expected) Require(Quaternion.Angle(Rotation(part), Rotation(peer.Snapshot.parts.Single(p => p.id == part.id))) < .001f, "Mouse confirmation lost a drivetrain result.");
            Require(DocumentModel.SamePart(baseline.parts.Single(p => p.id == motorId), Document().parts.Single(p => p.id == motorId)) &&
                DocumentModel.SamePart(baseline.parts.Single(p => p.id == bearingId), Document().parts.Single(p => p.id == bearingId)), "Simulation moved stationary housings or bearings.");
            var confirmed = WireJson.Clone(Document());
            await History(); Matches(baseline);
            await History(true); Matches(confirmed);
            await History();

            Begin(); tool.TogglePlayback();
            await Task.Delay(60);
            Invoke(tool, "ReadInput", Mouse.current, Keyboard.current);
            Require(tool.IsPlaying && tool.AnimationVisible && tool.Current.Time > 0, "Timeline playback did not advance locally.");
            tool.TogglePlayback(); tool.ResetMotion(); Matches(Document());
            tool.Seek(.5);
            long beforeTimeline = Document().revision;
            await Task.Delay(200);
            Require(peer.Snapshot.revision == beforeTimeline && ghostFrames == 0, "Timeline scrub/playback leaked over the network.");
            var setPose = Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(b => b.name == "Set Pose");
            Require(setPose.interactable, "Timeline Set pose is unavailable.");
            setPose.onClick.Invoke();
            await Accepted(beforeTimeline + 1);
            Require(tool.Current == null, "Timeline Set pose did not finish its preview.");

            // A teammate may edit during the private preview. Restore it before applying their result.
            Begin(); tool.Seek(.75);
            var remote = WireJson.Clone(peer.Snapshot.parts.Single(p => p.id == shaftId));
            var remoteRotation = Quaternion.AngleAxis(15, Body(shaftId).transform.forward) * Rotation(remote);
            remote.rotation = new double[] { remoteRotation.x, remoteRotation.y, remoteRotation.z, remoteRotation.w };
            var hold = await peer.ReserveAsync(new[] { shaftId });
            var remoteReceipt = await peer.SubmitAsync(peer.CreateTransaction("SetPoses", new[] { remote }, lease: hold.token));
            await Accepted(remoteReceipt.revision);
            Require(tool.Current == null && Vector3.Distance(Body(shaftId).transform.position, Position(remote)) < .001f, "Remote edit did not invalidate the local simulation safely.");
            tool.Cancel(); Matches(Document());

            // Even an unrelated create can change the discovered graph; a frozen old result must reject.
            Begin(); tool.Seek(.5);
            long oldRevision = Document().revision;
            var frozen = tool.Current.tracks.Select(t => CollaborationScene.CaptureLive(t.body)).ToArray();
            var oldVersions = CollaborationSession.Versions(frozen.Select(p => p.id));
            var extra = WireJson.Clone(remote); extra.id = Guid.NewGuid().ToString("N"); extra.position[0] += 50;
            var added = await peer.SubmitAsync(peer.CreateTransaction("AddParts", new[] { extra }));
            await Accepted(added.revision);
            Require(tool.Current == null && !await CollaborationSession.CommitResolvedPosesAsync(frozen, oldVersions, oldRevision), "Stale mechanism graph was committed.");
            Require(Document().revision == added.revision, "Rejected stale mechanism changed history.");
            Matches(Document());

            hold = await peer.ReserveAsync(new[] { shaftId });
            Begin(); tool.Seek(.5); next = Document().revision;
            tool.Commit();
            await Until(() => tool.Current == null && !CollaborationSession.HasPending);
            Require(Document().revision == next, "A denied simulation reservation partly changed the mechanism.");
            Matches(Document()); await peer.ReleaseAsync(hold.token);

            Begin(); tool.Seek(.5);
            connection.Suspend("Simulation test: disconnect during private preview.");
            await Until(() => !SimulationTool.HasPreview && !SimulationTool.IsActive);
            Matches(Document());
            await (Task)Invoke(session, "ReconnectAsync");
            Require(connection.IsConnected, "Simulation disconnect prevented reconnect.");
            Matches(Document());
            Require(ghostFrames == 0, "Private simulation sent ghost frames.");
            await peer.LeaveAsync();
            var owned = Get<CollaborationHost>(session, "host");
            Invoke(session, "Detach"); await owned.StopAsync();
            File.Delete(savedPath);
        }
        private static Vector3 Position(Part part) => new((float)part.position[0], (float)part.position[1], (float)part.position[2]);
        private static Quaternion Rotation(Part part) => new((float)part.rotation[0], (float)part.rotation[1], (float)part.rotation[2], (float)part.rotation[3]);
        private static void Point(Vector2 point, bool down = false) {
            foreach (var mouse in InputSystem.devices.OfType<Mouse>()) {
                var state = new MouseState { position = point };
                if (down) state = state.WithButton(MouseButton.Left);
                InputSystem.QueueStateEvent(mouse, state);
            }
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        }
        private static async Task Until(Func<bool> predicate) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate()) { if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("Collaboration simulation scenario timed out."); await Task.Delay(20); }
        }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
