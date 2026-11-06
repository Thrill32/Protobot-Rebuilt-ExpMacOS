using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Tools;
using Protobot.UI;
using UnityEngine;

namespace Protobot {
    [DefaultExecutionOrder(-200)]
    public sealed class CollaborationSession : MonoBehaviour {
        private static CollaborationSession instance;
        private CollaborationClient client;
        private CollaborationHost host;
        private CatalogManifest catalog;
        private readonly object queueGate = new object();
        private readonly Queue<(Action action, int bytes)> main = new Queue<(Action, int)>();
        private int queuedBytes;
        private EventBatch latestPresence;
        private CollaborationClient presenceClient;
        private readonly Dictionary<string, GameObject> ghosts = new Dictionary<string, GameObject>();
        private Document committed;
        private bool busy, applying, reserving, submitting, sendingPreview;
        private string lease;
        private string[] reserved = Array.Empty<string>();
        private long previewSequence;
        private long lastSubmittedRevision;
        private float nextRenew, nextPreview;
        private string status = "Development alpha · Host or join a catalog build.";
        private string endpoint = "http://127.0.0.1:8787", room = "team", displayName = "Editor", password = "", port = "8787";
        private bool lan, panel;
        private CollaborationTransport transport;
        private Participant[] participants = Array.Empty<Participant>();
        private Rect panelRect;
        private string[] mutations = { "Grid placement", "G / R", "Delete" };
        public static bool IsShared => instance != null && instance.client != null;
        public static bool IsApplying => instance != null && instance.applying;
        public static bool CanEdit => !IsShared || instance.client.IsConnected && instance.client.Participant.role != "viewer" && !instance.busy && !instance.submitting && !instance.reserving;
        public static bool HasPending => IsShared && (instance.submitting || instance.client.PendingCount > 0);
        public static string CommittedHash => IsShared && instance.committed != null ? DocumentModel.Hash(instance.committed) : null;
        public static bool ContainsPointer => instance != null && MouseInput.CurrentMouse != null && instance.panelRect.Contains(new Vector2(MouseInput.Position.x, Screen.height - MouseInput.Position.y));
        public static bool BlocksLegacyMutation => IsShared && !IsApplying;
        public static bool BlocksUIInput => instance != null && (instance.panel || instance.busy);
        public static bool CancelPanel() {
            if (instance == null || !instance.panel) return false;
            instance.panel = false; return true;
        }
        public static BuildData SaveSnapshot() => IsShared ? CollaborationScene.ToBuild(instance.committed) : SceneBuild.ToBuildData();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install() {
            bool enabled = CollaborationArguments.Enabled;
#if UNITY_EDITOR
            enabled |= UnityEditor.EditorPrefs.GetBool("Protobot.Collaboration.Development", false);
#endif
            if (enabled) EnableDevelopment();
        }
        public static void EnableDevelopment() {
            if (instance != null) return;
            new GameObject("Collaboration (development alpha)").AddComponent<CollaborationSession>();
        }
        private void Awake() {
            instance = this;
            displayName = CollaborationArguments.Get("--collab-name", Environment.UserName);
            endpoint = CollaborationArguments.Get("--collab-join", endpoint);
            room = CollaborationArguments.Get("--collab-room", room);
            var requestedTransport = CollaborationArguments.Get("--collab-transport", "auto");
            transport = requestedTransport == "long-polling" ? CollaborationTransport.LongPolling : requestedTransport == "websocket" ? CollaborationTransport.WebSocket : CollaborationTransport.Auto;
            Application.quitting += OnQuitting;
        }
        private void Start() {
            if (CollaborationArguments.Get("--collab-join") != null) RequestJoin();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CollaborationArguments.Get("--collab-probe") != null) _ = CollaborationPlayerProbe.RunAsync(this);
#endif
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal Task StartProbeHostAsync() { port = "0"; return HostAsync(); }
        internal string ProbeEndpoint => endpoint;
        internal Document ProbeDocument => committed == null ? null : WireJson.Clone(committed);
        internal string ProbeTransport => client?.ActiveTransport;
        internal bool ProbeConnected => client?.IsConnected == true;
#endif

