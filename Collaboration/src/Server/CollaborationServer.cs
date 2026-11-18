using System;
using System.IO;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace Protobot.Collaboration.Server {
    public sealed partial class CollaborationServer : IAsyncDisposable {
        private readonly WebApplication app;
        private readonly RoomStore storage;
        private readonly RoomRegistry registry;
        private readonly ProcessBudget budget;
        private readonly ConcurrentDictionary<string, RouteLimits> roomLimits = new ConcurrentDictionary<string, RouteLimits>(StringComparer.Ordinal);
        private sealed class RouteLimits {
            public readonly object joinGate = new object();
            public long window = Environment.TickCount64;
            public int attempts;
            public readonly SemaphoreSlim transfers = new SemaphoreSlim(1, 1), events = new SemaphoreSlim(10, 10), requests = new SemaphoreSlim(ProcessBudget.MaxRoomRequests, ProcessBudget.MaxRoomRequests);
        }
        private long connections;
        private readonly SemaphoreSlim requests = new SemaphoreSlim(ProcessBudget.MaxRequests, ProcessBudget.MaxRequests);
        private readonly SemaphoreSlim eventRequests = new SemaphoreSlim(ProcessBudget.MaxEvents, ProcessBudget.MaxEvents);
        private readonly SemaphoreSlim standaloneReplacement = new SemaphoreSlim(1, 1);
        public RoomRegistry Registry => registry;
        public bool MultiRoom => registry != null;
        private Room Resolve(HttpContext context) {
            string name = context.Request.RouteValues["room"]?.ToString();
            DocumentModel.Require(DocumentModel.ValidId(name), "room-not-found");
            if (registry != null) return registry.Resolve(name);
            DocumentModel.Require(Room.Name == name, "room-not-found"); return Room;
        }
        public Room Room { get; }
        public string Url { get; private set; }

        public CollaborationServer(Bootstrap seed, string listen = "http://127.0.0.1:0", bool allowInsecureLan = false, string dataDirectory = null, string multiRoomConfig = null, TlsConfiguration tls = null) {
            DocumentModel.Require(seed == null || DocumentModel.ValidId(seed.room), "invalid-room");
            var endpoint = new Uri(listen);
            DocumentModel.Require(string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) && endpoint.AbsolutePath == "/", "invalid-listener");
            DocumentModel.Require(endpoint.Scheme == "https" || endpoint.Scheme == "http" && (endpoint.IsLoopback || allowInsecureLan), "https-required-use-explicit-lan-http");
            DocumentModel.Require(tls == null || endpoint.Scheme == "https", "tls-requires-https-listener");
            DocumentModel.Require(tls == null || tls.Certificate != null, "tls-certificate-private-key-required");
            IPAddress address = null;
            DocumentModel.Require(tls == null || IPAddress.TryParse(endpoint.DnsSafeHost, out address), "tls-listener-requires-ip");
            DocumentModel.Require(multiRoomConfig == null || seed == null && dataDirectory == null, "use-single-or-multi-room");
            if (multiRoomConfig != null) { registry = new RoomRegistry(multiRoomConfig); budget = registry.Budget; }
            else {
                budget = new ProcessBudget();
                storage = dataDirectory == null ? null : new RoomStore(dataDirectory);
                try { Room = new Room(seed, storage: storage); Room.AttachBudget(budget); }
                catch { storage?.Dispose(); throw; }
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            // Application stdout is reserved for the private readiness protocol. Do not log bodies/credentials.
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
            builder.Services.AddSingleton<IHostedService>(_ => new ShutdownNotice(this));
            builder.WebHost.UseUrls(listen);
            builder.WebHost.ConfigureKestrel(options => {
                options.Limits.MaxRequestBodySize = WireJson.MaxMessageBytes;
                options.Limits.MaxConcurrentConnections = 128;
                options.ConfigureEndpointDefaults(listener => listener.Use(next => async connection => {
                    Interlocked.Increment(ref connections);
                    try { await next(connection); } finally { Interlocked.Decrement(ref connections); }
                }));
                if (tls != null) {
                    // Replace ambient Kestrel endpoints/default certificates. An explicit
                    // TLS configuration creates exactly one listener and no HTTP fallback.
                    options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
                    options.Listen(address, endpoint.Port, listener => listener.UseHttps(https => {
                        https.ServerCertificate = tls.Certificate; https.ServerCertificateChain = tls.Chain;
                    }));
                }
                options.Limits.MaxConcurrentUpgradedConnections = ProcessBudget.MaxEvents;
            });
            app = builder.Build();
            app.Use(async (context, next) => {
                if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
                context.Response.Headers.CacheControl = "no-store";
                if (Volatile.Read(ref stopping) != 0) { await Reject(context, "server-stopping", 503); return; }
                bool admitted = await requests.WaitAsync(0, context.RequestAborted);
                if (!admitted) { await Reject(context, "server-request-limit", 503); return; }
                SemaphoreSlim roomRequests = null; bool roomAdmitted = false;
                try {
                    if (context.Request.RouteValues.ContainsKey("room")) {
                        var selected = Resolve(context);
                        roomRequests = roomLimits.GetOrAdd(selected.Name, _ => new RouteLimits()).requests;
                        DocumentModel.Require(await roomRequests.WaitAsync(0, context.RequestAborted), "room-request-limit"); roomAdmitted = true;
                    }
                    await next(context);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (CollaborationException error) {
                    if (context.Response.HasStarted) context.Abort();
                    if (!context.Response.HasStarted) {
                        context.Response.StatusCode = error.Code == "owner-required" || error.Code == "server-admin-required" || error.Code == "room-creation-key-required" ? 403 :
                            error.Code == "room-not-found" ? 404 : error.Code == "server-stopping" || error.Code == "room-unavailable" || error.Code == "room-storage-failed-restart-required" ? 503 :
                            error.Code == "invalid-session" || error.Code == "membership-revoked" ? 401 : 409;
                        await Reject(context, error.Code, context.Response.StatusCode, error.RetryAfterMilliseconds);
                    }
                }
                catch (Exception error) when (error is System.Runtime.Serialization.SerializationException || error is ArgumentException || error is BadHttpRequestException) {
                    if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await Write(context, new WireError { error = "invalid-request" }); }
                }
                catch (IOException) { if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await Write(context, new WireError { error = "server-storage-write-failed" }); } else context.Abort(); }
                catch (OperationCanceledException) { if (!context.Response.HasStarted) { context.Response.StatusCode = 408; await Write(context, new WireError { error = "request-timeout" }); } else context.Abort(); }
                finally { if (roomAdmitted) roomRequests.Release(); requests.Release(); }
            });
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
            var rootCapabilities = registry == null ? Room.Capabilities : new Capabilities { durable = true, multiRoom = true, registryVersion = RoomRegistry.Version,
                geometryLimitsVersion = GeometryTransfer.LimitsVersion, geometryAssetBytes = GeometryTransfer.MaxAssetBytes,
                roomCreationVersion = ServerRooms.CreationVersion, roomCreationPolicy = registry.CreationPolicy, serverId = registry.ServerId,
                editorRoomReplacementVersion = RoomReplacement.EditorPolicyVersion,
                diagnosticsVersion = DiagnosticExport.Version, shortPollingVersion = CollaborationProtocol.ShortPollingVersion, transports = new[] { "websocket", "long-polling", "short-polling" } };
            app.MapGet("/capabilities", context => Write(context, registry == null ? Room.Capabilities : rootCapabilities));
            app.MapGet("/health", context => { context.Response.StatusCode = registry != null || Room.IsHealthy ? 200 : 503; return Write(context, registry == null ? Room.Capabilities : rootCapabilities); });
            const string path = "/rooms/{room}";
            app.MapGet(path + "/capabilities", context => Write(context, Resolve(context).Capabilities));
            app.MapGet(path + "/health", context => { var room = Resolve(context); context.Response.StatusCode = room.IsHealthy ? 200 : 503; return Write(context, room.Capabilities); });
            if (registry != null) {
                app.MapGet("/admin/rooms", context => { registry.Authenticate(Token(context)); return Write(context, RoomList()); });
                async Task CreateRoom(HttpContext context, bool admin) {
                    if (admin) registry.Authenticate(Token(context));
                    else registry.AuthorizeCreation(context.Request.Headers.ContainsKey("Authorization") ? Token(context) : null);
                    DocumentModel.Require(await registry.Creation.WaitAsync(0, context.RequestAborted), "server-creation-busy");
                    try {
                        // Admin seeds have a separate bounded local-export envelope; ordinary wire messages stay 4 MiB.
                        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                        if (limit != null && !limit.IsReadOnly) limit.MaxRequestBodySize = LocalJson.MaxBytes;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        var seed = await Read<Bootstrap>(context, LocalJson.MaxBytes, timeout.Token);
                        timeout.Token.ThrowIfCancellationRequested();
                        await Write(context, await Task.Run(() => registry.Create(seed)));
                    }
                    finally { registry.Creation.Release(); }
                }
                app.MapPost("/admin/rooms", context => CreateRoom(context, true));
                app.MapPost("/rooms", context => CreateRoom(context, false));
            }
            app.MapGet(path + "/diagnostics", context => {
                var selected = Resolve(context); selected.AuthorizeDiagnostics(Token(context));
                return Write(context, new DiagnosticReport { source = "room-owner", durable = selected.IsDurable, selectedRoom = RoomMetrics(selected) });
            });
            if (registry != null) app.MapGet("/admin/diagnostics", context => {
                registry.Authenticate(Token(context)); return Write(context, OperatorDiagnostics());
            });
            app.MapPost(path + "/join", async context => {
                var room = Resolve(context);
                var limits = roomLimits.GetOrAdd(room.Name, _ => new RouteLimits());
                lock (limits.joinGate) {
                    if (Environment.TickCount64 - limits.window > 60000) { limits.window = Environment.TickCount64; limits.attempts = 0; }
                    if (limits.attempts >= 60) throw new CollaborationException("join-rate-limit", (int)Math.Max(250, 60000 - (Environment.TickCount64 - limits.window)));
                    limits.attempts++;
                }
                await Write(context, room.Join(await Read<JoinRequest>(context), requireGenerationProof: true));
            });
            app.MapPost(path + "/lease", async context => await Write(context, Resolve(context).Reserve(Token(context), await Read<LeaseRequest>(context))));
            app.MapPost(path + "/release", async context => {
                Resolve(context).Release(Token(context), (await Read<LeaseRequest>(context)).token); await Write(context, new WireError());
            });
            app.MapPost(path + "/preview", async context => {
                Resolve(context).Preview(Token(context), await Read<Preview>(context)); await Write(context, new WireError());
            });
            app.MapPost(path + "/commit", async context => {
                var transaction = await Read<Transaction>(context);
                foreach (var reference in DocumentModel.AssetWrites(transaction)) GeometryTransfer.ValidateReference(reference);
                await Write(context, Resolve(context).Commit(Token(context), transaction));
            });
            // One active transfer per room; refuse excess requests instead of queueing unbounded bodies.
            app.MapMethods(path + "/assets/{hash}", new[] { "PUT", "GET", "HEAD" }, async context => {
                var room = Resolve(context);
                var transferGate = roomLimits.GetOrAdd(room.Name, _ => new RouteLimits()).transfers;
                string token = Token(context), hash = context.Request.RouteValues["hash"]?.ToString();
                bool upload = context.Request.Method == "PUT";
                room.AuthorizeAsset(token, hash, upload);
                if (context.Request.Method == "HEAD") { context.Response.ContentLength = room.AssetLength(token, hash); return; }
                DocumentModel.Require(await transferGate.WaitAsync(0, context.RequestAborted), "asset-transfer-busy");
                bool processTransfer = false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try {
                    DocumentModel.Require(await budget.Transfers.WaitAsync(0, context.RequestAborted), "server-transfer-busy"); processTransfer = true;
                    if (upload) {
                        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                        if (limit != null && !limit.IsReadOnly) limit.MaxRequestBodySize = GeometryTransfer.MaxAssetBytes;
                        DocumentModel.Require(!context.Request.ContentLength.HasValue || context.Request.ContentLength <= GeometryTransfer.MaxAssetBytes, "asset-byte-limit");
                        using var bytes = new MemoryStream(); var buffer = new byte[GeometryTransfer.BufferBytes]; int read;
                        while ((read = await context.Request.Body.ReadAsync(buffer, timeout.Token)) > 0) {
                            room.AuthorizeAsset(token, hash, true);
                            DocumentModel.Require(bytes.Length + read <= GeometryTransfer.MaxAssetBytes, "asset-byte-limit"); bytes.Write(buffer, 0, read);
                        }
                        timeout.Token.ThrowIfCancellationRequested(); room.PutAsset(token, hash, bytes.ToArray());
                        await Write(context, new WireError());
                    }
                    else {
                        byte[] bytes = room.GetAsset(token, hash);
                        context.Response.ContentType = "application/json"; context.Response.ContentLength = bytes.Length;
                        for (int offset = 0; offset < bytes.Length; offset += GeometryTransfer.BufferBytes) {
                            room.AuthorizeAsset(token, hash, false);
                            await context.Response.Body.WriteAsync(bytes.AsMemory(offset, Math.Min(GeometryTransfer.BufferBytes, bytes.Length - offset)), timeout.Token);
                        }
                    }
                }
                finally { if (processTransfer) budget.Transfers.Release(); transferGate.Release(); }
            });
            app.MapGet(path + "/receipt", context => Write(context, Resolve(context).Outcome(Token(context), context.Request.Query["operationId"],
                context.Request.Query.ContainsKey("generation") ? context.Request.Query["generation"].ToString() : null)));
            app.MapGet(path + "/replacement", context => Write(context, Resolve(context).InspectReplacement(Token(context))));
            app.MapGet(path + "/replacement-result", context => Write(context, Resolve(context).ReplacementOutcome(Token(context), context.Request.Query["operationId"])));
            app.MapPost(path + "/replacement", async context => {
                var room = Resolve(context); string credential = Token(context); room.AuthorizeReplacement(credential);
                // Share the existing one-candidate process permit with creation, so
                // replacement never introduces eight parallel portable-body buffers.
                var candidate = registry?.Creation ?? standaloneReplacement;
                DocumentModel.Require(await candidate.WaitAsync(0, context.RequestAborted), "room-replacement-busy");
                try {
                    var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                    if (limit != null && !limit.IsReadOnly) limit.MaxRequestBodySize = LocalJson.MaxBytes;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var request = await Read<RoomReplacementRequest>(context, LocalJson.MaxBytes, timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    await Write(context, await Task.Run(() => room.Replace(credential, request)));
                }
                finally { candidate.Release(); }
            });
            app.MapPost(path + "/leave", async context => { Resolve(context).Leave(Token(context)); await Write(context, new WireError()); });
            app.MapGet(path + "/participants", context => Write(context, Resolve(context).Memberships(Token(context))));
            app.MapPost(path + "/participants/remove", async context => await Write(context, Resolve(context).RemoveParticipant(Token(context), await Read<RemoveParticipant>(context))));
            app.MapGet(path + "/events", async context => {
                var room = Resolve(context);
                var eventGate = roomLimits.GetOrAdd(room.Name, _ => new RouteLimits()).events;
                DocumentModel.Require(await eventGate.WaitAsync(0, context.RequestAborted), "room-event-limit");
                bool processEvent = false;
                try {
                    DocumentModel.Require(await eventRequests.WaitAsync(0, context.RequestAborted), "server-event-limit"); processEvent = true;
                    string token = Token(context);
                    DocumentModel.Require(long.TryParse(context.Request.Query["after"], out var after) && long.TryParse(context.Request.Query["transient"], out _), "invalid-cursor");
                    long transient = long.Parse(context.Request.Query["transient"], System.Globalization.CultureInfo.InvariantCulture);
                    bool immediate = context.Request.Query.ContainsKey("wait");
                    DocumentModel.Require(!immediate || context.Request.Query["wait"] == "0" && !context.WebSockets.IsWebSocketRequest, "invalid-event-wait");
                    if (!context.WebSockets.IsWebSocketRequest) {
                        await Write(context, await room.ReadAsync(token, after, transient, context.RequestAborted, wait: !immediate)); return;
                    }
                    // Authenticate before the upgrade, so invalid credentials have an ordinary HTTP error.
                    EventBatch batch = await room.ReadAsync(token, after, -1, context.RequestAborted);
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    var closed = WatchSocket(socket, lifetime);
                    try {
                        while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open) {
                            byte[] bytes = WireJson.Encode(batch);
                            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token);
                            if (batch.edits.Length > 0) after = batch.edits.Last().revision;
                            transient = batch.transientVersion;
                            batch = await room.ReadAsync(token, after, transient, lifetime.Token);
                        }
                    }
                    catch (CollaborationException error) {
                        // A wake from durable revocation must terminate this event connection with
                        // the same reason as held requests. Bound the close even for a stalled peer.
                        using var close = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        try {
                            await socket.CloseOutputAsync(error.Code == "server-stopping" ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.PolicyViolation, error.Code, close.Token);
                            // An immediate Abort can discard the queued close frame
                            // before a delayed peer reads it. Allow its receive/close
                            // to finish within this same one-second close budget.
                            await Task.WhenAny(closed, Task.Delay(Timeout.Infinite, close.Token));
                        }
                        catch (WebSocketException) { }
                        catch (OperationCanceledException) { }
                    }
                    catch (WebSocketException) { }
                    catch (OperationCanceledException) { }
                    finally { lifetime.Cancel(); socket.Abort(); try { await closed; } catch (WebSocketException) { } }
                }
                finally { if (processEvent) eventRequests.Release(); eventGate.Release(); }
            });
        }

        private static async Task WatchSocket(WebSocket socket, CancellationTokenSource lifetime) {
            try {
                var buffer = new byte[128];
                // This channel is receive-only for clients. Mutations use authenticated HTTP requests.
                await socket.ReceiveAsync(buffer, lifetime.Token);
            }
            catch (OperationCanceledException) { }
            finally { lifetime.Cancel(); }
        }

        public async Task StartAsync(CancellationToken cancellation = default) {
            await app.StartAsync(cancellation);
            Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
        }
        public Task WaitAsync(CancellationToken cancellation) => app.WaitForShutdownAsync(cancellation);
        public async ValueTask DisposeAsync() {
            try { await app.StopAsync(TimeSpan.FromSeconds(5)); await app.DisposeAsync(); }
            finally { Room?.CloseStorage(); registry?.Dispose(); }
        }
        private static string Token(HttpContext context) {
            string auth = context.Request.Headers.Authorization;
            DocumentModel.Require(auth != null && auth.StartsWith("Bearer ", StringComparison.Ordinal) && auth.Length < 270, "invalid-session");
            return auth.Substring(7);
        }
        private static async Task<T> Read<T>(HttpContext context, int maximum = WireJson.MaxMessageBytes, CancellationToken cancellation = default) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30)); cancellation = timeout.Token;
            using var stream = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, cancellation)) > 0) {
                DocumentModel.Require(stream.Length + count <= maximum, "message-too-large");
                stream.Write(buffer, 0, count);
            }
            return LocalJson.Decode<T>(stream.ToArray(), maximum);
        }
        private static Task Reject(HttpContext context, string code, int status, int hint = 0) {
            context.Response.StatusCode = status;
            int delay = ConnectionRetry.CooldownMilliseconds(code, hint);
            if (delay > 0) context.Response.Headers.RetryAfter = ((delay + 999) / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // HEAD carries no JSON body. Only a fixed vocabulary is projected by clients.
            if (context.Request.Method == "HEAD" || code == "server-stopping") context.Response.Headers["X-Protobot-Rejection"] = code;
            return Write(context, new WireError { error = code });
        }
        private static async Task Write<T>(HttpContext context, T value) {
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(WireJson.Encode(value), context.RequestAborted);
        }
    }
}
