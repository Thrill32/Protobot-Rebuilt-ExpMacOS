using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Protobot.Collaboration.Server {
    public sealed class CollaborationServer : IAsyncDisposable {
        private readonly WebApplication app;
        private readonly RoomStore storage;
        public Room Room { get; }
        public string Url { get; private set; }

        public CollaborationServer(Bootstrap seed, string listen = "http://127.0.0.1:0", bool allowInsecureLan = false, string dataDirectory = null) {
            DocumentModel.Require(seed == null || DocumentModel.ValidId(seed.room), "invalid-room");
            var endpoint = new Uri(listen);
            DocumentModel.Require(string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) && endpoint.AbsolutePath == "/", "invalid-listener");
            DocumentModel.Require(endpoint.Scheme == "https" || endpoint.Scheme == "http" && (endpoint.IsLoopback || allowInsecureLan), "https-required-use-explicit-lan-http");
            storage = dataDirectory == null ? null : new RoomStore(dataDirectory);
            try { Room = new Room(seed, storage: storage); }
            catch { storage?.Dispose(); throw; }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            // Application stdout is reserved for the private readiness protocol. Do not log bodies/credentials.
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(listen);
            builder.WebHost.ConfigureKestrel(options => {
                options.Limits.MaxRequestBodySize = WireJson.MaxMessageBytes;
                options.Limits.MaxConcurrentConnections = 64;
                options.Limits.MaxConcurrentUpgradedConnections = 16;
            });
            app = builder.Build();
            app.Use(async (context, next) => {
                if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
                context.Response.Headers.CacheControl = "no-store";
                try { await next(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (CollaborationException error) {
                    if (!context.Response.HasStarted) {
                        context.Response.StatusCode = error.Code == "invalid-session" ? 401 : 409;
                        await Write(context, new WireError { error = error.Code });
                    }
                }
                catch (Exception error) when (error is System.Runtime.Serialization.SerializationException || error is ArgumentException || error is BadHttpRequestException) {
                    if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await Write(context, new WireError { error = "invalid-request" }); }
                }
            });
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
            app.MapGet("/capabilities", context => Write(context, Room.Capabilities));
            app.MapGet("/health", context => { context.Response.StatusCode = Room.IsHealthy ? 200 : 503; return Write(context, Room.Capabilities); });
            string path = "/rooms/" + Room.Name;
            // Bound failed-join work as well as participant count. No endpoint accepts filesystem paths.
            var joinGate = new object();
            long window = Environment.TickCount64;
            int attempts = 0;
            app.MapPost(path + "/join", async context => {
                lock (joinGate) {
                    if (Environment.TickCount64 - window > 60000) { window = Environment.TickCount64; attempts = 0; }
                    DocumentModel.Require(++attempts <= 60, "join-rate-limit");
                }
                await Write(context, Room.Join(await Read<JoinRequest>(context)));
            });
            app.MapPost(path + "/lease", async context => await Write(context, Room.Reserve(Token(context), await Read<LeaseRequest>(context))));
            app.MapPost(path + "/release", async context => {
                Room.Release(Token(context), (await Read<LeaseRequest>(context)).token); await Write(context, new WireError());
            });
            app.MapPost(path + "/preview", async context => {
                Room.Preview(Token(context), await Read<Preview>(context)); await Write(context, new WireError());
            });
            app.MapPost(path + "/commit", async context => await Write(context, Room.Commit(Token(context), await Read<Transaction>(context))));
            app.MapGet(path + "/receipt", context => Write(context, Room.Outcome(Token(context), context.Request.Query["operationId"])));
            app.MapPost(path + "/leave", async context => { Room.Leave(Token(context)); await Write(context, new WireError()); });
            app.MapGet(path + "/events", async context => {
                string token = Token(context);
                DocumentModel.Require(long.TryParse(context.Request.Query["after"], out var after) && long.TryParse(context.Request.Query["transient"], out _), "invalid-cursor");
                long transient = long.Parse(context.Request.Query["transient"], System.Globalization.CultureInfo.InvariantCulture);
                if (!context.WebSockets.IsWebSocketRequest) {
                    await Write(context, await Room.ReadAsync(token, after, transient, context.RequestAborted)); return;
                }
                // Authenticate before the upgrade, so invalid credentials have an ordinary HTTP error.
                EventBatch batch = await Room.ReadAsync(token, after, -1, context.RequestAborted);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var closed = WatchSocket(socket, lifetime);
                try {
                    while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open) {
                        byte[] bytes = WireJson.Encode(batch);
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token);
                        if (batch.edits.Length > 0) after = batch.edits.Last().revision;
                        transient = batch.transientVersion;
                        batch = await Room.ReadAsync(token, after, transient, lifetime.Token);
                    }
                }
                catch (WebSocketException) { }
                catch (OperationCanceledException) { }
                finally { lifetime.Cancel(); socket.Abort(); try { await closed; } catch (WebSocketException) { } }
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
            finally { storage?.Dispose(); }
        }
        private static string Token(HttpContext context) {
            string auth = context.Request.Headers.Authorization;
            DocumentModel.Require(auth != null && auth.StartsWith("Bearer ", StringComparison.Ordinal) && auth.Length < 270, "invalid-session");
            return auth.Substring(7);
        }
        private static async Task<T> Read<T>(HttpContext context) {
            using var stream = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0) {
                DocumentModel.Require(stream.Length + count <= WireJson.MaxMessageBytes, "message-too-large");
                stream.Write(buffer, 0, count);
            }
            return WireJson.Decode<T>(stream.ToArray());
        }
        private static async Task Write<T>(HttpContext context, T value) {
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(WireJson.Encode(value), context.RequestAborted);
        }
    }
}