        public static bool AllowsInput(InputEvent input) {
            if (instance == null) return true;
            if (instance.panel || instance.busy) return false;
            if (!IsShared) return true;
            switch (input.name) {
                case "Orbit": case "Pan": case "Noclip Camera": case "Toggle Orthographic": case "Focus":
                case "Pointer Select": case "Box Select": case "Select All": case "Multi-Select Button": case "Grouping Bypass":
                case "Toggle Grid": case "Toggle Holes Only": case "Select Opposite Hole": case "Enable Sidebar":
                case "Save": case "Stop Placing": case "Snap Bypass": return true;
                case "Grab Selection": case "Rotate Placement": case "Delete Selection": case "Place": return CanEdit;
                case "Increase First Property": case "Decrease First Property": case "Increase Second Property": case "Decrease Second Property":
                    return CanEdit && UnityEngine.Object.FindFirstObjectByType<Placement>()?.placing == true;
                default: return false;
            }
        }

        private void CancelTools() {
            KeyboardTransformTool.CancelActive();
            FindFirstObjectByType<Placement>()?.CancelPlacement();
            foreach (var movement in FindObjectsByType<MovementManager>(FindObjectsSortMode.None)) movement.CancelMovement();
            CustomModelTool.CancelActive(); Simulation.SimulationTool.ExitForEdit();
            FindFirstObjectByType<ConnectTool>()?.SetActive(false);
            foreach (var tool in FindObjectsByType<ToolToggle>(FindObjectsSortMode.None)) tool.DeselectTool();
        }

        private async Task HostAsync() {
            if (busy || IsShared) return;
            busy = true;
            try {
                CancelTools();
                catalog = CollaborationScene.Catalog;
                committed = CollaborationScene.CaptureDocument(catalog);
                string hostedBaselineHash = DocumentModel.Hash(committed);
                var builds = FindFirstObjectByType<BuildsManager>();
                bool wasSaved = builds.CurrentBuildIsSaved;
                if (!int.TryParse(port, out var listenPort) || listenPort < 0 || listenPort > 65535) throw new ArgumentException("Use a port from 0 to 65535.");
                var owner = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                host = new CollaborationHost();
                await host.StartAsync(new Bootstrap { room = room, password = password, ownerCredential = owner, catalog = catalog, document = committed }, listenPort, lan);
                endpoint = host.Url;
                await ConnectAsync(owner, true);
                builds.BeginHostedDocument(hostedBaselineHash, wasSaved);
                if (lan) {
                    var address = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                    if (address != null) endpoint = new UriBuilder(host.Url) { Host = address.ToString() }.Uri.GetLeftPart(UriPartial.Authority);
                }
                panel = false;
                status = lan ? "Hosting on LAN · reachability from another computer is untested." : "Hosting on loopback · other local players can join.";
            }
            catch (Exception error) { Report(error); if (host != null) await host.StopAsync(); host = null; client?.Dispose(); client = null; }
            finally { password = ""; busy = false; }
        }

        private void RequestJoin() {
            if (busy || IsShared) return;
            CancelTools(); panel = false;
            var builds = FindFirstObjectByType<BuildsManager>();
            builds.RunAfterUnsavedChanges(() => _ = JoinAsync());
        }
        private async Task JoinAsync() {
            busy = true;
            try { catalog = CollaborationScene.Catalog; await ConnectAsync(null, false); status = "Connected to " + room; }
            catch (Exception error) { Report(error); client?.Dispose(); client = null; }
            finally { password = ""; busy = false; }
        }
        private async Task ConnectAsync(string owner, bool hosting) {
            var candidate = new CollaborationClient(endpoint, room, catalog, transport, lan);
            try {
                await candidate.JoinAsync(displayName, password, owner);
                client = candidate;
                committed = candidate.Snapshot;
                applying = true;
                try { if (!hosting) CollaborationScene.Load(committed); }
                finally { applying = false; }
                if (!hosting) FindFirstObjectByType<BuildsManager>().BeginJoinedDocument();
                Subscribe(candidate);
                // The pump may have advanced during materialization; resnapshot before enabling input.
                committed = candidate.Snapshot;
                applying = true; try { CollaborationScene.Load(committed); } finally { applying = false; }
            }
            catch { _ = LeaveAndDispose(candidate); throw; }
        }

