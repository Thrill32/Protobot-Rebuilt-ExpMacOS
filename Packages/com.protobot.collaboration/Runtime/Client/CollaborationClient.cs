using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    public enum CollaborationTransport { Auto, WebSocket, LongPolling, ShortPolling }

    public sealed partial class CollaborationClient : IEditSession, IDisposable {
        [Serializable] private sealed class RecoveryState {
            public int schema = 2;
            public string origin, room, catalogHash, generation, resumeToken;
            [System.Runtime.Serialization.OptionalField] public string roomIdentity, pendingGeneration;
            [System.Runtime.Serialization.OptionalField] public bool resynchronizationRequired;
            public long sequence;
            public string[] pendingIds = Array.Empty<string>();
        }
        private HttpClient http;
        private NetworkPolicy network;
        public string NetworkRoute => network.Route;
        private readonly Uri endpoint;
        private readonly string room;
        private readonly CatalogManifest catalog;
        private readonly object gate = new object();
        private Document document;
        private string token;
        private string resumeToken;
        private string membershipGeneration, membershipRoomIdentity, pendingGeneration;
        private readonly string recoveryPath;
        private FileStream recoveryOwnership;
        private bool loadedRecovery, disposed;
        private CancellationTokenSource lifetime;
        private Task pump;
        private TaskCompletionSource<bool> advanced = Signal();
        private long transient = -1;
        private long sequence;
        private long connectionEpoch;
        private int joining;
        private readonly Dictionary<string, Transaction> pending = new Dictionary<string, Transaction>();
        public Participant Participant { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsDurable { get; private set; }
        public bool SupportsParticipantAdministration { get; private set; }
        public bool IsMembershipRevoked { get; private set; }
        public bool RequiresResynchronization { get; private set; }
        public bool SupportsRoomReplacement { get; private set; }
        public string ActiveTransport { get; private set; }
        public CollaborationTransport Transport { get; }
        public long ConnectionEpoch => Interlocked.Read(ref connectionEpoch);
        public string[] RecoveryMessages { get; private set; } = Array.Empty<string>();
        public event Action<AcceptedEdit> Accepted;
        public event Action<EventBatch> Presence;
        public event Action<string> Disconnected;
        public event Action<string> PendingResolved;
        public Document Snapshot { get { lock (gate) return document == null ? null : GeometryTransfer.CloneDocument(document); } }
        public string AcceptedGeneration { get { lock (gate) return document?.generation; } }
        public int PendingCount { get { lock (gate) return pending.Count; } }

        public CollaborationClient(string url, string roomId, CatalogManifest manifest, CollaborationTransport transport = CollaborationTransport.Auto, bool allowInsecureLan = false, string recoveryFile = null, NetworkPolicy networkPolicy = null) {
            endpoint = new Uri(url.TrimEnd('/') + "/");
            DocumentModel.Require(string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) &&
                endpoint.AbsolutePath == "/" && DocumentModel.ValidId(roomId), "invalid-endpoint");
            DocumentModel.Require(endpoint.Scheme == "https" || endpoint.Scheme == "http" && (endpoint.IsLoopback || allowInsecureLan), "https-required-use-explicit-lan-http");
            room = roomId; catalog = WireJson.Clone(manifest); Transport = transport;
            recoveryPath = recoveryFile == null ? null : Path.GetFullPath(recoveryFile);
            network = networkPolicy ?? NetworkPolicy.SystemDefault; http = network.CreateHttpClient();
        }

        public const int ConnectionDeadlineMilliseconds = 180000;
        public async Task<JoinResponse> JoinAsync(string name, string password = "", string ownerCredential = null, bool viewer = false,
            CancellationToken cancellation = default, IProgress<ConnectionProgress> progress = null, bool acceptReplacement = false) {
            DocumentModel.Require(Interlocked.CompareExchange(ref joining, 1, 0) == 0, "already-connecting");
            var userCancellation = cancellation;
            using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            connectionDeadline.CancelAfter(ConnectionDeadlineMilliseconds); cancellation = connectionDeadline.Token;
            try {
                DocumentModel.Require(lifetime == null, "already-connected");
                DocumentModel.Require(!IsMembershipRevoked, "membership-revoked");
                long epoch = Interlocked.Increment(ref connectionEpoch);
                void Check() { cancellation.ThrowIfCancellationRequested(); lock (gate) DocumentModel.Require(!disposed && ConnectionEpoch == epoch, "client-closed"); }
                Check(); lock (gate) { diagnosticClock.Restart(); diagnosticCode = null; diagnosticStage = null; diagnosticTransfer = null; diagnosticEvents.Clear(); }
                DiagnosticProgress(new ConnectionProgress(ConnectionStage.CheckingServer), progress);
                var server = await ConnectionRetry.ReadAsync((_, token) => Request<Capabilities>(HttpMethod.Get, new Uri(endpoint, "capabilities"), null, token), cancellation, new DiagnosticProgressRelay(value => DiagnosticProgress(value, progress))).ConfigureAwait(false);
                DocumentModel.Require(server.protocol == 1 && server.version == CollaborationProtocol.Version, "server-version-mismatch");
                DocumentModel.Require(server.multiRoom || server.room == room, "catalog-or-room-mismatch");
                Check(); DiagnosticProgress(new ConnectionProgress(ConnectionStage.CheckingRoom), progress);
                var capabilities = await ConnectionRetry.ReadAsync((_, token) => Request<Capabilities>(HttpMethod.Get, new Uri(endpoint, "rooms/" + room + "/capabilities"), null, token), cancellation, new DiagnosticProgressRelay(value => DiagnosticProgress(value, progress))).ConfigureAwait(false);
                DocumentModel.Require(capabilities.protocol == 1 && capabilities.version == CollaborationProtocol.Version, "server-version-mismatch");
                DocumentModel.Require(capabilities.room == room && capabilities.catalogHash == catalog.hash, "catalog-or-room-mismatch");
                DocumentModel.Require(Transport != CollaborationTransport.ShortPolling || capabilities.shortPollingVersion == CollaborationProtocol.ShortPollingVersion, "short-polling-unavailable");
                IsDurable = capabilities.durable;
                SupportsParticipantAdministration = capabilities.participantAdministration;
                SupportsDiagnostics = capabilities.diagnosticsVersion == DiagnosticExport.Version;
                SupportsRoomReplacement = capabilities.roomReplacementVersion == RoomReplacement.Version;
                LoadRecovery(capabilities, ownerCredential != null && endpoint.IsLoopback);
                bool replaced; string previousGeneration;
                lock (gate) {
                    string identity = capabilities.roomIdentity ?? capabilities.generation;
                    DocumentModel.Require(membershipRoomIdentity == null || membershipRoomIdentity == identity, "saved-room-does-not-match");
                    previousGeneration = membershipGeneration; replaced = membershipGeneration != null && membershipGeneration != capabilities.generation;
                    if (replaced) RequiresResynchronization = true;
                    bool mustAccept = replaced || RequiresResynchronization && (document == null || document.generation != capabilities.generation);
                    DocumentModel.Require(!mustAccept || acceptReplacement && SupportsRoomReplacement, "saved-room-does-not-match");
                }
                Check(); DiagnosticProgress(new ConnectionProgress(ConnectionStage.Authenticating), progress); Check();
                // Admission can create durable membership. Once sent, settle its
                // bounded reply and save the credential before honoring cancellation.
                var joined = await Post<JoinRequest, JoinResponse>("join", new JoinRequest {
                    room = room, catalogHash = catalog.hash, name = name, password = password, ownerCredential = ownerCredential, viewer = viewer,
                    resumeToken = resumeToken, expectedGeneration = capabilities.generation, roomIdentity = capabilities.roomIdentity,
                    resynchronizeFromGeneration = replaced ? previousGeneration : null
                }, CancellationToken.None, 10000).ConfigureAwait(false);
                DocumentModel.Validate(joined.document, catalog);
                DocumentModel.Require(capabilities.geometryTransfer && joined.document.schema == 2, "geometry-transfer-required");
                DocumentModel.Require(!string.IsNullOrEmpty(joined.resumeToken) && joined.resumeToken.Length < 256 && joined.sequence >= 0 &&
                    joined.room == room && joined.document.generation == capabilities.generation, "invalid-join-response");
                lock (gate) {
                    DocumentModel.Require(!disposed && ConnectionEpoch == epoch, "client-closed");
                    token = joined.token; resumeToken = joined.resumeToken; membershipGeneration = joined.document.generation; Participant = joined.participant;
                    membershipRoomIdentity = capabilities.roomIdentity ?? capabilities.generation;
                    sequence = Math.Max(sequence, joined.sequence); SaveRecovery(joined.document.generation);
                }
                Check();
                // Stage geometry separately: two different, individually valid room
                // generations must not collide with one cumulative client cache quota.
                var candidateGeometry = CandidateGeometry(joined.document);
                await HydrateAsync(joined.document, cancellation, new DiagnosticProgressRelay(value => DiagnosticProgress(value, progress)), candidateGeometry).ConfigureAwait(false);
                string[] unresolved;
                var recovery = new List<string>();
                string unresolvedGeneration;
                lock (gate) { unresolved = pending.Keys.ToArray(); unresolvedGeneration = pendingGeneration; }
                for (int i = 0; i < unresolved.Length; i++) {
                    Check(); DiagnosticProgress(new ConnectionProgress(ConnectionStage.RecoveringEdits, i, unresolved.Length), progress);
                    string operation = unresolved[i];
                    var receipt = await Get<Receipt>("receipt?operationId=" + Uri.EscapeDataString(operation) +
                        (SupportsRoomReplacement && unresolvedGeneration != null ? "&generation=" + Uri.EscapeDataString(unresolvedGeneration) : ""), cancellation).ConfigureAwait(false);
                    string message = receipt.accepted ? "Recovered accepted edit at revision " + receipt.revision : WireFailure.Code(receipt.error) ?? "Edit was not accepted; retry explicitly.";
                    if (receipt.accepted && unresolvedGeneration != joined.document.generation) message += ". Its content was superseded by room replacement.";
                    recovery.Add(message);
                }
                Check();
                var candidateDocument = GeometryTransfer.CloneDocument(joined.document);
                string candidateHash = DocumentModel.Hash(GeometryTransfer.References(joined.document));
                lock (gate) {
                    Check();
                    var resolved = unresolved.ToDictionary(id => id, id => pending[id]);
                    foreach (string id in unresolved) pending.Remove(id);
                    string previousPendingGeneration = pendingGeneration; if (pending.Count == 0) pendingGeneration = null;
                    bool previousResyncRequired = RequiresResynchronization; RequiresResynchronization = false;
                    try { SaveRecovery(joined.document.generation); }
                    catch { foreach (var pair in resolved) pending[pair.Key] = pair.Value; pendingGeneration = previousPendingGeneration; RequiresResynchronization = previousResyncRequired; throw; }
                    geometryCache = candidateGeometry; document = candidateDocument; diagnosticHash = candidateHash; RequiresResynchronization = false;
                    RecoveryMessages = recovery.ToArray(); transient = -1; IsConnected = true;
                    lifetime = new CancellationTokenSource(); var connectionLifetime = lifetime;
                    pump = Task.Run(() => Pump(connectionLifetime.Token));
                }
                foreach (string message in recovery) PendingResolved?.Invoke(message);
                DiagnosticProgress(new ConnectionProgress(ConnectionStage.Ready), progress);
                return joined;
            }
            catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested && connectionDeadline.IsCancellationRequested) {
                DiagnosticEvent("connection-attempt-timeout"); throw new CollaborationException("connection-attempt-timeout");
            }
            catch (Exception error) { DiagnosticEvent(DiagnosticExport.Code(error)); throw; }
            finally { Interlocked.Exchange(ref joining, 0); }
        }

        private void LoadRecovery(Capabilities capabilities, bool ownedLoopback) {
            lock (gate) {
                DocumentModel.Require(!disposed, "client-closed");
                if (recoveryPath == null || loadedRecovery) return;
                if (!IsDurable) { DocumentModel.Require(!File.Exists(recoveryPath), "saved-room-is-not-durable"); return; }
                RecoveryFiles.CreateDirectory(Path.GetDirectoryName(recoveryPath));
                recoveryOwnership = new FileStream(recoveryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try {
                    RecoveryFiles.PrivateFile(recoveryPath + ".lock");
                    if (File.Exists(recoveryPath)) {
                        var saved = RecoveryFiles.Read<RecoveryState>(recoveryPath);
                        DocumentModel.Require(saved != null && (saved.schema == 1 || saved.schema == 2) && saved.room == room && saved.catalogHash == catalog.hash &&
                            (saved.roomIdentity ?? saved.generation) == (capabilities.roomIdentity ?? capabilities.generation) &&
                            (saved.origin == endpoint.GetLeftPart(UriPartial.Authority) || ownedLoopback), "saved-room-does-not-match");
                        DocumentModel.Require(!string.IsNullOrEmpty(saved.resumeToken) && saved.resumeToken.Length < 256 && saved.sequence >= 0 &&
                            DocumentModel.ValidId(saved.generation) && (saved.schema == 1 || DocumentModel.ValidId(saved.roomIdentity)) &&
                            saved.pendingIds != null && saved.pendingIds.Length <= 256 && saved.pendingIds.All(DocumentModel.ValidId) &&
                            saved.pendingIds.Distinct().Count() == saved.pendingIds.Length, "invalid-session-recovery-file");
                        resumeToken = saved.resumeToken; membershipGeneration = saved.generation; sequence = saved.sequence;
                        membershipRoomIdentity = saved.roomIdentity ?? saved.generation;
                        RequiresResynchronization = saved.schema == 2 && saved.resynchronizationRequired;
                        pendingGeneration = saved.pendingIds.Length == 0 ? null : saved.schema == 1 ? saved.generation : saved.pendingGeneration;
                        DocumentModel.Require(saved.pendingIds.Length == 0 || DocumentModel.ValidId(pendingGeneration), "invalid-session-recovery-file");
                        foreach (var id in saved.pendingIds) pending[id] = new Transaction { operationId = id, generation = pendingGeneration };
                    }
                    loadedRecovery = true;
                }
                catch { recoveryOwnership.Dispose(); recoveryOwnership = null; throw; }
            }
        }
        private void SaveRecovery(string generation = null) {
            if (recoveryPath == null || !IsDurable) return;
            DocumentModel.Require(!disposed && resumeToken != null, "client-closed");
            RecoveryFiles.Write(recoveryPath, new RecoveryState { origin = endpoint.GetLeftPart(UriPartial.Authority), room = room,
                catalogHash = catalog.hash, generation = generation ?? document.generation, roomIdentity = membershipRoomIdentity,
                pendingGeneration = pendingGeneration, resynchronizationRequired = RequiresResynchronization,
                resumeToken = resumeToken, sequence = sequence, pendingIds = pending.Keys.ToArray() });
        }
        public static void ForgetRecovery(string path) {
            if (!File.Exists(path)) return;
            using (var ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) File.Delete(path);
        }

        public Transaction CreateTransaction(string kind, Part[] writes, string[] deletes = null, VersionRef[] dependencies = null, string lease = null,
            ChainRecord[] chainWrites = null, string[] chainDeletes = null, VersionRef[] chainDependencies = null, GeometryAsset[] assetWrites = null) {
            lock (gate) {
                DocumentModel.Require(IsConnected && document != null, "disconnected");
                var ids = (writes ?? Array.Empty<Part>()).Select(p => p.id).Concat(deletes ?? Array.Empty<string>()).ToArray();
                var expected = (dependencies ?? Array.Empty<VersionRef>()).ToDictionary(v => v.id);
                foreach (var id in ids) if (!expected.ContainsKey(id)) expected.Add(id, new VersionRef { id = id, version = document.parts.FirstOrDefault(p => p.id == id)?.version ??
                    document.tombstones.FirstOrDefault(p => p.id == id)?.version ?? 0 });
                var chainIds = (chainWrites ?? Array.Empty<ChainRecord>()).Select(c => c.id).Concat(chainDeletes ?? Array.Empty<string>()).ToArray();
                var expectedChains = (chainDependencies ?? Array.Empty<VersionRef>()).ToDictionary(v => v.id);
                foreach (var id in chainIds) if (!expectedChains.ContainsKey(id)) expectedChains.Add(id, new VersionRef { id = id,
                    version = DocumentModel.Chains(document).FirstOrDefault(c => c.id == id)?.version ?? DocumentModel.ChainTombstones(document).FirstOrDefault(c => c.id == id)?.version ?? 0 });
                return new Transaction { generation = document.generation, kind = kind, sequence = ++sequence,
                    writes = writes ?? Array.Empty<Part>(), deletes = deletes ?? Array.Empty<string>(), expected = expected.Values.ToArray(), lease = lease,
                    chainWrites = chainWrites ?? Array.Empty<ChainRecord>(), chainDeletes = chainDeletes ?? Array.Empty<string>(), expectedChains = expectedChains.Values.ToArray(),
                    assetWrites = assetWrites ?? Array.Empty<GeometryAsset>() };
            }
        }

        public async Task<Receipt> SubmitAsync(Transaction transaction, CancellationToken cancellation = default) {
            DocumentModel.Require(IsConnected, IsMembershipRevoked ? "membership-revoked" : "disconnected");
            long epoch = ConnectionEpoch;
            transaction = await PrepareAssetsAsync(transaction, cancellation).ConfigureAwait(false);
            try {
                lock (gate) {
                    DocumentModel.Require(ConnectionEpoch == epoch && IsConnected && !disposed, "client-connection-changed");
                    DocumentModel.Require(transaction.generation == document.generation, "generation-mismatch");
                    DocumentModel.Require(pending.Count < 256 || pending.ContainsKey(transaction.operationId), "pending-operation-limit");
                    DocumentModel.Require(pending.Count == 0 || pendingGeneration == document.generation, "unresolved-previous-generation-operations");
                    if (pending.Count == 0) pendingGeneration = document.generation;
                    pending[transaction.operationId] = WireJson.Clone(transaction); SaveRecovery();
                }
                var receipt = await Post<Transaction, Receipt>("commit", transaction, cancellation).ConfigureAwait(false);
                if (receipt.accepted) await WaitForRevisionAsync(receipt.revision, cancellation).ConfigureAwait(false);
                lock (gate) { pending.Remove(transaction.operationId); if (pending.Count == 0) pendingGeneration = null; SaveRecovery(); }
                return receipt;
            }
            catch { if (ConnectionEpoch == epoch) Freeze("Connection interrupted; pending outcome must be resolved by reconnecting."); throw; }
        }

        public Task<LeaseResponse> ReserveAsync(string[] ids, string lease = null, CancellationToken cancellation = default) {
            DocumentModel.Require(IsConnected, IsMembershipRevoked ? "membership-revoked" : "disconnected");
            return Post<LeaseRequest, LeaseResponse>("lease", new LeaseRequest { ids = ids, token = lease }, cancellation);
        }
        public Task ReleaseAsync(string lease, CancellationToken cancellation = default) => Post<LeaseRequest, WireError>("release", new LeaseRequest { token = lease }, cancellation);
        public Task PreviewAsync(Preview preview, CancellationToken cancellation = default) => Post<Preview, WireError>("preview", preview, cancellation);

        public async Task WaitForRevisionAsync(long revision, CancellationToken cancellation = default) {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                while (true) {
                    Task wait;
                    lock (gate) {
                        if (document.revision >= revision) return;
                        DocumentModel.Require(IsConnected, "disconnected"); wait = advanced.Task;
                    }
                    var canceled = Task.Delay(Timeout.Infinite, timeout.Token);
                    await Task.WhenAny(wait, canceled).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                }
            }
        }

        public async Task LeaveAsync() {
            bool left = false;
            try { if (token != null) { await Post<WireError, WireError>("leave", new WireError(), CancellationToken.None).ConfigureAwait(false); left = true; } }
            finally {
                token = null; await StopPump().ConfigureAwait(false);
                if (left) lock (gate) {
                    resumeToken = membershipGeneration = membershipRoomIdentity = pendingGeneration = null; pending.Clear(); sequence = 0; RequiresResynchronization = false;
                    if (recoveryPath != null && File.Exists(recoveryPath)) File.Delete(recoveryPath);
                }
            }
        }
        public async Task PrepareReconnectAsync() => await StopPump().ConfigureAwait(false);

        // Route is deliberately absent from the membership scope. Keep this client,
        // accepted document, pending IDs and credentials; only an explicit Join resolves them.
        public async Task SetNetworkPolicyAsync(NetworkPolicy policy, CancellationToken cancellation = default) {
            DocumentModel.Require(policy != null, "proxy-route-unsupported");
            DocumentModel.Require(Interlocked.CompareExchange(ref joining, 1, 0) == 0, "already-connecting");
            try {
                await StopPump().ConfigureAwait(false); cancellation.ThrowIfCancellationRequested();
                var replacement = await Task.Run(() => policy.CreateHttpClient(), cancellation).ConfigureAwait(false);
                lock (gate) {
                    if (disposed || cancellation.IsCancellationRequested) { replacement.Dispose(); cancellation.ThrowIfCancellationRequested(); throw new CollaborationException("client-closed"); }
                    Interlocked.Increment(ref connectionEpoch); var previous = http; http = replacement; network = policy; previous.Dispose();
                }
            }
            finally { Interlocked.Exchange(ref joining, 0); }
        }

        public Task<MembershipList> MembershipsAsync(CancellationToken cancellation = default) {
            DocumentModel.Require(SupportsParticipantAdministration, "participant-administration-unavailable");
            return Get<MembershipList>("participants", cancellation);
        }
        public Task<RemovalResult> RemoveParticipantAsync(string participantId, CancellationToken cancellation = default) {
            DocumentModel.Require(SupportsParticipantAdministration, "participant-administration-unavailable");
            return Post<RemoveParticipant, RemovalResult>("participants/remove", new RemoveParticipant { participantId = participantId }, cancellation);
        }

        private async Task StopPump() {
            IsConnected = false;
            var old = lifetime; old?.Cancel();
            if (pump != null) { try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            old?.Dispose(); lifetime = null; pump = null;
        }

        private async Task Pump(CancellationToken cancellation) {
            try {
                if (Transport == CollaborationTransport.Auto || Transport == CollaborationTransport.WebSocket) {
                    WebSocket socket = null;
                    try {
                        bool connected = false;
                        try {
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                                timeout.CancelAfter(5000); socket = await OpenEventSocketAsync(timeout.Token).ConfigureAwait(false); connected = true;
                            }
                        }
                        catch (Exception error) when (Transport == CollaborationTransport.Auto && !cancellation.IsCancellationRequested &&
                            (error is WebSocketException || error is HttpRequestException || error is OperationCanceledException || error is PlatformNotSupportedException || error is CollaborationException known && known.Code == "websocket-unavailable")) { }
                        if (connected) {
                            ActiveTransport = "websocket";
                            var bytes = new byte[8192];
                            while (!cancellation.IsCancellationRequested) {
                                using (var stream = new MemoryStream()) {
                                    WebSocketReceiveResult received;
                                    do {
                                        using (var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                                            heartbeat.CancelAfter(TimeSpan.FromSeconds(45));
                                            received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), heartbeat.Token).ConfigureAwait(false);
                                        }
                                        if (received.MessageType == WebSocketMessageType.Close && received.CloseStatus == WebSocketCloseStatus.EndpointUnavailable && received.CloseStatusDescription == "server-stopping")
                                            throw new CollaborationException("server-stopping");
                                        if (received.MessageType == WebSocketMessageType.Close && received.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                                            throw new CollaborationException(received.CloseStatusDescription == "membership-revoked" ? "membership-revoked" :
                                                received.CloseStatusDescription == "room-replaced" ? "room-replaced" : "event-session-ended");
                                        DocumentModel.Require(received.MessageType == WebSocketMessageType.Text && stream.Length + received.Count <= WireJson.MaxMessageBytes, "invalid-event-stream");
                                        stream.Write(bytes, 0, received.Count);
                                    } while (!received.EndOfMessage);
                                    await ApplyAsync(WireJson.Decode<EventBatch>(stream.ToArray()), cancellation).ConfigureAwait(false);
                                }
                            }
                            return;
                        }
                    }
                    finally { socket?.Dispose(); }
                }
                await PumpRequestsAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { Freeze(WireFailure.Code(error)); }
        }

        private async Task ApplyAsync(EventBatch batch, CancellationToken cancellation) {
            DocumentModel.Require(batch != null && batch.edits != null && batch.previews != null && batch.participants != null, "invalid-events");
            foreach (var edit in batch.edits) {
                foreach (var reference in DocumentModel.AssetWrites(edit.transaction))
                    await ObtainAssetAsync(reference, cancellation).ConfigureAwait(false);
                lock (gate) {
                    if (edit.revision <= document.revision) continue;
                    DocumentModel.Require(edit.revision == document.revision + 1, "revision-gap");
                    var next = DocumentModel.Reduce(document, edit.transaction, catalog);
                    DocumentModel.Require(next.revision == edit.revision && DocumentModel.Hash(next) == edit.hash, "document-hash-mismatch");
                    HydrateCached(next); document = next; diagnosticHash = edit.hash; var old = advanced; advanced = Signal(); old.TrySetResult(true);
                }
                Accepted?.Invoke(edit);
            }
            transient = batch.transientVersion; Presence?.Invoke(batch);
        }

        private void Freeze(string reason) {
            DiagnosticEvent(DiagnosticExport.SafeCode(reason));
            if (reason == "membership-revoked") IsMembershipRevoked = true;
            if (reason == "room-replaced") RequiresResynchronization = true;
            if (IsMembershipRevoked) reason = "membership-revoked";
            bool notify = IsConnected; IsConnected = false; lifetime?.Cancel();
            lock (gate) advanced.TrySetResult(true);
            if (notify) Disconnected?.Invoke(reason);
        }
        public void Suspend(string reason) => Freeze(reason);
        private Uri EventUri() { lock (gate) return new Uri(endpoint, "rooms/" + room + "/events?after=" + document.revision + "&transient=" + transient + (Transport == CollaborationTransport.ShortPolling ? "&wait=0" : "")); }
        private Task<T> Get<T>(string action, CancellationToken cancellation) => Request<T>(HttpMethod.Get, new Uri(endpoint, "rooms/" + room + "/" + action), null, cancellation);
        private Task<TOut> Post<TIn, TOut>(string action, TIn value, CancellationToken cancellation, int timeoutMilliseconds = 30000) => Request<TOut>(HttpMethod.Post, new Uri(endpoint, "rooms/" + room + "/" + action), WireJson.Encode(value), cancellation, timeoutMilliseconds);
        private async Task<T> Request<T>(HttpMethod method, Uri uri, byte[] content, CancellationToken cancellation, int timeoutMilliseconds = 30000) {
            long epoch = ConnectionEpoch;
            var connectionCancellation = uri.AbsolutePath.EndsWith("/leave", StringComparison.Ordinal) ? CancellationToken.None : lifetime?.Token ?? CancellationToken.None;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, connectionCancellation);
            timeout.CancelAfter(timeoutMilliseconds);
            var requestCancellation = timeout.Token;
            try {
            using (var request = new HttpRequestMessage(method, uri)) {
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (content != null) { request.Content = new ByteArrayContent(content); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation).ConfigureAwait(false)) {
                    NetworkPolicy.CheckProxyResponse(response);
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream()) {
                        var buffer = new byte[8192]; int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, requestCancellation).ConfigureAwait(false)) > 0) {
                            DocumentModel.Require(output.Length + read <= WireJson.MaxMessageBytes, "message-too-large"); output.Write(buffer, 0, read);
                        }
                        var bytes = output.ToArray();
                        if (!response.IsSuccessStatusCode) {
                            var rejection = ConnectionRetry.Rejection(response, bytes); string reason = rejection.Code;
                            if ((reason == "membership-revoked" || reason == "server-stopping" || reason == "room-replaced") && ConnectionEpoch == epoch) Freeze(reason);
                            throw rejection;
                        }
                        var value = WireJson.Decode<T>(bytes);
                        if (value is Receipt receipt) receipt.error = WireFailure.Code(receipt.error);
                        return value;
                    }
                }
            }
            }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !cancellation.IsCancellationRequested) {
                if (ConnectionEpoch == epoch) Freeze("Connection interrupted; reconnect to synchronize.");
                throw;
            }
        }
        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() {
            lock (gate) { if (disposed) return; disposed = true; Interlocked.Increment(ref connectionEpoch); }
            Freeze("Connection closed."); http.Dispose(); recoveryOwnership?.Dispose();
        }
    }
}
