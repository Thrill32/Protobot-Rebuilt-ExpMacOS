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
    public sealed partial class CollaborationSession : MonoBehaviour {
        private static CollaborationSession instance;
        private CollaborationClient client;
        private CollaborationHost host;
        private CollaborationRecovery.HostedRoom hostedRoom;
        private CollaborationRecovery.HostedRoom[] recoveredRooms = Array.Empty<CollaborationRecovery.HostedRoom>();
        private bool portEdited, unreadableRecovery;
        private CatalogManifest catalog;
        private readonly object queueGate = new object();
        private readonly Queue<(Action action, int bytes)> main = new Queue<(Action, int)>();
        private int queuedBytes;
        private EventBatch latestPresence;
        private CollaborationClient presenceClient;
        private readonly Dictionary<string, GameObject> ghosts = new Dictionary<string, GameObject>();
        private Document committed;
        private bool busy, applying, reserving, submitting, sendingPreview;
        private bool broadcastReservationPreview, renewingLease;
        private string lease;
        private string[] reserved = Array.Empty<string>();
        private HashSet<string> previewIds;
        private VersionRef[] reservedVersions = Array.Empty<VersionRef>();
        private int reservationGeneration;
        private Task releasePending = Task.CompletedTask;
        private Task reservationPending = Task.CompletedTask;
        private Task hostShutdown = Task.CompletedTask;
        private readonly Dictionary<string, (SelectionManager manager, string[] ids)> selectAfterApply = new();
        private long previewSequence;
        private long lastSubmittedRevision;
        private float nextRenew, nextPreview;
        private string status = "Choose a server to find and join a room.";
        private string endpoint = "", room = "team", displayName = "Editor", password = "", port = "0";
        private bool lan, panel;
        private CollaborationTransport transport;
        private Participant[] participants = Array.Empty<Participant>();
        private string[] mutations = { "Placement / Move", "G / R / handles", "Connect", "Paint / properties", "Copies / paste", "Custom models / mounts", "Chains", "Simulation set pose", "Delete", "Personal undo / redo" };
        public static bool IsShared => instance != null && instance.client != null;
        internal static void RequireRoomAppearanceCapacity(long addition) {
            var accepted = instance.committed;
            var costs = DocumentModel.Assets(accepted).ToDictionary(asset => asset.hash, GeometryTransfer.MaterializedBytes, StringComparer.Ordinal);
            GeometryTransfer.RequireSceneBudget(accepted, hash => costs[hash], addition);
        }
        internal static int CustomAppearanceVersion => IsShared ? instance.client.AppearanceVersion : CustomAppearance.Version;
        public static int CustomGeometryByteLimit => IsShared ? instance.client.GeometryAssetByteLimit : GeometryTransfer.MaxAssetBytes;
        public static bool IsApplying => instance != null && instance.applying;
        public static bool CanPreview => !IsShared || instance.client.IsConnected && !instance.busy && !instance.submitting && !instance.reserving && instance.committed.revision >= instance.lastSubmittedRevision;
        public static bool CanEdit => CanPreview && (!IsShared || instance.client.Participant.role != "viewer");
        public static bool HasPending => IsShared && (instance.submitting || instance.client.PendingCount > 0);
        public static bool HasReservation => IsShared && (instance.lease != null || instance.reserving);
        public static bool HasActivePreview => HasReservation || IsShared && Simulation.SimulationTool.HasPreview;
        public static long CommittedRevision => IsShared ? instance.committed.revision : 0;
        public static string CommittedHash => IsShared && instance.committed != null ? DocumentModel.Hash(instance.committed) : null;
        public static bool ContainsPointer => instance != null && instance.ContainsUIPointer;
        public static bool BlocksLegacyMutation => IsShared && !IsApplying;
        public static bool BlocksUIInput => instance != null && (instance.panel || instance.busy);
        public static bool CancelPanel() {
            if (instance == null || !instance.panel && instance.connectionAttempt == null) return false;
            instance.ClosePanel(); return true;
        }
        public static BuildData SaveSnapshot() => IsShared ? CollaborationScene.ToBuild(instance.committed) : SceneBuild.ToBuildData();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install() {
            if (instance != null) return;
            new GameObject("Collaboration Session").AddComponent<CollaborationSession>();
        }
        private void Awake() {
            instance = this;
            displayName = CollaborationArguments.Get("--collab-name", Environment.UserName);
            endpoint = LocalPreferences.GetString(LastServerPreference, endpoint);
            endpoint = CollaborationArguments.Get("--collab-join", endpoint);
            endpoint = CollaborationArguments.Get("--collab-server", endpoint);
            room = CollaborationArguments.Get("--collab-room", room);
            var requestedTransport = CollaborationArguments.Get("--collab-transport", "auto");
            networkRoute = CollaborationArguments.Get("--collab-network-route", "system") == "direct" ? 1 : 0;
            invalidNetworkArgument = CollaborationArguments.Get("--collab-network-route", "system") != "system" && CollaborationArguments.Get("--collab-network-route") != "direct";
            transport = requestedTransport == "long-polling" ? CollaborationTransport.LongPolling : requestedTransport == "websocket" ? CollaborationTransport.WebSocket : requestedTransport == "short-polling" ? CollaborationTransport.ShortPolling : CollaborationTransport.Auto;
            InitializeUI();
            Application.quitting += OnQuitting;
        }
        private void Start() {
            if (CollaborationArguments.Get("--collab-server") != null) OpenPanel(1);
            if (CollaborationArguments.Get("--collab-join") != null) RequestJoin();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CollaborationArguments.Get("--collab-probe") != null) _ = CollaborationPlayerProbe.RunAsync(this);
#endif
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal Task StartProbeHostAsync() { port = "0"; return HostAsync(); }
        internal string ProbeEndpoint => endpoint;
        internal Document ProbeDocument => committed == null ? null : GeometryTransfer.CloneDocument(committed);
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
                case "Enable Simulation Tool": case "Simulation Animation": case "Simulation Play / Pause": case "Simulation Reset":
                case "Simulation Faster": case "Simulation Slower": case "Simulation Cancel": return CanPreview && !HasReservation;
                case "Simulation Commit": return CanEdit && !HasReservation;
                case "Simulation Chain Selected Sprockets": return CanEdit && !HasReservation;
                // A held pointer must survive the asynchronous reservation wait; dragging starts only after the grant.
                case "Tool Drag": return instance.client.IsConnected && instance.client.Participant.role != "viewer";
                case "Grab Selection": case "Mirror Selection": case "Rotate Placement": case "Delete Selection": case "Place":
                case "Grab Hole Snap": case "Duplicate": case "Pick Part": case "Copy Selection": case "Paste Selection":
                case "Undo": case "Redo": case "Enable Transform Tool": case "Enable Move Tool": case "Enable Color Tool": case "Enable Connect Tool":
                case "Place Selection": case "Import Build": case "Save Selected Parts As": case "Import Custom Model":
                case "Add Custom Mounting Points": case "Change Mounting Depth": case "Recalibrate Mounting Grid": return CanEdit;
                case "Increase First Property": case "Decrease First Property": case "Increase Second Property": case "Decrease Second Property":
                    return CanEdit;
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
            var attempt = BeginConnection("Starting room…");
            try {
                ValidateSettings(false);
                CancelTools();
                await hostShutdown;
                attempt.Token.ThrowIfCancellationRequested();
                catalog = CollaborationScene.Catalog;
                committed = CollaborationScene.CaptureDocument(catalog);
                string hostedBaselineHash = DocumentModel.Hash(committed);
                var builds = FindFirstObjectByType<BuildsManager>();
                bool wasSaved = builds.CurrentBuildIsSaved;
                if (!int.TryParse(port, out var listenPort) || listenPort < 0 || listenPort > 65535) throw new ArgumentException("Use a port from 0 to 65535.");
                var owner = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var seed = new Bootstrap { room = room, password = password, ownerCredential = owner, catalog = catalog, document = committed,
                    allowEditorProjectReplacement = allowEditorProjectReplacement };
                hostedRoom = CollaborationRecovery.Create(seed, listenPort);
                host = new CollaborationHost();
                await host.StartAsync(seed, listenPort, lan, hostedRoom.directory, attempt.Token);
                hostedRoom.port = host.BoundPort; CollaborationRecovery.Save(hostedRoom);
                endpoint = host.Url;
                await ConnectAsync(owner, true, attempt);
                builds.BeginHostedDocument(hostedBaselineHash, wasSaved);
                if (lan) AdvertiseLan();
                status = lan ? "Hosting on LAN · reachability from another computer is untested." : "Hosting on loopback · other local players can join.";
            }
            catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) {
                status = "Connection canceled. The current build and local file are unchanged. Recovery data is kept for retry.";
                if (host != null) await host.StopAsync(); host = null; client?.Dispose(); client = null; hostedRoom = null;
            }
            catch (Exception error) { if (!closing) Report(error); if (host != null) await host.StopAsync(); host = null; client?.Dispose(); client = null; hostedRoom = null; }
            finally { ClearPassword(); EndConnection(attempt); if (!closing) RefreshRecoveredRooms(); }
        }

        private void RefreshRecoveredRooms() {
            try { recoveredRooms = CollaborationRecovery.List(out unreadableRecovery); }
            catch { recoveredRooms = Array.Empty<CollaborationRecovery.HostedRoom>(); unreadableRecovery = true; }
        }
        private void RequestRecovery(CollaborationRecovery.HostedRoom saved) {
            if (busy || IsShared) return;
            CancelTools(); ClosePanel();
            FindFirstObjectByType<BuildsManager>().RunAfterUnsavedChanges(() => _ = RecoverFromUIAsync(saved));
        }
        private async Task RecoverFromUIAsync(CollaborationRecovery.HostedRoom saved) { var recovering = RecoverHostAsync(saved); var attempt = connectionAttempt; await recovering; if (!closing && attempt?.ClosePanelRequested != true) OpenPanel(); }
        private async Task RecoverHostAsync(CollaborationRecovery.HostedRoom saved) {
            if (busy || IsShared) return;
            var attempt = BeginConnection("Recovering accepted room…");
            try {
                CancelTools();
                await hostShutdown;
                attempt.Token.ThrowIfCancellationRequested();
                hostedRoom = CollaborationRecovery.Read(saved.directory);
                catalog = CollaborationScene.Catalog;
                DocumentModel.Require(hostedRoom.catalogHash == catalog.hash, "recovery-catalog-mismatch-use-the-matching-build");
                room = hostedRoom.room;
                int listenPort = hostedRoom.port;
                if (portEdited && (!int.TryParse(port, out listenPort) || listenPort < 0 || listenPort > 65535)) throw new ArgumentException("Use a port from 0 to 65535.");
                host = new CollaborationHost(); await host.StartAsync(null, listenPort, lan, hostedRoom.directory, attempt.Token);
                DocumentModel.Require(host.Recovered, "recovery-room-not-found");
                hostedRoom.port = host.BoundPort; CollaborationRecovery.Save(hostedRoom);
                endpoint = host.Url;
                await ConnectAsync(hostedRoom.ownerCredential, false, attempt, acceptReplacement: true);
                if (lan) AdvertiseLan();
                status = "Recovered accepted room · local files use Save As. " + string.Join(" ", client.RecoveryMessages);
            }
            catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) {
                status = "Connection canceled. The current build and local file are unchanged. Recovery data is kept for retry.";
                if (host != null) await host.StopAsync(); host = null; client?.Dispose(); client = null; hostedRoom = null;
            }
            catch (Exception error) { if (!closing) Report(error); if (host != null) await host.StopAsync(); host = null; client?.Dispose(); client = null; hostedRoom = null; }
            finally { ClearPassword(); EndConnection(attempt); if (!closing) RefreshRecoveredRooms(); }
        }
        private void AdvertiseLan() {
            var address = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            if (address != null) endpoint = new UriBuilder(host.Url) { Host = address.ToString() }.Uri.GetLeftPart(UriPartial.Authority);
        }

        private void RequestJoin() {
            ownerJoin = null; acceptReplacementJoin = false; RequestJoinCore();
        }
        private void RequestJoinCore() {
            if (busy || IsShared || AdmissionCooldownSeconds > 0) return;
            try { ValidateSettings(true); } catch (Exception error) { Report(error); return; }
            CancelTools(); ClosePanelCore(false);
            var builds = FindFirstObjectByType<BuildsManager>();
            builds.RunAfterUnsavedChanges(() => _ = JoinFromUIAsync());
        }
        private async Task JoinFromUIAsync() { var joining = JoinAsync(); var attempt = connectionAttempt; await joining; if (!closing && attempt?.ClosePanelRequested != true) OpenPanel(); }
        private async Task JoinAsync() {
            if (busy || IsShared || AdmissionCooldownSeconds > 0) return;
            var attempt = BeginConnection("Connecting to room…");
            try {
                ValidateSettings(true); await hostShutdown; attempt.Token.ThrowIfCancellationRequested(); catalog = CollaborationScene.Catalog;
                if (ownerJoin != null) {
                    DocumentModel.Require(ownerJoin.catalogHash == catalog.hash, "catalog-or-room-mismatch");
                    using var remote = new RemoteRoomCreation(endpoint, lan, await SelectedNetworkAsync(attempt.Token));
                    await remote.CheckAsync(ownerJoin, attempt.Token);
                }
                await ConnectAsync(ownerJoin?.ownerCredential, false, attempt, ownerJoin, ownerJoin != null || acceptReplacementJoin); status = "Connected to " + room;
                RememberServer(endpoint); savedAccessSignature = directorySignature = null;
            }
            catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) {
                status = "Connection canceled. The current build and local file are unchanged. Saved membership is kept for retry.";
            }
            catch (Exception error) { if (!closing) Report(error); client?.Dispose(); client = null; }
            finally { ownerJoin = null; acceptReplacementJoin = false; ClearPassword(); EndConnection(attempt); }
        }
        private async Task ConnectAsync(string owner, bool hosting, ConnectionAttempt attempt, CreatedRoom createdRoom = null, bool acceptReplacement = false) {
            attempt.Token.ThrowIfCancellationRequested();
            string recovery = createdRoom != null ? CreatedRoomRecovery.FileName(CreatedRoomsRoot, endpoint, room) + ".session" :
                owner != null && hostedRoom != null ? CollaborationRecovery.OwnerSession(hostedRoom) : CollaborationRecovery.SessionFile(endpoint, room, catalog.hash);
            var network = owner != null && hostedRoom != null ? NetworkPolicy.Direct : await SelectedNetworkAsync(attempt.Token);
            var candidate = new CollaborationClient(endpoint, room, catalog, transport, lan, recovery, network);
            attempt.Candidate = candidate;
            try {
                await candidate.JoinAsync(displayName, password, owner, owner == null && joinAsViewer, attempt.Token, attempt, acceptReplacement);
                attempt.Token.ThrowIfCancellationRequested();
                if (hosting && committed.parts.Any(part => part.mirrored))
                    DocumentModel.Require(candidate.SupportsMirror, "mirror-server-required");
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
            catch {
                if (candidate.RequiresResynchronization) { resyncJoinOrigin = endpoint; resyncJoinRoom = room; }
                candidate.Dispose(); throw;
            }
        }

        private void Subscribe(CollaborationClient connected) {
            connected.Accepted += edit => {
                if (!Enqueue(() => {
                    if (client != connected || edit.revision <= committed.revision) return;
                    applying = true;
                    try {
                        DocumentModel.Require(edit.revision == committed.revision + 1, "scene-revision-gap");
                        // Apply this revision even if the network thread has already received later edits.
                        var next = connected.Materialize(DocumentModel.Reduce(committed, edit.transaction, catalog));
                        var ids = edit.transaction.writes.Select(p => p.id).Concat(edit.transaction.deletes);
                        Simulation.SimulationTool.BeforeSharedEdit(edit.author == connected.Participant.id);
                        if (edit.author != connected.Participant.id && reserved.Intersect(ids).Any()) CancelSharedPreview();
                        CollaborationScene.Apply(edit, next); committed = next;
                        if (selectAfterApply.Remove(edit.transaction.operationId, out var result)) SelectParts(result.manager, result.ids);
                    }
                    catch (Exception error) { Report(error); connected.Suspend("Scene apply failed; reconnect to synchronize."); CollaborationScene.Load(committed); }
                    finally { applying = false; }
                }, WireJson.Encode(edit).Length)) connected.Suspend("Scene updates fell behind; reconnect to synchronize.");
            };
            connected.Presence += update => { lock (queueGate) { latestPresence = update; presenceClient = connected; } };
            connected.Disconnected += reason => Enqueue(() => {
                if (client != connected) return;
                CancelTools(); CancelPose(); ClearGhosts(); status = reason == "server-stopping"
                    ? FriendlyStatus(reason)
                    : WireFailure.Code(new CollaborationException(reason));
                CollaborationScene.ApplyParts(committed.parts);
            });
            connected.PendingResolved += message => Enqueue(() => status = "Pending edit outcome checked. Reconnect to synchronize the accepted build.");
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

        internal static bool CheckEditBudget(int reservedParts, int mutations, int addedParts = 0, int addedRoutes = 0, int addedAssets = 0) {
            if (!IsShared) return true;
            string error = instance.EditBudgetError(reservedParts, mutations, addedParts, addedRoutes, addedAssets);
            return error == null || instance.EditLimit(error);
        }

        private string EditBudgetError(int reservedParts, int mutations, int addedParts = 0, int addedRoutes = 0, int addedAssets = 0) {
            if (reservedParts > client.ReservationPartLimit)
                return $"This edit needs to reserve {reservedParts} unique parts. The room limit is {client.ReservationPartLimit}.";
            if (mutations > client.TransactionMutationLimit)
                return $"This edit needs {mutations} mutations (parts, routes and new assets). The room limit is {client.TransactionMutationLimit}.";
            int parts = committed.parts.Length + addedParts;
            if (addedParts > 0 && parts > DocumentModel.MaxParts)
                return $"This edit would result in {parts} parts. The room limit is {DocumentModel.MaxParts}.";
            int routes = DocumentModel.Chains(committed).Length + addedRoutes;
            if (addedRoutes > 0 && routes > DocumentModel.MaxChains)
                return $"This edit would result in {routes} routes. The room limit is {DocumentModel.MaxChains}.";
            int assets = DocumentModel.Assets(committed).Length + addedAssets;
            if (addedAssets > 0 && assets > DocumentModel.MaxAssets)
                return $"This edit would result in {assets} custom assets. The room limit is {DocumentModel.MaxAssets}.";
            return null;
        }

        internal static string RouteBudgetError(IEnumerable<string> previous, IEnumerable<string> next, bool addsRoute) =>
            IsShared ? instance.EditBudgetError(previous.Union(next, StringComparer.Ordinal).Count(), 1, addedRoutes: addsRoute ? 1 : 0) : null;

        private bool EditLimit(string message) {
            status = message;
            InteractionFeedback.Notify(message);
            Debug.LogWarning("Collaboration: " + message);
            return false;
        }

        internal static int NewAssetCount(IEnumerable<CustomModelData> models) {
            var existing = DocumentModel.Assets(instance.committed).Select(asset => asset.hash).ToHashSet(StringComparer.Ordinal);
            return models.Count(data => existing.Add(CollaborationScene.CaptureAsset(data).hash));
        }

        internal static bool CheckPlacementBudget(int parts, int routes = 0, int assets = 0) =>
            CheckEditBudget(0, parts + routes + assets, parts, routes, assets);

        public static void ReservePose(IReadOnlyList<GameObject> bodies, Action granted, Action denied = null, bool sendPreviews = true,
            bool copy = false, bool moveOriginals = true) {
            if (instance == null) return;
            var saved = CollaborationScene.Bodies(bodies);
            if (saved.Count == 0) { denied?.Invoke(); return; }
            _ = instance.ReservePartsAsync(saved, sendPreviews, granted, denied, copy, moveOriginals);
        }
        public static void ReserveCustomMounts(IReadOnlyList<CustomModel> models, Action granted, Action denied = null) {
            if (instance == null) return;
            var saved = CollaborationScene.Bodies((models ?? Array.Empty<CustomModel>()).Where(model => model != null).Select(model => model.gameObject));
            if (saved.Count == 0) { denied?.Invoke(); return; }
            _ = instance.ReservePartsAsync(saved, false, granted, denied);
        }
        private async Task ReservePartsAsync(IReadOnlyList<GameObject> bodies, bool sendPreviews, Action granted, Action denied,
            bool copy = false, bool moveOriginals = true) {
            if (!CanEdit || lease != null || reserving) return;
            var ids = bodies.Select(b => b.GetComponent<SavedObject>().ConnectionId).Distinct().ToArray();
            bool FitsBudget() {
                int routes = copy ? CompleteRoutes(ids.ToHashSet(StringComparer.Ordinal)).Count() : 0;
                return CheckEditBudget(ids.Length, ids.Length + (copy && moveOriginals ? ids.Length : 0) + routes,
                    copy ? ids.Length : 0, routes);
            }
            if (!FitsBudget()) { denied?.Invoke(); return; }
            reserving = true; status = "Waiting for edit reservation…";
            var previousRequest = reservationPending;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            reservationPending = completed.Task;
            var versions = Versions(ids);
            int request = ++reservationGeneration;
            var connection = client;
            long epoch = connection.ConnectionEpoch;
            try {
                await previousRequest;
                await releasePending;
                if (client != connection || connection.ConnectionEpoch != epoch || request != reservationGeneration) return;
                var result = await connection.ReserveAsync(ids);
                if (client != connection || connection.ConnectionEpoch != epoch || request != reservationGeneration || bodies.Any(b => b == null || !b.activeInHierarchy)) {
                    try { await connection.ReleaseAsync(result.token); } catch { } denied?.Invoke(); return;
                }
                if (copy && !FitsBudget()) {
                    releasePending = Release(result.token); denied?.Invoke(); return;
                }
                lease = result.token; reserved = ids; reservedVersions = versions; broadcastReservationPreview = sendPreviews;
                if (sendPreviews) previewIds = ids.OrderBy(id => id, StringComparer.Ordinal)
                    .Take(CollaborationProtocol.PreviewPartCount(ids.Length)).ToHashSet(StringComparer.Ordinal);
                nextRenew = Time.realtimeSinceStartup + 3; nextPreview = Time.realtimeSinceStartup; previewSequence = 0;
                reserving = false;
                status = "Editing reserved parts"; granted();
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch && request == reservationGeneration) { CancelPose(); Report(error); denied?.Invoke(); } }
            finally { completed.TrySetResult(true); if (client == connection && connection.ConnectionEpoch == epoch && request == reservationGeneration) reserving = false; }
        }
        public static bool HasPoseReservation(IEnumerable<GameObject> bodies) => IsShared && instance.lease != null &&
            CollaborationScene.Bodies(bodies).Select(b => b.GetComponent<SavedObject>().ConnectionId).OrderBy(x => x).SequenceEqual(instance.reserved.OrderBy(x => x));
        public static void CancelPose() {
            if (!IsShared) return;
            if (instance.submitting && instance.client.IsConnected) return;
            instance.reservationGeneration++; instance.reserving = false;
            var token = instance.lease; instance.lease = null; instance.reserved = Array.Empty<string>(); instance.reservedVersions = Array.Empty<VersionRef>();
            instance.broadcastReservationPreview = false; instance.previewIds = null;
            if (token != null) instance.releasePending = instance.Release(token);
        }
        private async Task Release(string token) { var connection = client; try { if (connection != null) await connection.ReleaseAsync(token); } catch { } }
        private void CancelSharedPreview() {
            CancelTools();
            FindFirstObjectByType<ConnectTool>()?.CancelPending();
            CancelPose();
        }
        public static VersionRef[] Versions(IEnumerable<string> ids) => instance?.committed == null ? Array.Empty<VersionRef>() :
            ids.Distinct().Select(id => new VersionRef { id = id, version = instance.committed.parts.FirstOrDefault(p => p.id == id)?.version ??
                instance.committed.tombstones.FirstOrDefault(p => p.id == id)?.version ?? 0 }).ToArray();
        public static Part AcceptedPart(string id) => IsShared ? instance.committed?.parts.FirstOrDefault(part => part.id == id) : null;
        public static VersionRef[] ChainVersions(IEnumerable<string> ids) => instance?.committed == null ? Array.Empty<VersionRef>() :
            ids.Distinct().Select(id => new VersionRef { id = id, version = DocumentModel.Chains(instance.committed).FirstOrDefault(c => c.id == id)?.version ??
                DocumentModel.ChainTombstones(instance.committed).FirstOrDefault(c => c.id == id)?.version ?? 0 }).ToArray();
        private Transaction CreateTransaction(string kind, Part[] writes, string[] deletes = null, VersionRef[] dependencies = null, string token = null, long? expectedRevision = null,
            ChainRecord[] chainWrites = null, string[] chainDeletes = null, VersionRef[] chainDependencies = null, GeometryAsset[] assetWrites = null) {
            var expected = (dependencies ?? Array.Empty<VersionRef>()).ToDictionary(v => v.id);
            foreach (var version in Versions(writes.Select(p => p.id).Concat(deletes ?? Array.Empty<string>())))
                if (!expected.ContainsKey(version.id)) expected.Add(version.id, version);
            var expectedChains = (chainDependencies ?? Array.Empty<VersionRef>()).ToDictionary(v => v.id);
            foreach (var version in ChainVersions((chainWrites ?? Array.Empty<ChainRecord>()).Select(c => c.id).Concat(chainDeletes ?? Array.Empty<string>())))
                if (!expectedChains.ContainsKey(version.id)) expectedChains.Add(version.id, version);
            var transaction = client.CreateTransaction(kind, writes, deletes, expected.Values.ToArray(), token,
                chainWrites ?? Array.Empty<ChainRecord>(), chainDeletes ?? Array.Empty<string>(), expectedChains.Values.ToArray(), assetWrites ?? Array.Empty<GeometryAsset>());
            transaction.expectedRevision = expectedRevision;
            return transaction;
        }
        public static void ConfirmPose(IEnumerable<GameObject> bodies, List<GameObject> copies = null, VersionRef[] dependencies = null) {
            if (!IsShared) return;
            var session = instance;
            try {
                DocumentModel.Require(session.client.IsConnected, "disconnected");
                var sourceBodies = CollaborationScene.Bodies(bodies);
                var copiedChains = session.CaptureCopiedRoutes(sourceBodies, copies);
                var writes = CollaborationScene.Bodies(sourceBodies.Concat(copies ?? Enumerable.Empty<GameObject>())).Select(b => CollaborationScene.Capture(b)).ToArray();
                if ((copies == null || copies.Count == 0) && writes.All(p => DocumentModel.SamePart(p, session.committed.parts.FirstOrDefault(before => before.id == p.id)))) { CancelPose(); return; }
                var expected = (dependencies ?? Array.Empty<VersionRef>()).Concat(session.reservedVersions).GroupBy(v => v.id).Select(g => g.First()).ToArray();
                var tx = session.CreateTransaction(copies?.Count > 0 ? "TransformAndCopy" : "SetPoses", writes, dependencies: expected, token: session.lease,
                    chainWrites: copiedChains, chainDependencies: ChainVersions(copiedChains.Select(c => c.id)));
                if (copies != null) ObjectDuplicator.DiscardCopies(copies);
                _ = session.Submit(tx);
            }
            catch (CollaborationException error) {
                CollaborationScene.ApplyParts(session.committed.parts.Where(p => session.reserved.Contains(p.id)));
                CancelPose(); session.Report(error);
                if (copies != null) ObjectDuplicator.DiscardCopies(copies);
            }
        }
        public static void ConfirmCustomMounts(IEnumerable<CustomModel> models) {
            if (!IsShared) return;
            var session = instance;
            var layouts = (models ?? Enumerable.Empty<CustomModel>()).Where(model => model != null)
                .GroupBy(model => model.GetComponent<SavedObject>().ConnectionId).Select(group => group.First()).ToArray();
            var ids = layouts.Select(model => model.GetComponent<SavedObject>().ConnectionId).ToArray();
            try {
                DocumentModel.Require(session.client.IsConnected && session.lease != null, "lease-expired");
                DocumentModel.Require(ids.Length > 0 && ids.OrderBy(id => id).SequenceEqual(session.reserved.OrderBy(id => id)), "lease-set-mismatch");
                var writes = layouts.Select(CollaborationScene.CaptureCustomMounts).ToArray();
                if (writes.All(write => DocumentModel.SamePart(write, session.committed.parts.FirstOrDefault(part => part.id == write.id)))) {
                    CancelPose(); return;
                }
                var tx = session.CreateTransaction("SetCustomMounts", writes, dependencies: session.reservedVersions, token: session.lease);
                _ = session.Submit(tx, () => CollaborationScene.ApplyCustomMounts(session.committed.parts.Where(part => ids.Contains(part.id)).ToArray()));
            }
            catch (Exception error) {
                CollaborationScene.ApplyCustomMounts(session.committed.parts.Where(part => ids.Contains(part.id)).ToArray());
                CancelPose(); session.Report(error);
            }
        }
        public static void CancelCustomMounts() => CancelPose();
        internal static void ConfirmMirror(GameObject[] bodies, Vector3 pivot, Vector3 normal, SelectionManager selectCopies) {
            if (!IsShared) return;
            var session = instance;
            try {
                DocumentModel.Require(session.client.SupportsMirror, "mirror-server-required");
                DocumentModel.Require(HasPoseReservation(bodies), "lease-set-mismatch");
                var ids = session.reserved.ToDictionary(id => id, id => selectCopies != null ? Guid.NewGuid().ToString("N") : id);
                var writes = session.committed.parts.Where(part => ids.ContainsKey(part.id)).Select(source => {
                    var part = WireJson.Clone(source);
                    part.id = ids[source.id];
                    part.mirrored = !part.mirrored;
                    var pose = PartReflection.Across(CollaborationScene.GetPose(source), pivot, normal);
                    part.position = new double[] { pose.position.x, pose.position.y, pose.position.z };
                    part.rotation = new double[] { pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w };
                    return part;
                }).ToArray();
                DocumentModel.Require(writes.Length == ids.Count, "stale-entity");
                var routes = selectCopies != null ? session.CaptureCopiedRoutes(ids) : Array.Empty<ChainRecord>();
                var routeSources = selectCopies != null ? session.CompleteRoutes(ids.Keys.ToHashSet(StringComparer.Ordinal))
                    .Select(route => route.id) : Enumerable.Empty<string>();
                var tx = session.CreateTransaction(selectCopies != null ? "MirrorAndCopy" : "MirrorParts", writes,
                    dependencies: session.reservedVersions, token: session.lease,
                    chainWrites: routes, chainDependencies: ChainVersions(routeSources));
                if (selectCopies != null)
                    session.selectAfterApply[tx.operationId] = (selectCopies, writes.Select(part => part.id).ToArray());
                _ = session.Submit(tx);
            }
            catch (Exception error) { CancelPose(); session.Report(error); }
        }

        private ChainRecord[] CaptureCopiedRoutes(List<GameObject> sourceBodies, List<GameObject> copies) {
            if (copies == null || copies.Count == 0) return Array.Empty<ChainRecord>();
            var sourceParts = sourceBodies.Where(body => body != null).SelectMany(body => body.GetComponentsInChildren<SavedObject>(true))
                .Select(saved => saved.gameObject).Distinct().ToList();
            var sourceRoots = ObjectDuplicator.CopyRoots(sourceParts);
            DocumentModel.Require(sourceRoots.Count == copies.Count, "copy-map-mismatch");
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < sourceRoots.Count; i++) {
                var source = sourceRoots[i].GetComponentsInChildren<SavedObject>(true);
                var copy = copies[i].GetComponentsInChildren<SavedObject>(true);
                DocumentModel.Require(source.Length == copy.Length, "copy-map-mismatch");
                for (int j = 0; j < source.Length; j++) ids[source[j].ConnectionId] = copy[j].ConnectionId;
            }
            return CaptureCopiedRoutes(ids);
        }

        private IEnumerable<ChainRecord> CompleteRoutes(HashSet<string> ids) =>
            DocumentModel.Chains(committed).Where(route => route.members.All(ids.Contains));

        private ChainRecord[] CaptureCopiedRoutes(Dictionary<string, string> ids) {
            var used = committed.parts.Select(p => p.id).Concat(committed.tombstones.Select(p => p.id))
                .Concat(DocumentModel.Chains(committed).Select(c => c.id)).Concat(DocumentModel.ChainTombstones(committed).Select(c => c.id)).ToHashSet(StringComparer.Ordinal);
            return CompleteRoutes(ids.Keys.ToHashSet(StringComparer.Ordinal)).Select(chain => {
                string id; do id = Guid.NewGuid().ToString("N"); while (!used.Add(id));
                return new ChainRecord { id = id, members = chain.members.Select(member => ids[member]).ToArray(),
                    routeVersion = chain.routeVersion, wraps = chain.wraps?.ToArray(), rope = chain.rope };
            }).ToArray();
        }
        public static void PlaceCatalog(GameObject temporary, VersionRef[] dependencies = null) => PlaceCopies(new List<GameObject> { temporary }, dependencies);
        public static void PlaceCopies(List<GameObject> temporary, VersionRef[] dependencies = null, SelectionManager selectResult = null,
            List<GameObject> temporaryChains = null) => _ = PlaceCopiesAsync(temporary, dependencies, selectResult, temporaryChains);
        public static async Task<bool> PlaceCopiesAsync(List<GameObject> temporary, VersionRef[] dependencies = null, SelectionManager selectResult = null,
            List<GameObject> temporaryChains = null) {
            var session = instance;
            try {
                if (!IsShared || !CanEdit) {
                    InteractionFeedback.Notify("Could not add parts to the shared room. Reconnect as an editor or wait for the current edit to finish.");
                    return false;
                }
                var parts = CollaborationScene.Bodies(temporary).Select(b => CollaborationScene.Capture(b)).ToArray();
                var existingAssets = DocumentModel.Assets(session.committed).Select(asset => asset.hash).ToHashSet(StringComparer.Ordinal);
                var assets = CollaborationScene.CaptureAssets(temporary).Where(asset => !existingAssets.Contains(asset.hash)).ToArray();
                var chains = (temporaryChains ?? new List<GameObject>()).Where(obj => obj != null).Select(obj =>
                    obj.TryGetComponent<Simulation.RopeRoute>(out var rope) ? CollaborationScene.CaptureRope(rope.Data) :
                    obj.GetComponent<Simulation.ChainLoop>()?.Capture()).Where(route => route != null).ToArray();
                if (!CheckPlacementBudget(parts.Length, chains.Length, assets.Length)) return false;
                var tx = session.CreateTransaction("AddParts", parts, dependencies: dependencies,
                    chainWrites: chains, chainDependencies: ChainVersions(chains.Select(c => c.id)), assetWrites: assets);
                if (selectResult != null) session.selectAfterApply[tx.operationId] = (selectResult, parts.Select(p => p.id).ToArray());
                // Staging objects must disappear before the accepted event materializes their IDs.
                ObjectDuplicator.DiscardCopies(temporary); temporary = null;
                if (temporaryChains != null) { ObjectDuplicator.DiscardCopies(temporaryChains); temporaryChains = null; }
                return await session.Submit(tx);
            }
            catch (Exception error) { if (session != null) session.Report(error); return false; }
            finally {
                if (temporary != null) ObjectDuplicator.DiscardCopies(temporary);
                if (temporaryChains != null) ObjectDuplicator.DiscardCopies(temporaryChains);
            }
        }
        private static void SelectParts(SelectionManager manager, string[] ids) {
            if (manager == null) return;
            var bodies = PartsManager.FindLoadedObjects().Where(b => ids.Contains(b.GetComponent<SavedObject>().ConnectionId)).ToList();
            manager.ClearCurrent(false, true, true);
            manager.SelectExact(bodies);
        }
        public static void Paint(GameObject[] bodies, Color color, bool reset) {
            if (!IsShared || !CanEdit) return;
            var writes = bodies.Select(body => {
                var part = WireJson.Clone(instance.committed.parts.First(p => p.id == body.GetComponent<SavedObject>().ConnectionId));
                var desired = reset ? OwnedPartMaterials.CaptureDefaults(body).DefaultColors().Select(c => c.color).FirstOrDefault() : color;
                part.hasPaint = !reset; part.color = new double[] { desired.r, desired.g, desired.b }; return part;
            }).ToArray();
            _ = instance.ChangeParts(writes);
        }
        public static void ReplacePart(GameObject original, PartGenerator generator, Vector3? anchor, Vector3? localHole) {
            if (!IsShared || !CanEdit) return;
            var replacement = PartParameterEditing.Replace(original, generator, anchor, localHole);
            if (replacement == null) return;
            var write = CollaborationScene.Capture(replacement);
            ObjectDuplicator.DiscardCopies(new List<GameObject> { replacement });
            _ = instance.ChangeParts(new[] { write });
        }
        // Frozen results from local-only tools use a scoped reservation, without broadcasting their private preview.
        public static Task<bool> CommitResolvedPosesAsync(Part[] writes, VersionRef[] dependencies, long expectedRevision) {
            if (!IsShared || !CanEdit || HasReservation) return Task.FromResult(false);
            return instance.ChangeParts(WireJson.Clone(writes), "SetPoses", dependencies, expectedRevision);
        }
        private async Task<bool> ChangeParts(Part[] writes, string kind = "SetParts", VersionRef[] dependencies = null, long? expectedRevision = null) {
            if (lease != null || !CheckEditBudget(writes.Length, writes.Length)) return false;
            submitting = true;
            var connection = client; long epoch = connection.ConnectionEpoch;
            var expected = dependencies ?? Versions(writes.Select(p => p.id));
            string token = null;
            try {
                await reservationPending;
                await releasePending;
                token = (await connection.ReserveAsync(writes.Select(p => p.id).ToArray())).token;
                if (client != connection || connection.ConnectionEpoch != epoch) return false;
                var receipt = await connection.SubmitAsync(CreateTransaction(kind, writes, dependencies: expected, token: token, expectedRevision: expectedRevision));
                if (client != connection || connection.ConnectionEpoch != epoch) return false;
                if (receipt.accepted) lastSubmittedRevision = Math.Max(lastSubmittedRevision, receipt.revision);
                status = receipt.accepted ? "Accepted revision " + receipt.revision : "Edit rejected: " + receipt.error;
                return receipt.accepted;
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch) Report(error); return false; }
            finally { if (token != null) { try { await connection.ReleaseAsync(token); } catch { } } if (client == connection && connection.ConnectionEpoch == epoch) submitting = false; }
        }
        public static void History(bool redo) {
            if (!IsShared || !CanEdit) return;
            instance.CancelSharedPreview();
            _ = instance.Submit(instance.CreateTransaction(redo ? "Redo" : "Undo", Array.Empty<Part>()));
        }
        public static void ToggleChain(IEnumerable<Simulation.SimulationPart> parts) {
            if (!IsShared || !CanEdit) return;
            var selected = parts?.Distinct().ToList() ?? new List<Simulation.SimulationPart>();
            if (Simulation.ChainLoop.Find(selected.Select(p => p.GetComponent<SavedObject>().ConnectionId)) == null &&
                !Simulation.ChainLoop.Compatible(selected, out var reason)) { InteractionFeedback.Notify(reason); return; }
            instance.ChangeChain(selected);
        }
        public static void SetChainRoute(Builds.ChainData data, bool remove = false) {
            if (!IsShared || !CanEdit) return;
            if (data.members?.Length > DocumentModel.MaxRouteMembers) {
                instance.EditLimit($"The chain has {data.members.Length} parts. The route limit is {DocumentModel.MaxRouteMembers}.");
                return;
            }
            if (!DocumentModel.ValidRoute(data.members, data.routeVersion, data.wraps)) return;
            var byId = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            var parts = data.members.Select(id => byId.TryGetValue(id, out var body) ? body.GetComponent<Simulation.SimulationPart>() : null).ToList();
            if (!remove && !Simulation.ChainRouteGeometry.TryBuild(parts, data.routeVersion, data.wraps, out _, out var reason)) {
                InteractionFeedback.Notify(reason); return;
            }
            instance.ChangeChain(parts, data, remove);
        }
        private void ChangeChain(List<Simulation.SimulationPart> parts, ChainData route = null, bool remove = false) {
            if (parts.Any(part => part == null)) return;
            var memberIds = parts.Select(p => p.GetComponent<SavedObject>().ConnectionId).Distinct().ToArray();
            var existing = DocumentModel.Chains(committed).FirstOrDefault(c => c.rope == null && c.members.ToHashSet().SetEquals(memberIds));
            var write = !remove && (route != null || existing == null) ? new ChainRecord {
                id = existing?.id ?? Guid.NewGuid().ToString("N"), members = memberIds,
                routeVersion = route?.routeVersion ?? 0, wraps = route?.wraps?.ToArray()
            } : null;
            _ = ChangeRouteAsync(existing, write, "Chain");
        }
        public static void SetRopeRoute(RopeRouteData route, bool remove = false) {
            if (!IsShared || !CanEdit) return;
            var session = instance;
            var existing = DocumentModel.Chains(session.committed).FirstOrDefault(c => c.rope != null && c.members[0] == route.start.part);
            var write = remove ? null : CollaborationScene.CaptureRope(route);
            if (write != null) write.id = existing?.id ?? Guid.NewGuid().ToString("N");
            _ = session.ChangeRouteAsync(existing, write, "Rope");
        }
        private async Task ChangeRouteAsync(ChainRecord existing, ChainRecord write, string label) {
            if (write == null && existing == null) return;
            if (lease != null || submitting) return;
            submitting = true;
            var connection = client; long epoch = connection.ConnectionEpoch; string token = null;
            try {
                var memberIds = (existing?.members ?? Array.Empty<string>()).Concat(write?.members ?? Array.Empty<string>()).Distinct().ToArray();
                if (!CheckEditBudget(memberIds.Length, 1, addedRoutes: existing == null ? 1 : 0)) return;
                var expected = Versions(memberIds);
                var versions = ChainVersions(new[] { write?.id ?? existing.id });
                await reservationPending; await releasePending;
                token = (await connection.ReserveAsync(memberIds)).token;
                if (client != connection || connection.ConnectionEpoch != epoch) return;
                var tx = CreateTransaction("SetChains", Array.Empty<Part>(), dependencies: expected, token: token,
                    chainWrites: write == null ? null : new[] { write }, chainDeletes: write == null ? new[] { existing.id } : null,
                    chainDependencies: versions);
                var receipt = await connection.SubmitAsync(tx);
                if (client != connection || connection.ConnectionEpoch != epoch) return;
                if (receipt.accepted) lastSubmittedRevision = Math.Max(lastSubmittedRevision, receipt.revision);
                status = receipt.accepted ? label + (write != null ? " route saved" : " removed") + " at revision " + receipt.revision :
                    label + " edit rejected: " + receipt.error;
                InteractionFeedback.Notify(status);
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch) Report(error); }
            finally { if (token != null) { try { await connection.ReleaseAsync(token); } catch { } } if (client == connection && connection.ConnectionEpoch == epoch) submitting = false; }
        }
        public static void Delete(IEnumerable<GameObject> bodies) { if (IsShared && CanEdit) _ = instance.DeleteAsync(bodies.ToArray()); }
        private async Task DeleteAsync(GameObject[] bodies) {
            if (lease != null) return;
            submitting = true;
            var connection = client;
            long epoch = connection.ConnectionEpoch;
            string token = null;
            try {
                var ids = bodies.Select(b => b.GetComponent<SavedObject>().ConnectionId).Distinct().ToArray();
                var chains = DocumentModel.Chains(committed).Where(c => c.members.Intersect(ids).Any()).ToArray();
                var reservationIds = ids.Concat(chains.SelectMany(c => c.members)).Distinct().ToArray();
                if (!CheckEditBudget(reservationIds.Length, ids.Length + chains.Length)) return;
                var expected = Versions(reservationIds);
                await reservationPending;
                await releasePending;
                var reservation = await connection.ReserveAsync(reservationIds);
                token = reservation.token;
                if (client != connection || connection.ConnectionEpoch != epoch) { try { await connection.ReleaseAsync(reservation.token); } catch { } return; }
                var tx = CreateTransaction("DeleteParts", Array.Empty<Part>(), ids, expected, reservation.token,
                    chainDeletes: chains.Select(c => c.id).ToArray(), chainDependencies: ChainVersions(chains.Select(c => c.id)));
                var receipt = await connection.SubmitAsync(tx);
                if (client != connection || connection.ConnectionEpoch != epoch) return;
                if (receipt.accepted) lastSubmittedRevision = Math.Max(lastSubmittedRevision, receipt.revision);
                if (!receipt.accepted) { status = receipt.error; await Release(reservation.token); }
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch) Report(error); }
            finally { if (token != null) { try { await connection.ReleaseAsync(token); } catch { } } if (client == connection && connection.ConnectionEpoch == epoch) submitting = false; }
        }
        private async Task<bool> Submit(Transaction transaction, Action rejected = null) {
            submitting = true;
            if (DocumentModel.AssetWrites(transaction).Length > 0) status = "Uploading custom geometry…";
            var connection = client;
            long epoch = connection.ConnectionEpoch;
            try {
                if (!CheckEditBudget(reserved.Length, DocumentModel.MutationCount(transaction))) {
                    selectAfterApply.Remove(transaction.operationId);
                    rejected?.Invoke();
                    CollaborationScene.ApplyParts(committed.parts.Where(p => reserved.Contains(p.id)));
                    return false;
                }
                await reservationPending;
                await releasePending;
                var receipt = await connection.SubmitAsync(transaction);
                if (client != connection || connection.ConnectionEpoch != epoch) return false;
                if (receipt.accepted) lastSubmittedRevision = Math.Max(lastSubmittedRevision, receipt.revision);
                if (!receipt.accepted) {
                    selectAfterApply.Remove(transaction.operationId); status = "Edit rejected: " + receipt.error;
                    InteractionFeedback.Notify(status);
                    rejected?.Invoke(); CollaborationScene.ApplyParts(committed.parts.Where(p => reserved.Contains(p.id)));
                    return false;
                }
                // A receipt confirms authority acceptance; the Unity event queue must also
                // apply that revision before an import may claim visible local success.
                while (client == connection && connection.ConnectionEpoch == epoch && connection.IsConnected && committed.revision < receipt.revision)
                    await Task.Yield();
                if (client != connection || connection.ConnectionEpoch != epoch || !connection.IsConnected) {
                    InteractionFeedback.Notify("The room accepted the edit. Reconnect to synchronize its display before continuing.");
                    return false;
                }
                status = "Accepted revision " + receipt.revision;
                return true;
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch) {
                selectAfterApply.Remove(transaction.operationId);
                Report(error); rejected?.Invoke(); CollaborationScene.ApplyParts(committed.parts.Where(p => reserved.Contains(p.id)));
            } return false; }
            finally { if (client == connection && connection.ConnectionEpoch == epoch) { submitting = false; CancelPose(); } }
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
            submitting = false;
            CancelTools(); CancelPose();
            if (committed != null) CollaborationScene.ApplyParts(committed.parts);
            var previous = client; client = null;
            var owned = host; host = null; hostShutdown = LeaveAndStop(previous, owned);
            hostedRoom = null;
            committed = null; participants = Array.Empty<Participant>(); ClearGhosts(); status = "Continuing offline.";
            submitting = reserving = sendingPreview = renewingLease = false;
            lastSubmittedRevision = 0;
            selectAfterApply.Clear();
            StateSystems.StateSystem.instance?.ResetStates();
            RefreshRecoveredRooms();
        }
        private static async Task LeaveAndDispose(CollaborationClient previous) { try { await previous.LeaveAsync(); } catch { } finally { previous.Dispose(); } }
        private static async Task LeaveAndStop(CollaborationClient previous, CollaborationHost owned) {
            try { if (previous != null) await LeaveAndDispose(previous); }
            finally { if (owned != null) await owned.StopAsync(); }
        }
        private Task ReconnectAsync() => ReconnectCoreAsync(false);
        private async Task ReconnectCoreAsync(bool acceptReplacement) {
            if (AdmissionCooldownSeconds > 0) return;
            if (client == null || busy) return;
            var attempt = BeginConnection(acceptReplacement ? "Resynchronizing replaced room…" : "Reconnecting to room…");
            try {
                if (acceptReplacement) { CancelTools(); CancelPose(); ClearGhosts(); }
                await client.PrepareReconnectAsync();
                attempt.Token.ThrowIfCancellationRequested();
                await client.SetNetworkPolicyAsync(host != null ? NetworkPolicy.Direct : await SelectedNetworkAsync(attempt.Token), attempt.Token);
                if (host != null && !host.IsRunning) await host.RestartAsync(attempt.Token);
                await client.JoinAsync(displayName, cancellation: attempt.Token, progress: attempt, acceptReplacement: acceptReplacement);
                attempt.Token.ThrowIfCancellationRequested();
                submitting = reserving = sendingPreview = renewingLease = false;
                lock (queueGate) { main.Clear(); queuedBytes = 0; latestPresence = null; presenceClient = null; }
                committed = client.Snapshot;
                if (acceptReplacement) selectAfterApply.Clear();
                applying = true; try { CollaborationScene.Load(committed); } finally { applying = false; }
                status = (acceptReplacement ? "New room generation synchronized. Local file state remains independent. " : "Reconnected and synchronized. ") + string.Join(" ", client.RecoveryMessages);
            }
            catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) {
                await client.PrepareReconnectAsync();
                lock (queueGate) { main.Clear(); queuedBytes = 0; latestPresence = null; presenceClient = null; }
                status = "Reconnect canceled. The last accepted build and pending outcomes are kept. Retry reconnect to synchronize.";
            }
            catch (Exception error) { if (!closing) Report(error); }
            finally { EndConnection(attempt); }
        }

        public static bool CancelWaitingReservation() {
            if (!IsShared || !instance.reserving) return false;
            CancelPose(); return true;
        }

        private void Update() {
            // Reconnect publishes one complete snapshot on success. Until then,
            // retain the last accepted scene; cancellation discards queued frames.
            if (connectionAttempt != null) return;
            int count = 0;
            while (count++ < 128 && TryDequeue(out var action)) action();
            EventBatch presence; CollaborationClient source;
            lock (queueGate) { presence = latestPresence; source = presenceClient; latestPresence = null; }
            if (source == client && client?.IsConnected == true && presence != null) { participants = presence.participants; DrawGhosts(presence.previews); }
            if (lease == null || !client.IsConnected) return;
            // A confirmed edit may still be uploading when its original lease would expire.
            if (!renewingLease && Time.realtimeSinceStartup >= nextRenew) { nextRenew = Time.realtimeSinceStartup + 3; _ = Renew(); }
            if (!submitting && broadcastReservationPreview && !sendingPreview && Time.realtimeSinceStartup >= nextPreview) {
                nextPreview = Time.realtimeSinceStartup + CollaborationProtocol.PreviewIntervalMilliseconds(reserved.Length) / 1000f;
                _ = SendPreview();
            }
        }
        private async Task Renew() {
            renewingLease = true;
            var connection = client; var renewing = lease;
            long epoch = connection.ConnectionEpoch;
            try { await connection.ReserveAsync(reserved, renewing); }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch && lease == renewing && !submitting) { CancelSharedPreview(); Report(error); } }
            finally { if (client == connection && connection.ConnectionEpoch == epoch) renewingLease = false; }
        }
        private async Task SendPreview() {
            sendingPreview = true;
            var connection = client; var sending = lease;
            long epoch = connection.ConnectionEpoch;
            try {
                var ids = previewIds;
                bool compact = connection.SupportsLargeEdits;
                var preview = FindFirstObjectByType<Placement>()?.SharedMovePreview(compact, ids) ?? PartsManager.FindLoadedObjects()
                    .Where(body => ids.Contains(body.GetComponent<SavedObject>().ConnectionId))
                    .Select(body => CollaborationScene.CapturePreview(body, Simulation.SimulationTool.SavedPose(body), compact)).ToArray();
                await connection.PreviewAsync(new Preview { lease = sending, sequence = ++previewSequence, parts = preview });
            }
            catch (Exception error) { if (client == connection && connection.ConnectionEpoch == epoch && lease == sending && !submitting) { CancelSharedPreview(); Report(error); } }
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
        internal static void ReportCustomImportFailure(Exception error) { if (instance != null) instance.Report(error); }
        private void Report(Exception error) { AdmissionFeedback(error); status = error is CollaborationException known ? known.Code switch {
            "connection-attempt-timeout" => "The connection attempt reached its three-minute limit. Accepted content, known membership and pending outcomes are kept. Retry explicitly when ready.",
            "safe-retry-exhausted" => "Safe connection attempts exhausted. Check the network or server, then retry explicitly. Accepted content and pending outcomes are kept.",
            "room-full" => "The room is full (10 active participants or 1,000 retained memberships). Ask the owner to check usage, then retry explicitly.",
            "join-rate-limit" => "The room has reached its admission rate limit (60 requests per minute). Wait for the cooldown, then retry explicitly.",
            "short-polling-unavailable" => "This helper does not support short polling. Use an existing transport or update the helper with the matching release and catalog.",
            "asset-unused-quota" => "The room's unused upload budget is full. Reuse an uploaded model or start a new room.",
            "asset-room-quota" or "asset-client-quota" or "asset-materialization-quota" => "The room's custom geometry budget is full. Save your accepted build before starting a new room.",
            "asset-byte-limit" or "local-serialization-limit" => $"This model exceeds the shared room's {CustomGeometryByteLimit / (1024 * 1024)} MiB encoded geometry limit. " +
                (CustomGeometryByteLimit < GeometryTransfer.MaxAssetBytes ? "Update the collaboration server to share larger models." : "Reduce mesh detail and import again; OBJ/STL file size can differ."),
            "edit-byte-limit" => "The encoded edit exceeds this room's network size limit. Reduce the selection and try again.",
            "geometry-limits-client-required" => "Update this player to join a server that supports larger custom models.",
            "geometry-limits-unsupported" => "This server advertises unsupported model limits. Update the player and collaboration server together.",
            "appearance-client-required" => "Update this player to join a server that preserves imported model appearance.",
            "appearance-helper-required" => "Update the collaboration server to share models with imported colors or textures.",
            "appearance-version-unsupported" => "This model uses an unsupported appearance format.",
            "appearance-texture-dimensions" => "Diffuse PNG and JPEG images must be between 1 and 2048 pixels on each side.",
            "appearance-texture-budget" => "Diffuse images exceed the 16 MiB decoded RGB budget.",
            "asset-scene-quota" => "This build has too much instanced custom geometry. Reduce mesh detail or the number of custom-model copies.",
            "room-not-found" => "That room ID is not registered on this server. Check the invitation's room ID.",
            "room-unavailable" or "room-storage-failed-restart-required" => "This room is unavailable. Ask the server operator to inspect its status and recovery data.",
            "server-geometry-quota" => "The server's shared geometry budget is full. Ask the operator to check room usage.",
            "server-transfer-busy" => "The server is transferring models for other rooms. Retry when a slot is available.",
            "room-request-limit" or "room-event-limit" or "server-request-limit" or "server-event-limit" => "The room or server has reached its connection limit. Close extra sessions and retry.",
            "asset-transfer-busy" => "Another model is transferring. Retry when it finishes.",
            "missing-asset" or "asset-hash-mismatch" => "Custom geometry is missing or damaged. Reconnect to synchronize; ask the host to check recovery if it persists.",
            _ => FriendlyStatus(WireFailure.Code(error))
        } : FriendlyStatus(WireFailure.Code(error)); InteractionFeedback.Notify(status); Debug.LogWarning("Collaboration: " + status); }
        private void OnQuitting() { closing = true; CancelDirectory(); CancelRoomOperation(); ClearRoomKeys(); CancelDiagnosticExport(); CancelConnection(); connectionTest?.Cancel(); connectionAttempt?.Candidate?.Dispose(); host?.Dispose(); client?.Dispose(); }
        private void OnDestroy() { OnQuitting(); Application.quitting -= OnQuitting; ClearGhosts(); DisposeUI(); if (instance == this) instance = null; }
    }
}