        private void Subscribe(CollaborationClient connected) {
            connected.Accepted += edit => {
                if (!Enqueue(() => {
                    if (client != connected || edit.revision <= committed.revision) return;
                    applying = true;
                    try {
                        DocumentModel.Require(edit.revision == committed.revision + 1, "scene-revision-gap");
                        // Apply this revision even if the network thread has already received later edits.
                        var next = DocumentModel.Reduce(committed, edit.transaction, catalog);
                        var ids = edit.transaction.writes.Select(p => p.id).Concat(edit.transaction.deletes);
                        if (reserved.Intersect(ids).Any()) { KeyboardTransformTool.CancelActive(); CancelPose(); }
                        CollaborationScene.Apply(edit, next); committed = next;
                    }
                    catch (Exception error) { Report(error); connected.Suspend("Scene apply failed; reconnect to synchronize."); CollaborationScene.Load(committed); }
                    finally { applying = false; }
                }, WireJson.Encode(edit).Length)) connected.Suspend("Scene updates fell behind; reconnect to synchronize.");
            };
            connected.Presence += update => { lock (queueGate) { latestPresence = update; presenceClient = connected; } };
            connected.Disconnected += reason => Enqueue(() => {
                if (client != connected) return;
                CancelTools(); CancelPose(); ClearGhosts(); status = reason;
            });
            connected.PendingResolved += message => Enqueue(() => status = message);
        }

        private bool Enqueue(Action action, int bytes = 0) {
            lock (queueGate) {
                if (bytes > 0 && (main.Count >= 256 || queuedBytes + bytes > 8 * 1024 * 1024)) return false;
                main.Enqueue((action, bytes)); queuedBytes += bytes; return true;
            }
        }
        private bool TryDequeue(out Action action) {
            lock (queueGate) {
                if (main.Count == 0) { action = null; return false; }
                var next = main.Dequeue(); queuedBytes -= next.bytes; action = next.action; return true;
            }
        }

