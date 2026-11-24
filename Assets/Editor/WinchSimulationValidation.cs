using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.StateSystems;
using Protobot.Tools;
using Protobot.Transformations;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class WinchSimulationValidation {
        private const string Key = "Protobot.WinchSimulationSmoke";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int frames;

        public static void Smoke() => Start(false);
        public static void SharedSmoke() => Start(true);

        private static void Start(bool shared) {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetInt(Key, shared ? 2 : 1);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (SessionState.GetInt(Key, 0) == 0) return;
            frames = 0;
            EditorApplication.update += Tick;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (SessionState.GetInt(Key, 0) == 0) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick;
            bool shared = SessionState.GetInt(Key, 0) == 2;
            SessionState.EraseInt(Key);
            try {
                await Check(shared);
                Debug.Log(shared ? "Shared winch passed: route transactions, private scrubbing, accepted Set Pose, copy/paste, endpoint deletion/history, helper recovery and .pbb reopening."
                    : "Winch passed: catalog assembly, rope attachment controls, traveling load and pulley ratio, pawl, signed scrubbing and limits, cancel, Set Pose/history and .pbb reopening.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        private static async Task Check(bool shared) {
            GameObject.Find("Launch Canvas")?.SetActive(false);
            GameObject.Find("Splash Screen")?.SetActive(false);
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            var selection = Get<SelectionManager>(tool, "selectionManager");
            var placement = Object.FindFirstObjectByType<Placement>();
            var menu = Object.FindFirstObjectByType<AddPartsUI>(FindObjectsInactive.Include);
            menu.DisplayListGroup("Motion");
            var rows = menu.partUIsContainer.GetComponentsInChildren<PartDisplayUI>(true).Where(row => row.partType != null)
                .GroupBy(row => row.partType.id).ToDictionary(group => group.Key, group => group.First());
            menu.gameObject.SetActive(false);
            var bracket = Place("WBRK", new Vector3(22, 30, 40));
            var front = BracketHole("Front mounting hole 3-3");
            var rear = BracketHole("Rear mounting hole 3-3");
            var center = (front + rear) / 2;
            float side = Mathf.Sign((front - center).z);
            var shaft = PartsManager.GeneratePart("SHFT-Normal-4", center, Quaternion.identity);
            var drum = Place("WDRM", center);
            var ratchet = Place("WRAT", front + Vector3.forward * (side * .22f));
            var pawlFront = bracket.GetComponentsInChildren<HoleCollider>().First(hole => hole.name.StartsWith("Front mounting hole") &&
                (hole.transform.position - front).magnitude > 1.01f && (hole.transform.position - front).magnitude < 1.2f);
            var pawlPivot = pawlFront.transform.position + Vector3.forward * (side * .22f);
            float pawlAngle = Vector3.SignedAngle(new Vector3(.49f, -.22f, 0), ratchet.transform.position - pawlPivot, Vector3.forward);
            var pawlRotation = Quaternion.AngleAxis(pawlAngle, Vector3.forward);
            var pawlPin = PartsManager.GeneratePart("SHFT-Normal-4", pawlPivot - Vector3.forward * (side * 1.9f), pawlRotation);
            var pawl = Place("WPAW", pawlPivot, pawlRotation);
            // Matching axle Z leaves the actual pulley groove .025 in off plane.
            // A plate attachment on the other side keeps the last span skewed.
            const float loadOffset = .028f;
            var redirectPoint = center + new Vector3(5, .5f, 0);
            var redirectRotation = Quaternion.Euler(0, 1, 0);
            var axle = PartsManager.GeneratePart("SHFT-Normal-2", redirectPoint, redirectRotation);
            var pulley = Place("PULY", redirectPoint, redirectRotation);
            var attachment = center + new Vector3(9, 5, loadOffset);
            var load = PartsManager.GeneratePart("PLTE-1-1", attachment, Quaternion.identity);
            var eye = load.GetComponentsInChildren<HoleCollider>().First();
            load.transform.position += attachment - eye.transform.position;
            Physics.SyncTransforms();

            string bracketId = Id(bracket), shaftId = Id(shaft), drumId = Id(drum), pulleyId = Id(pulley), loadId = Id(load);
            string pawlId = Id(pawl), ratchetId = Id(ratchet), axleId = Id(axle), pinId = Id(pawlPin);
            var routeIds = new[] { drumId, pulleyId, loadId };
            var assemblyIds = new[] { bracketId, shaftId, drumId, pulleyId, loadId, pawlId, ratchetId, axleId, pinId };
            var builds = Object.FindFirstObjectByType<BuildsManager>();
            string path = Path.Combine(Path.GetTempPath(), "Protobot winch pose " + Guid.NewGuid().ToString("N") + ".pbb");
            CollaborationSession session = null;
            CollaborationClient peer = null;
            int ghosts = 0;
            try {
                if (shared) {
                    session = Object.FindFirstObjectByType<CollaborationSession>();
                    Set(session, "port", "0");
                    await (Task)Invoke(session, "HostAsync");
                    Require(CollaborationSession.IsShared, "Could not host winch: " + Get<string>(session, "status"));
                    peer = new CollaborationClient(Get<string>(session, "endpoint"), "team", CollaborationScene.Catalog, CollaborationTransport.LongPolling);
                    peer.Presence += batch => ghosts += batch.previews?.Length ?? 0;
                    await peer.JoinAsync("Winch observer");
                }

                CameraNavigationValidation.FocusRouteGameView();
                await InteractionOverhaulValidation.Frames();
                SelectRoute();
                tool.EditRopeRoute();
                Require(tool.RouteEditor?.IsRope == true, "Main could not open the rope editor.");
                Click("Rope Routing", "Pick Drum");
                var groove = Body(drumId).GetComponent<RopeGroove>();
                await PickDrum(Body(drumId), groove.Center);
                Require(!Label("Rope Routing", "Route status").Contains("Click"), "The editor did not accept the drum attachment.");
                Click("Rope Routing", "Pick End");
                var end = Body(loadId).GetComponentsInChildren<HoleCollider>().First();
                await Pick(Body(loadId), end.transform.position, Vector3.forward);
                Require(tool.RouteEditor.IsValid, "Initial route failed: " + Label("Rope Routing", "Route status"));
                Require(CollaborationSession.SaveSnapshot().ropes.Length == 0, "Draft was saved before Apply.");
                await Change(() => Click("Rope Routing", "Apply"));
                var initial = Rope().Data;
                Require(initial.availableLength == 48 && initial.MemberIds.SequenceEqual(routeIds), "Applied route lost its budget or ordered members.");
                await History(false); Require(Rope() == null, "Route creation undo left a route.");
                await History(true); Require(Rope()?.Data.id == initial.id, "Route creation redo lost identity.");

                SelectRoute();
                tool.EditRopeRoute();
                GameObject.Find("Rope Routing").GetComponentInChildren<InputField>().text = "26";
                await Change(() => Click("Rope Routing", "Apply"));
                Require(Rope().Data.id == initial.id && Rope().Data.availableLength == 26, "Editing changed route identity or lost the new length.");
                await History(false); Require(Rope().Data.availableLength == 48, "Undo did not restore rope length.");
                await History(true); Require(Rope().Data.availableLength == 26, "Redo did not restore the new length.");

                if (!shared) {
                    var guide = Body(pulleyId).transform;
                    var start = guide.position;
                    guide.position -= Vector3.forward * .05f;
                    var drive = Body(shaftId);
                    var initialRotation = drive.transform.rotation;
                    var blocked = MechanismSimulation.Create(drive);
                    Require(!blocked.HasMotion && blocked.WinchStatus?.Contains("one plane") == true, "Misaligned rope did not block its driven mechanism.");
                    blocked.Evaluate(1);
                    Require(drive.transform.rotation == initialRotation, "A blocked winch still spun the drive shaft.");
                    Require(!tool.Begin(drive) && tool.Current == null, "Tool 5 entered a shaft-only preview for a blocked winch.");
                    guide.position = start;
                }

                var baseline = CollaborationSession.SaveSnapshot();
                var originalLoad = Body(loadId).transform.position;
                var originalPulley = Body(pulleyId).transform.rotation;
                var pulleyCenter = Body(pulleyId).GetComponent<RopeGroove>().Center;
                float originalTail = TailLength(Rope());
                var originalWoundStart = Rope().GetComponent<MeshFilter>().sharedMesh.vertices[^2];
                var originals = assemblyIds.ToDictionary(id => id, id => new Pose(Body(id).transform.position, Body(id).transform.rotation));
                var model = Begin();
                Require(model.HasWinchPawl && model.PawlEngaged, "Mounted ratchet and pawl were not detected.");
                var travelAxis = model.WinchTravelAxis;
                Require(Vector3.Dot(travelAxis, (Body(loadId).transform.position - Body(pulleyId).transform.position).normalized) > .98f,
                    "The load travel axis does not follow the outgoing tangent.");
                tool.Seek(1);
                Near((float)model.Time, 0, "Pawl blocked payout");
                Click("Simulation Controls", "Winch Pawl");
                Require(!model.PawlEngaged, "Pawl control did not disengage it.");
                tool.ToggleAnimation();
                var timeline = GameObject.Find("Simulation Controls").GetComponentsInChildren<Slider>().Single(slider => slider.name == "Timeline");
                timeline.value = (float)((1 - model.TimelineStart) / (model.TimelineEnd - model.TimelineStart));
                CheckMotion(1);
                await Task.Delay(180);
                Require(Rope().GetComponent<MeshRenderer>().enabled, "Taut rope disappeared during preview.");
                Near(TailLength(Rope()) - originalTail, (float)model.RopeTravel, "Paid-out tail length");
                Require(baseline.CompareData(CollaborationSession.SaveSnapshot()), "Simulation preview leaked into a build snapshot.");
                if (shared) Require(ghosts == 0 && !CollaborationSession.HasReservation, "Winch preview sent ghosts or reserved parts.");

                tool.Seek(4.4);
                CheckMotion(4.4);
                await Task.Delay(35);
                Require(Rope().GetComponent<MeshRenderer>().enabled, "Rope disappeared after a complete drum turn.");
                Near(Rope().GetComponent<MeshFilter>().sharedMesh.vertices[^2], originalWoundStart, "Wound rope shifted with the drum phase");
                tool.Seek(1e6);
                Near((float)model.Time, (float)model.TimelineEnd, "Payout stop");
                await Task.Delay(35);
                Require(Rope().GetComponent<MeshRenderer>().enabled, "The rope disappeared at its payout stop.");
                tool.Seek(-1e6);
                Near((float)model.Time, (float)model.TimelineStart, "Take-up stop");
                await Task.Delay(35);
                Near(TailLength(Rope()) - originalTail, (float)model.RopeTravel, "Tail length at take-up stop");
                tool.Seek(0);
                Require(model.Advance(.5) && model.RopeTravel < 0, "Winch playback did not start by pulling the load with the pawl disengaged.");
                CheckMotion(model.Time);
                tool.Cancel();
                foreach (var id in assemblyIds) Match(id, originals[id]);

                model = Begin();
                tool.ToggleAnimation();
                var committedTime = -.75;
                tool.Seek(committedTime);
                CheckMotion(committedTime);
                var confirmed = assemblyIds.ToDictionary(id => id, id => new Pose(Body(id).transform.position, Body(id).transform.rotation));
                await Change(() => Click("Simulation Controls", "Set Pose"));
                Require(!SimulationTool.HasPreview, "Set Pose retained a private preview.");
                foreach (var id in assemblyIds) Match(id, confirmed[id]);
                await History(false); foreach (var id in assemblyIds) Match(id, originals[id]);
                await History(true); foreach (var id in assemblyIds) Match(id, confirmed[id]);

                if (shared) {
                    SelectBodies(new[] { drumId, pulleyId });
                    var properties = Object.FindFirstObjectByType<AddPropertiesUI>(FindObjectsInactive.Include);
                    Invoke(properties, "PlacePickedPart");
                    Require(placement.CurrentSelectionPlacement?.PartCount == 2, "P did not stage the partial winch selection.");
                    Set(placement, "currentDisplacement", new Displacement(center + Vector3.left * 25, Quaternion.identity));
                    await Change(() => Invoke(placement, "Place"));
                    Require(CollaborationSession.SaveSnapshot().ropes.Length == 1, "An incomplete P copy acquired a rope.");
                    placement.CancelPlacement();
                    await History(false);

                    SelectBodies(assemblyIds);
                    Invoke(properties, "PlacePickedPart");
                    Require(placement.CurrentSelectionPlacement?.PartCount == assemblyIds.Length, "P did not stage the complete winch.");
                    Set(placement, "currentDisplacement", new Displacement(center + Vector3.left * 25, Quaternion.identity));
                    await Change(() => Invoke(placement, "Place"));
                    CheckSharedCopy();
                    placement.CancelPlacement();
                    await History(false);

                    SelectBodies(assemblyIds);
                    var keyboard = Object.FindFirstObjectByType<KeyboardTransformTool>();
                    keyboard.enabled = false;
                    try {
                        foreach (var device in InputSystem.devices.OfType<Keyboard>())
                            InputSystem.QueueStateEvent(device, new KeyboardState(UnityEngine.InputSystem.Key.LeftAlt));
                        UpdateInput();
                        Invoke(keyboard, "BeginSelection", false);
                        await Until(() => Get<bool>(keyboard, "active"));
                        Require(Get<List<GameObject>>(keyboard, "duplicates").Count == assemblyIds.Length, "Alt did not stage the complete winch.");
                        Invoke(keyboard, "SetTarget", Vector3.up * 12, Quaternion.identity);
                        await Change(() => Invoke(keyboard, "CompleteConfirmation"));
                    }
                    finally {
                        foreach (var device in InputSystem.devices.OfType<Keyboard>())
                            InputSystem.QueueStateEvent(device, new KeyboardState());
                        UpdateInput();
                        keyboard.enabled = true;
                    }
                    CheckSharedCopy();
                    await History(false);

                    var transfer = Object.FindFirstObjectByType<BuildTransferTool>();
                    var oldClipboard = GUIUtility.systemCopyBuffer;
                    try {
                        SelectBodies(assemblyIds);
                        Require(await transfer.CopySelectedAsync() && await transfer.PasteAsync(), "Main could not paste the complete shared winch.");
                        Set(placement, "currentDisplacement", new Displacement(center + Vector3.right * 25, Quaternion.identity));
                        await Change(() => Invoke(placement, "Place"));
                    }
                    finally { GUIUtility.systemCopyBuffer = oldClipboard; }
                    var pasted = CheckSharedCopy();
                    await History(false); Require(CollaborationSession.SaveSnapshot().ropes.Length == 1, "Undo left the pasted rope.");
                    await History(true); Require(CollaborationSession.SaveSnapshot().ropes.Any(route => route.id == pasted.id), "Redo lost the copied route.");
                    await History(false);
                    await Change(() => CollaborationSession.Delete(new[] { Body(loadId) }));
                    Require(Rope() == null && CollaborationSession.SaveSnapshot().ropes.Length == 0, "Endpoint deletion left a shared rope.");
                    await History(false); Require(Rope()?.Data.id == initial.id, "Undoing endpoint deletion lost the rope.");
                    await History(true); Require(Rope() == null, "Redoing endpoint deletion left a rope.");
                    await History(false);

                    SelectRoute(); tool.EditRopeRoute();
                    await Change(() => Click("Rope Routing", "Remove"));
                    Require(Rope() == null, "Shared Remove left the rope.");
                    await History(false); Require(Rope()?.Data.id == initial.id, "Undoing shared Remove lost the rope.");

                    var owned = Get<CollaborationHost>(session, "host");
                    string hash = CollaborationSession.CommittedHash;
                    await peer.LeaveAsync();
                    peer.Dispose(); peer = null;
                    await owned.RestartAsync();
                    var connection = Get<CollaborationClient>(session, "client");
                    connection.Suspend("Winch recovery fixture");
                    await (Task)Invoke(session, "ReconnectAsync");
                    Require(connection.IsConnected && owned.Recovered && CollaborationSession.CommittedHash == hash && Rope()?.Data.id == initial.id,
                        "Shared room reopening lost the winch, route, or confirmed pose.");
                    await History(true); Require(Rope() == null, "Recovered redo did not remove the rope.");
                    await History(false); Require(Rope()?.Data.id == initial.id, "Recovered undo did not restore the same rope.");

                    RopeRouteData CheckSharedCopy() {
                        var copy = CollaborationSession.SaveSnapshot().ropes.Single(route => route.id != initial.id);
                        Require(!copy.MemberIds.Intersect(routeIds).Any() && copy.availableLength == 26, "Shared copy did not remap rope members.");
                        Require(copy.MemberIds.All(id => Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None).Any(part => part.ConnectionId == id)) &&
                            RopeRoute.Find(copy.start.part)?.Data.id == copy.id, "Shared copy did not materialize the route and its members.");
                        return copy;
                    }
                }

                // Save from a second private preview. The on-disk build must hold the confirmed poses.
                model = Begin();
                tool.Seek(-.25);
                builds.buildPath = path;
                Require(await (Task<bool>)Invoke(builds, "SaveAsync", false) && File.Exists(path), "Main could not save the confirmed winch.");
                tool.Cancel();
                var beforeOpen = Rope();
                Require(await builds.OpenPathAsync(path), "Main could not reopen the saved winch.");
                Require(!CollaborationSession.IsShared && builds.CurrentBuildIsSaved && Rope() != beforeOpen &&
                    Rope()?.Data.id == initial.id && Rope().Data.availableLength == 26 && Rope().GetComponent<MeshRenderer>().enabled,
                    "Reopening lost the rope identity or length.");
                foreach (var id in assemblyIds) Match(id, confirmed[id]);
                Require(Begin().HasWinchPawl, "The reopened build could not simulate its winch.");
                tool.Cancel();
                var seed = CollaborationScene.CaptureDocument(CollaborationScene.Catalog);
                var seededRoute = DocumentModel.Chains(seed).Single(record => record.id == initial.id);
                Require(seededRoute.rope?.length == 26 && seededRoute.members.SequenceEqual(routeIds), "Hosting an existing build would lose its rope.");

                void CheckMotion(double seconds) {
                    var payout = .4210216f * (float)(seconds * Math.PI / 2);
                    Near((float)model.RopeTravel, payout, "Signed drum travel");
                    float tail = originalTail + payout;
                    float slide = Mathf.Sqrt(tail * tail - loadOffset * loadOffset) -
                        Mathf.Sqrt(originalTail * originalTail - loadOffset * loadOffset);
                    Near(Body(loadId).transform.position, originalLoad + travelAxis * slide, "Load displacement");
                    var pulleyRotation = Quaternion.AngleAxis(payout / .3625f * Mathf.Rad2Deg, originalPulley * Vector3.forward) * originalPulley;
                    Require(Quaternion.Angle(Body(pulleyId).transform.rotation, pulleyRotation) < .06f, "Pulley did not follow signed rope travel.");
                    Near(Body(pulleyId).GetComponent<RopeGroove>().Center, pulleyCenter, "Redirect groove pivot");
                    foreach (var id in new[] { axleId, bracketId, pawlId, pinId }) Match(id, originals[id]);
                }
            }
            finally {
                peer?.Dispose();
                if (CollaborationSession.IsShared) CollaborationSession.LeaveForFileOpen();
                if (session != null) await Get<Task>(session, "hostShutdown");
                File.Delete(path);
            }

            GameObject Place(string id, Vector3 position, Quaternion? rotation = null) {
                var before = PartsManager.FindLoadedObjects().ToHashSet();
                rows[id].ToggleCurrent(true);
                Require(placement.CurrentPartPlacement?.objectId == id, "Main catalog did not select " + id);
                Set(placement, "currentDisplacement", new Displacement(position, rotation ?? Quaternion.identity));
                Invoke(placement, "Place");
                var body = PartsManager.FindLoadedObjects().Single(part => !before.Contains(part));
                placement.CancelPlacement();
                Require(body.GetComponent<SavedObject>().id == id, "Main placed the wrong part for " + id);
                return body;
            }
            Vector3 BracketHole(string name) => bracket.GetComponentsInChildren<HoleCollider>().Single(hole => hole.name == name).transform.position;
            GameObject Body(string id) => PartsManager.FindLoadedObjects().Single(body => Id(body) == id);
            RopeRoute Rope() => RopeRoute.Find(drumId);
            void SelectBodies(IEnumerable<string> ids) {
                selection.SetCurrent(new MultiSelection(null, ids.Select(Body).ToList()) { individual = true }, true, true);
                EventSystem.current.SetSelectedGameObject(null);
            }
            void SelectRoute() {
                SimulationTool.ExitForEdit();
                SelectBodies(new[] { drumId, pulleyId });
            }
            MechanismSimulation Begin() {
                SimulationTool.ExitForEdit();
                selection.SetCurrent(new ObjectSelection { gameObject = Body(shaftId), individual = true }, true, true);
                EventSystem.current.SetSelectedGameObject(null);
                tool.Activate();
                Require(tool.Current?.HasWinch == true, "Tool 5 did not discover the winch: " + tool.Current?.WinchStatus);
                return tool.Current;
            }
            void Match(string id, Pose expected) {
                var body = Body(id);
                Near(body.transform.position, expected.position, body.GetComponent<SavedObject>().id + " position");
                Require(Quaternion.Angle(body.transform.rotation, expected.rotation) < .06f, "Unexpected rotation of " + body.GetComponent<SavedObject>().id);
            }
            async Task Change(Action edit) {
                long revision = CollaborationSession.CommittedRevision + 1;
                edit();
                if (!shared) return;
                await Until(() => CollaborationSession.CommittedRevision == revision && !CollaborationSession.HasPending && CollaborationSession.CanPreview);
                if (peer != null) {
                    await peer.WaitForRevisionAsync(revision);
                    Require(CollaborationSession.CommittedHash == DocumentModel.Hash(peer.Snapshot), "The peer's accepted winch state diverged.");
                }
            }
            async Task History(bool redo) {
                await Change(() => { if (redo) StateSystem.instance.Redo(); else StateSystem.instance.Undo(); });
                if (!shared) await Task.Delay(320);
            }
        }

        private static float TailLength(RopeRoute route) {
            var vertices = route.GetComponent<MeshFilter>().sharedMesh.vertices;
            // The two caps follow the 8-vertex rings. The penultimate ring is
            // the redirect exit, and the last cap is the load attachment.
            var exit = Vector3.zero;
            for (int i = vertices.Length - 18; i < vertices.Length - 10; i++) exit += vertices[i] / 8;
            return Vector3.Distance(exit, vertices[^1]);
        }

        private static async Task PickDrum(GameObject drum, Vector3 center) {
            var collision = drum.GetComponent<MeshCollider>();
            var attempts = new List<string>();
            foreach (var direction in new[] { Vector3.up, Vector3.right, Vector3.left, new Vector3(0, 1, 1).normalized })
                foreach (float offset in new[] { .5f, -.5f, .25f, -.25f, 0 }) {
                    var target = center + Vector3.forward * offset + Vector3.Cross(direction, Vector3.forward) * .12f;
                    var hit = Aim(target, direction);
                    if (hit?.GetComponentInParent<SavedObject>()?.gameObject != drum) {
                        var direct = collision.Raycast(new Ray(target + direction * 8, -direction), out _, 16);
                        attempts.Add($"{direction}/{offset} {hit?.name ?? "none"} (direct {direct})");
                        continue;
                    }
                    await CameraNavigationValidation.RouteClick(MouseInput.Position);
                    return;
                }
            throw new InvalidOperationException($"No visible drum surface at {center}. Collider {collision.bounds} on layer {drum.layer}, near {PivotCamera.Main.camera.nearClipPlane}. " + string.Join(", ", attempts));
        }

        private static async Task Pick(GameObject body, Vector3 point, Vector3 normal) {
            var shape = Aim(point, normal);
            Require(shape?.GetComponentInParent<SavedObject>()?.gameObject == body,
                $"Fixture ray for {body.name} hit {shape?.name ?? "nothing"} at world point {point}.");
            await CameraNavigationValidation.RouteClick(MouseInput.Position);
        }

        private static Collider Aim(Vector3 point, Vector3 normal) {
            var camera = PivotCamera.Main.camera;
            var up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > .9f ? Vector3.forward : Vector3.up;
            camera.transform.SetPositionAndRotation(point + normal * 8, Quaternion.LookRotation(-normal, up));
            var screen = (Vector2)camera.WorldToScreenPoint(point);
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = screen });
            UpdateInput();
            Physics.SyncTransforms();
            Physics.Raycast(camera.ScreenPointToRay(MouseInput.Position), out var hit, Mathf.Infinity,
                LayerMask.GetMask("Default", "HoleCollisions"), QueryTriggerInteraction.Ignore);
            return hit.collider;
        }

        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });

        private static void Click(string canvas, string name) {
            var button = GameObject.Find(canvas).GetComponentsInChildren<Button>().Single(item => item.name == name);
            ExecuteEvents.Execute(button.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
        }

        private static string Label(string canvas, string name) =>
            GameObject.Find(canvas).GetComponentsInChildren<Text>().Single(text => text.name == name).text;
        private static string Id(GameObject body) => body.GetComponent<SavedObject>().ConnectionId;
        private static T Get<T>(object body, string name) => (T)body.GetType().GetField(name, Private).GetValue(body);
        private static void Set(object body, string name, object value) => body.GetType().GetField(name, Private).SetValue(body, value);
        private static object Invoke(object body, string name, params object[] args) => body.GetType().GetMethod(name, Private).Invoke(body, args);
        private static void Near(float actual, float expected, string label) =>
            Require(Mathf.Abs(actual - expected) < .002f, $"{label}: {actual:F5}, expected {expected:F5}.");
        private static void Near(Vector3 actual, Vector3 expected, string label) =>
            Require(Vector3.Distance(actual, expected) < .002f, $"{label}: {actual.ToString("F4")}, expected {expected.ToString("F4")}.");
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        private static async Task Until(Func<bool> predicate) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate()) {
                if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("Shared winch edit did not settle.");
                await Task.Delay(20);
            }
        }
    }
}
