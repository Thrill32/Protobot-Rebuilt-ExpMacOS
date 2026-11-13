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
    public enum CollaborationTransport { Auto, WebSocket, LongPolling }

    public sealed class CollaborationClient : IEditSession, IDisposable {
        [Serializable] private sealed class RecoveryState {
            public int schema = 1;
            public string origin, room, catalogHash, generation, resumeToken;
            public long sequence;
            public string[] pendingIds = Array.Empty<string>();
        }
        private readonly HttpClient http;
        private readonly Uri endpoint;
        private readonly string room;
        private readonly CatalogManifest catalog;
        private readonly object gate = new object();
        private Document document;
        private string token;
        private string resumeToken;
        private readonly string recoveryPath;
        private FileStream recoveryOwnership;
        private bool loadedRecovery, disposed;
        private CancellationTokenSource lifetime;
        private Task pump;
        private TaskCompletionSource<bool> advanced = Signal();
        private long transient = -1;
        private long sequence;
        private long connectionEpoch;
        private readonly Dictionary<string, Transaction> pending = new Dictionary<string, Transaction>();
        public Participant Participant { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsDurable { get; private set; }
        public string ActiveTransport { get; private set; }
        public CollaborationTransport Transport { get; }
        public long ConnectionEpoch => Interlocked.Read(ref connectionEpoch);
        public string[] RecoveryMessages { get; private set; } = Array.Empty<string>();
        public event Action<AcceptedEdit> Accepted;
        public event Action<EventBatch> Presence;
        public event Action<string> Disconnected;
        public event Action<string> PendingResolved;
        public Document Snapshot { get { lock (gate) return document == null ? null : WireJson.Clone(document); } }
        public int PendingCount { get { lock (gate) return pending.Count; } }

        public CollaborationClient(string url, string roomId, CatalogManifest manifest, CollaborationTransport transport = CollaborationTransport.Auto, bool allowInsecureLan = false, string recoveryFile = null) {
            endpoint = new Uri(url.TrimEnd('/') + "/");
            DocumentModel.Require(string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) &&
                endpoint.AbsolutePath == "/" && DocumentModel.ValidId(roomId), "invalid-endpoint");
            DocumentModel.Require(endpoint.Scheme == "https" || endpoint.Scheme == "http" && (endpoint.IsLoopback || allowInsecureLan), "https-required-use-explicit-lan-http");
            room = roomId; catalog = WireJson.Clone(manifest); Transport = transport;
            recoveryPath = recoveryFile == null ? null : Path.GetFullPath(recoveryFile);
            // Default OS trust/proxy behavior is intentional. No certificate bypass callbacks.
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task<JoinResponse> JoinAsync(string name, string password = "", string ownerCredential = null, bool viewer = false, CancellationToken cancellation = default) {
            DocumentModel.Require(lifetime == null, "already-connected");
            Interlocked.Increment(ref connectionEpoch);
            var capabilities = await Request<Capabilities>(HttpMethod.Get, new Uri(endpoint, "capabilities"), null, cancellation).ConfigureAwait(false);
            DocumentModel.Require(capabilities.protocol == 1 && capabilities.version == CollaborationProtocol.Version, "server-version-mismatch");
            DocumentModel.Require(capabilities.room == room && capabilities.catalogHash == catalog.hash, "catalog-or-room-mismatch");
            IsDurable = capabilities.durable;
            LoadRecovery(capabilities, ownerCredential != null && endpoint.IsLoopback);
            var joined = await Post<JoinRequest, JoinResponse>("join", new JoinRequest {
                catalogHash = catalog.hash, name = name, password = password, ownerCredential = ownerCredential, viewer = viewer,
                resumeToken = resumeToken
            }, cancellation).ConfigureAwait(false);
            DocumentModel.Validate(joined.document, catalog);
            DocumentModel.Require(!string.IsNullOrEmpty(joined.resumeToken) && joined.resumeToken.Length < 256 && joined.sequence >= 0 &&
                joined.document.generation == capabilities.generation, "invalid-join-response");
            token = joined.token; resumeToken = joined.resumeToken; Participant = joined.participant;
            lock (gate) { document = WireJson.Clone(joined.document); sequence = Math.Max(sequence, joined.sequence); SaveRecovery(); }
            string[] unresolved;
            var recovery = new List<string>();
            lock (gate) unresolved = pending.Keys.ToArray();
            foreach (var operation in unresolved) {
                var receipt = await Get<Receipt>("receipt?operationId=" + Uri.EscapeDataString(operation), cancellation).ConfigureAwait(false);
                lock (gate) { pending.Remove(operation); SaveRecovery(); }
                string message = receipt.accepted ? "Recovered accepted edit at revision " + receipt.revision : receipt.error ?? "Edit was not accepted; retry explicitly.";
                recovery.Add(message); PendingResolved?.Invoke(message);
            }
            RecoveryMessages = recovery.ToArray();
            transient = -1; IsConnected = true;
            lifetime = new CancellationTokenSource();
            var connectionLifetime = lifetime;
            pump = Task.Run(() => Pump(connectionLifetime.Token));
            return joined;
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
                        DocumentModel.Require(saved != null && saved.schema == 1 && saved.room == room && saved.catalogHash == catalog.hash &&
                            saved.generation == capabilities.generation && (saved.origin == endpoint.GetLeftPart(UriPartial.Authority) || ownedLoopback), "saved-room-does-not-match");
                        DocumentModel.Require(!string.IsNullOrEmpty(saved.resumeToken) && saved.resumeToken.Length < 256 && saved.sequence >= 0 &&
                            saved.pendingIds != null && saved.pendingIds.Length <= 256 && saved.pendingIds.All(DocumentModel.ValidId) &&
                            saved.pendingIds.Distinct().Count() == saved.pendingIds.Length, "invalid-session-recovery-file");
                        resumeToken = saved.resumeToken; sequence = saved.sequence;
                        foreach (var id in saved.pendingIds) pending[id] = new Transaction { operationId = id };
                    }
                    loadedRecovery = true;
                }
                catch { recoveryOwnership.Dispose(); recoveryOwnership = null; throw; }
            }
        }
        private void SaveRecovery() {
            if (recoveryPath == null || !IsDurable) return;
            DocumentModel.Require(!disposed && resumeToken != null, "client-closed");
            RecoveryFiles.Write(recoveryPath, new RecoveryState { origin = endpoint.GetLeftPart(UriPartial.Authority), room = room,
                catalogHash = catalog.hash, generation = document.generation, resumeToken = resumeToken, sequence = sequence, pendingIds = pending.Keys.ToArray() });
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
            DocumentModel.Require(IsConnected, "disconnected");
            long epoch = ConnectionEpoch;
            try {
                lock (gate) {
                    DocumentModel.Require(pending.Count < 256 || pending.ContainsKey(transaction.operationId), "pending-operation-limit");
                    pending[transaction.operationId] = WireJson.Clone(transaction); SaveRecovery();
                }
                var receipt = await Post<Transaction, Receipt>("commit", transaction, cancellation).ConfigureAwait(false);
                if (receipt.accepted) await WaitForRevisionAsync(receipt.revision, cancellation).ConfigureAwait(false);
                lock (gate) { pending.Remove(transaction.operationId); SaveRecovery(); }
                return receipt;
            }
            catch { if (ConnectionEpoch == epoch) Freeze("Connection interrupted; pending outcome must be resolved by reconnecting."); throw; }
        }

        public Task<LeaseResponse> ReserveAsync(string[] ids, string lease = null, CancellationToken cancellation = default) {
            DocumentModel.Require(IsConnected, "disconnected");
            return Post<LeaseRequest, LeaseResponse>("lease", new LeaseRequest { ids = ids, token = lease }, cancellation);
        }
        public Task ReleaseAsync(string lease, CancellationToken cancellation = default) => Post<LeaseRequest, WireError>("release", new LeaseRequest { token = lease }, cancellation);
        public Task PreviewAsync(Preview preview, CancellationToken cancellation = default) => Post<Preview, WireError>("preview", preview, cancellation);

        public async Task WaitForRevisionAsync(long revision, CancellationToken cancellation = default) {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
                    resumeToken = null; pending.Clear(); sequence = 0;
                    if (recoveryPath != null && File.Exists(recoveryPath)) File.Delete(recoveryPath);
                }
            }
        }
        public async Task PrepareReconnectAsync() => await StopPump().ConfigureAwait(false);

        private async Task StopPump() {
            IsConnected = false;
            var old = lifetime; old?.Cancel();
            if (pump != null) { try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            old?.Dispose(); lifetime = null; pump = null;
        }

        private async Task Pump(CancellationToken cancellation) {
            try {
                if (Transport != CollaborationTransport.LongPolling) {
                    using (var socket = new ClientWebSocket()) {
                        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                        var uri = new UriBuilder(EventUri()) { Scheme = endpoint.Scheme == "https" ? "wss" : "ws" };
                        bool connected = false;
                        try {
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                                timeout.CancelAfter(5000); await socket.ConnectAsync(uri.Uri, timeout.Token).ConfigureAwait(false); connected = true;
                            }
                        }
                        catch (Exception error) when (Transport == CollaborationTransport.Auto && !cancellation.IsCancellationRequested &&
                            (error is WebSocketException || error is HttpRequestException || error is OperationCanceledException || error is PlatformNotSupportedException)) { }
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
                                        DocumentModel.Require(received.MessageType == WebSocketMessageType.Text && stream.Length + received.Count <= WireJson.MaxMessageBytes, "invalid-event-stream");
                                        stream.Write(bytes, 0, received.Count);
                                    } while (!received.EndOfMessage);
                                    Apply(WireJson.Decode<EventBatch>(stream.ToArray()));
                                }
                            }
                            return;
                        }
                    }
                }
                ActiveTransport = "long-polling";
                while (!cancellation.IsCancellationRequested) Apply(await Request<EventBatch>(HttpMethod.Get, EventUri(), null, cancellation).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { Freeze(error is CollaborationException known ? known.Code : "Connection lost. Reconnect to synchronize."); }
        }

        private void Apply(EventBatch batch) {
            DocumentModel.Require(batch != null && batch.edits != null && batch.previews != null && batch.participants != null, "invalid-events");
            foreach (var edit in batch.edits) {
                lock (gate) {
                    if (edit.revision <= document.revision) continue;
                    DocumentModel.Require(edit.revision == document.revision + 1, "revision-gap");
                    var next = DocumentModel.Reduce(document, edit.transaction, catalog);
                    DocumentModel.Require(next.revision == edit.revision && DocumentModel.Hash(next) == edit.hash, "document-hash-mismatch");
                    document = next; var old = advanced; advanced = Signal(); old.TrySetResult(true);
                }
                Accepted?.Invoke(edit);
            }
            transient = batch.transientVersion; Presence?.Invoke(batch);
        }

        private void Freeze(string reason) {
            bool notify = IsConnected; IsConnected = false; lifetime?.Cancel();
            lock (gate) advanced.TrySetResult(true);
            if (notify) Disconnected?.Invoke(reason);
        }
        public void Suspend(string reason) => Freeze(reason);
        private Uri EventUri() { lock (gate) return new Uri(endpoint, "rooms/" + room + "/events?after=" + document.revision + "&transient=" + transient); }
        private Task<T> Get<T>(string action, CancellationToken cancellation) => Request<T>(HttpMethod.Get, new Uri(endpoint, "rooms/" + room + "/" + action), null, cancellation);
        private Task<TOut> Post<TIn, TOut>(string action, TIn value, CancellationToken cancellation) => Request<TOut>(HttpMethod.Post, new Uri(endpoint, "rooms/" + room + "/" + action), WireJson.Encode(value), cancellation);
        private async Task<T> Request<T>(HttpMethod method, Uri uri, byte[] content, CancellationToken cancellation) {
            long epoch = ConnectionEpoch;
            var connectionCancellation = uri.AbsolutePath.EndsWith("/leave", StringComparison.Ordinal) ? CancellationToken.None : lifetime?.Token ?? CancellationToken.None;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, connectionCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var requestCancellation = timeout.Token;
            try {
            using (var request = new HttpRequestMessage(method, uri)) {
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (content != null) { request.Content = new ByteArrayContent(content); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation).ConfigureAwait(false)) {
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream()) {
                        var buffer = new byte[8192]; int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, requestCancellation).ConfigureAwait(false)) > 0) {
                            DocumentModel.Require(output.Length + read <= WireJson.MaxMessageBytes, "message-too-large"); output.Write(buffer, 0, read);
                        }
                        var bytes = output.ToArray();
                        if (!response.IsSuccessStatusCode) {
                            string reason;
                            try { reason = WireJson.Decode<WireError>(bytes)?.error; } catch { reason = null; }
                            throw new CollaborationException(reason ?? "http-" + (int)response.StatusCode);
                        }
                        return WireJson.Decode<T>(bytes);
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