        public static void ReservePose(IReadOnlyList<GameObject> bodies, Action granted) { if (instance != null) _ = instance.ReservePoseAsync(bodies, granted); }
        private async Task ReservePoseAsync(IReadOnlyList<GameObject> bodies, Action granted) {
            if (!CanEdit || lease != null || reserving) return;
            reserving = true; status = "Waiting for edit reservation…";
            var ids = bodies.Select(b => b.GetComponent<SavedObject>().ConnectionId).Distinct().ToArray();
            var connection = client;
            long epoch = connection.ConnectionEpoch;
            try {
                var result = await connection.ReserveAsync(ids);
                if (client != connection || connection.ConnectionEpoch != epoch) { try { await connection.ReleaseAsync(result.token); } catch { } return; }
                lease = result.token; reserved = ids; nextRenew = Time.realtimeSinceStartup + 3; previewSequence = 0;
                status = "Editing reserved parts"; granted();
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch) Report(error); }
            finally { if (client == connection && connection.ConnectionEpoch == epoch) reserving = false; }
        }
        public static bool HasPoseReservation(IEnumerable<GameObject> bodies) => IsShared && instance.lease != null &&
            bodies.Select(b => b.GetComponent<SavedObject>().ConnectionId).OrderBy(x => x).SequenceEqual(instance.reserved.OrderBy(x => x));
        public static void CancelPose() {
            if (!IsShared) return;
            var token = instance.lease; instance.lease = null; instance.reserved = Array.Empty<string>();
            if (token != null) _ = instance.Release(token);
        }
        private async Task Release(string token) { var connection = client; try { if (connection != null) await connection.ReleaseAsync(token); } catch { } }
        public static void ConfirmPose(IEnumerable<GameObject> bodies) {
            if (!IsShared) return;
            var session = instance;
            try {
                DocumentModel.Require(session.client.IsConnected, "disconnected");
                var writes = bodies.Select(b => CollaborationScene.Capture(b)).ToArray();
                var tx = session.client.CreateTransaction("SetPoses", writes, lease: session.lease);
                _ = session.Submit(tx);
            }
            catch (CollaborationException error) {
                CollaborationScene.ApplyParts(session.committed.parts.Where(p => session.reserved.Contains(p.id)));
                CancelPose(); session.Report(error);
            }
        }
        public static void PlaceCatalog(GameObject temporary) {
            var part = CollaborationScene.Capture(temporary);
            temporary.SetActive(false); Destroy(temporary);
            if (IsShared && CanEdit) {
                try { _ = instance.Submit(instance.client.CreateTransaction("AddParts", new[] { part })); }
                catch (CollaborationException error) { instance.Report(error); }
            }
        }
        public static void Delete(IEnumerable<GameObject> bodies) { if (IsShared && CanEdit) _ = instance.DeleteAsync(bodies.ToArray()); }
        private async Task DeleteAsync(GameObject[] bodies) {
            if (lease != null) return;
            submitting = true;
            var connection = client;
            long epoch = connection.ConnectionEpoch;
            try {
                var ids = bodies.Select(b => b.GetComponent<SavedObject>().ConnectionId).Distinct().ToArray();
                var reservation = await connection.ReserveAsync(ids);
                if (client != connection || connection.ConnectionEpoch != epoch) { try { await connection.ReleaseAsync(reservation.token); } catch { } return; }
                var tx = connection.CreateTransaction("DeleteParts", Array.Empty<Part>(), ids, lease: reservation.token);
                var receipt = await connection.SubmitAsync(tx);
                if (client != connection || connection.ConnectionEpoch != epoch) return;
                if (receipt.accepted) lastSubmittedRevision = Math.Max(lastSubmittedRevision, receipt.revision);
                if (!receipt.accepted) { status = receipt.error; await Release(reservation.token); }
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch) Report(error); }
            finally { if (client == connection && connection.ConnectionEpoch == epoch) submitting = false; }
        }
        private async Task Submit(Transaction transaction) {
            submitting = true;
            var connection = client;
            long epoch = connection.ConnectionEpoch;
            try {
                var receipt = await connection.SubmitAsync(transaction);
                if (client != connection || connection.ConnectionEpoch != epoch) return;
                if (receipt.accepted) lastSubmittedRevision = Math.Max(lastSubmittedRevision, receipt.revision);
                if (!receipt.accepted) { status = "Edit rejected: " + receipt.error; CollaborationScene.ApplyParts(committed.parts.Where(p => reserved.Contains(p.id))); }
                else status = "Accepted revision " + receipt.revision;
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch) { Report(error); CollaborationScene.ApplyParts(committed.parts.Where(p => reserved.Contains(p.id))); } }
            finally { if (client == connection && connection.ConnectionEpoch == epoch) { CancelPose(); submitting = false; } }
        }

        public static async Task<bool> WaitForSaveAsync() {
            if (!IsShared) return true;
            var connection = instance.client;
            while ((instance.submitting || instance.committed.revision < instance.lastSubmittedRevision) && connection.IsConnected) {
                await Task.Yield();
                if (instance.client != connection) return false;
            }
            if (connection.PendingCount > 0 || instance.committed.revision < instance.lastSubmittedRevision) { instance.status = "Reconnect to resolve pending edits before saving."; return false; }
            return true;
        }
        public static void LeaveForFileOpen() { if (IsShared && !IsApplying) instance.Detach(); }
        private void Detach() {
            CancelTools(); CancelPose();
            if (committed != null) CollaborationScene.ApplyParts(committed.parts);
            var previous = client; client = null;
            if (previous != null) _ = LeaveAndDispose(previous);
            var owned = host; host = null; if (owned != null) _ = owned.StopAsync();
            committed = null; participants = Array.Empty<Participant>(); ClearGhosts(); status = "Continuing offline.";
            submitting = reserving = sendingPreview = false;
            lastSubmittedRevision = 0;
            StateSystems.StateSystem.instance?.ResetStates();
        }
        private static async Task LeaveAndDispose(CollaborationClient previous) { try { await previous.LeaveAsync(); } catch { } finally { previous.Dispose(); } }
        private async Task ReconnectAsync() {
            if (client == null || busy) return;
            busy = true;
            try {
                await client.PrepareReconnectAsync(); await client.JoinAsync(displayName);
                submitting = reserving = sendingPreview = false;
                lock (queueGate) { main.Clear(); queuedBytes = 0; latestPresence = null; presenceClient = null; }
                committed = client.Snapshot;
                applying = true; try { CollaborationScene.Load(committed); } finally { applying = false; }
                status = "Reconnected and synchronized. " + string.Join(" ", client.RecoveryMessages);
            }
            catch (Exception error) { Report(error); }
            finally { busy = false; }
        }

        private void Update() {
            int count = 0;
            while (count++ < 128 && TryDequeue(out var action)) action();
            EventBatch presence; CollaborationClient source;
            lock (queueGate) { presence = latestPresence; source = presenceClient; latestPresence = null; }
            if (source == client && client?.IsConnected == true && presence != null) { participants = presence.participants; DrawGhosts(presence.previews); }
            if (lease == null || !client.IsConnected || submitting) return;
            if (Time.realtimeSinceStartup >= nextRenew) { nextRenew = Time.realtimeSinceStartup + 3; _ = Renew(); }
            if (!sendingPreview && Time.realtimeSinceStartup >= nextPreview) { nextPreview = Time.realtimeSinceStartup + .1f; _ = SendPreview(); }
        }
        private async Task Renew() {
            var connection = client; var renewing = lease;
            long epoch = connection.ConnectionEpoch;
            try { await connection.ReserveAsync(reserved, renewing); }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch && lease == renewing) { KeyboardTransformTool.CancelActive(); CancelPose(); Report(error); } }
        }
        private async Task SendPreview() {
            sendingPreview = true;
            var connection = client; var sending = lease;
            long epoch = connection.ConnectionEpoch;
            try {
                var bodies = PartsManager.FindLoadedObjects().Where(b => reserved.Contains(b.GetComponent<SavedObject>().ConnectionId)).ToArray();
                await connection.PreviewAsync(new Preview { lease = sending, sequence = ++previewSequence, parts = bodies.Select(b => CollaborationScene.Capture(b)).ToArray() });
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch && lease == sending) { KeyboardTransformTool.CancelActive(); CancelPose(); Report(error); } }
            finally { if (client == connection && connection.ConnectionEpoch == epoch) sendingPreview = false; }
        }

        private void DrawGhosts(Preview[] previews) {
            // Read-only mesh ghosts: no SavedObject, Collider, detector, selection or file ownership.
            var visible = new HashSet<string>();
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            foreach (var preview in previews.Where(p => p.author != client.Participant.id)) foreach (var part in preview.parts) {
                visible.Add(part.id);
                if (!ghosts.TryGetValue(part.id, out var ghost) && bodies.TryGetValue(part.id, out var source)) {
                    ghost = new GameObject("Collaboration ghost");
                    var mesh = ghost.AddComponent<MeshFilter>();
                    mesh.sharedMesh = MeshCombiner.CombineMeshes(new List<GameObject> { source }, source.transform.position, source.transform.rotation);
                    var renderer = ghost.AddComponent<MeshRenderer>();
                    var original = source.GetComponentInChildren<MeshRenderer>();
                    renderer.material = new Material(original.sharedMaterial);
                    renderer.material.color = new Color(.2f, .8f, 1f, .35f);
                    ghosts.Add(part.id, ghost);
                }
                if (ghost != null) ghost.transform.SetPositionAndRotation(new Vector3((float)part.position[0], (float)part.position[1], (float)part.position[2]),
                    new Quaternion((float)part.rotation[0], (float)part.rotation[1], (float)part.rotation[2], (float)part.rotation[3]));
            }
            foreach (var key in ghosts.Keys.Where(id => !visible.Contains(id)).ToArray()) RemoveGhost(key);
        }
        private void RemoveGhost(string key) {
            var ghost = ghosts[key]; Destroy(ghost.GetComponent<MeshFilter>().sharedMesh); Destroy(ghost.GetComponent<MeshRenderer>().sharedMaterial); Destroy(ghost); ghosts.Remove(key);
        }
        private void ClearGhosts() { foreach (var key in ghosts.Keys.ToArray()) RemoveGhost(key); }
        private void Report(Exception error) { status = error.Message; InteractionFeedback.Notify(status); Debug.LogWarning("Collaboration: " + status); }
        private void OnQuitting() { host?.Dispose(); client?.Dispose(); }
        private void OnDestroy() { OnQuitting(); Application.quitting -= OnQuitting; ClearGhosts(); if (instance == this) instance = null; }

        private void OnGUI() {
            float width = Mathf.Min(350, Screen.width - 16);
            panelRect = new Rect(Mathf.Max(8, Screen.width - width - 12), 80, width, panel ? Mathf.Min(540, Screen.height - 90) : IsShared ? 240 : 44);
            GUILayout.BeginArea(panelRect, GUI.skin.box);
            if (GUILayout.Button(panel ? "Close collaboration" : "Collaboration · development alpha")) panel = !panel;
            if (panel) {
                GUILayout.Label("Catalog builds · ephemeral room · save locally");
                if (!IsShared) {
                    GUILayout.Label("Your name"); displayName = GUILayout.TextField(displayName, 48);
                    GUILayout.Label("Room ID"); room = GUILayout.TextField(room, 80);
                    GUILayout.Label("Password (optional)"); password = GUILayout.PasswordField(password, '*', 128);
                    lan = GUILayout.Toggle(lan, "Allow unencrypted LAN HTTP");
                    transport = (CollaborationTransport)GUILayout.SelectionGrid((int)transport, new[] { "Auto", "WebSocket", "HTTPS requests" }, 1);
                    GUILayout.Label("Host port (0 chooses a free port)"); port = GUILayout.TextField(port, 5);
                    GUI.enabled = !busy;
                    if (GUILayout.Button("Host current build")) _ = HostAsync();
                    GUILayout.Label("Join server origin"); endpoint = GUILayout.TextField(endpoint, 256);
                    if (GUILayout.Button("Join room…")) RequestJoin();
                    GUI.enabled = true;
                }
            }
            if (IsShared) {
                GUILayout.Label(room + " · revision " + committed.revision + " · " + (client.ActiveTransport ?? "connecting"));
                GUILayout.Label(string.Join(", ", participants.Select(p => p.name)));
                GUILayout.Label("Available: " + string.Join(", ", mutations));
                GUILayout.Label("Server origin: " + endpoint);
                if (GUILayout.Button("Copy endpoint and room")) GUIUtility.systemCopyBuffer = endpoint + "  room: " + room;
                if (!client.IsConnected && GUILayout.Button("Reconnect")) _ = ReconnectAsync();
                if (GUILayout.Button(host != null ? "Stop hosting and continue offline" : "Leave and continue offline")) Detach();
            }
            if (panel || IsShared) GUILayout.Label(status, new GUIStyle(GUI.skin.label) { wordWrap = true });
            GUILayout.EndArea();
        }
    }
}
