using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Protobot.Collaboration;

// Failure controls are accessible only to this process, with a listener bound to loopback.
internal sealed class LoopbackProxy : IAsyncDisposable {
    private readonly WebApplication app;
    private readonly HttpClient forward = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public string Url { get; private set; }
    public bool DropNextCommitResponse;
    public bool DropNextRemovalResponse;
    public int CommitDelayMilliseconds;
    public bool DropNextAssetUploadResponse, DropNextAssetDownload;
    public bool CorruptAssetDownload;
    public TaskCompletionSource<bool> AssetDownloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ContinueAssetDownload;
    public int AssetPauseAfterBytes;
    public TaskCompletionSource<bool> PartialAssetSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> JoinReplyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ContinueJoinReply;
    public string RejectedJoinError;
    public string RejectedAdministrationError;
    public string RejectedDiagnosticsError;
    public bool RejectHeldEvents, HideShortPollingCapability;
    public string ReportedGeneration { get; set; }
    public readonly ConcurrentQueue<long> ShortPollTimes = new ConcurrentQueue<long>();
    public int JoinRequests;
    public bool OriginProxyAuthorizationObserved, OriginBasicAuthorizationObserved;
    public bool OriginBasicChallenge { get; set; }
    public TaskCompletionSource<bool> PauseShortPoll;
    public TaskCompletionSource<bool> ShortPollPaused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public LoopbackProxy(string upstream) {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        app.Run(async context => {
            OriginProxyAuthorizationObserved |= context.Request.Headers.ContainsKey("Proxy-Authorization");
            OriginBasicAuthorizationObserved |= context.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);
            if (OriginBasicChallenge) { context.Response.StatusCode = 401; context.Response.Headers.WWWAuthenticate = "Basic realm=\"origin\""; return; }
            bool events = context.Request.Path.Value.EndsWith("/events", StringComparison.Ordinal);
            if (context.Request.Path.Value.EndsWith("/join", StringComparison.Ordinal)) Interlocked.Increment(ref JoinRequests);
            if (events && RejectHeldEvents && context.Request.Query["wait"] != "0") { context.Response.StatusCode = 403; return; }
            if (events && context.Request.Query["wait"] == "0") {
                ShortPollTimes.Enqueue(Stopwatch.GetTimestamp()); while (ShortPollTimes.Count > 64) ShortPollTimes.TryDequeue(out _);
                if (PauseShortPoll != null) {
                    ShortPollPaused.TrySetResult(true);
                    try { await PauseShortPoll.Task.WaitAsync(context.RequestAborted); } catch (OperationCanceledException) { return; }
                }
            }
            if (RejectedDiagnosticsError != null && context.Request.Path.Value.EndsWith("/diagnostics", StringComparison.Ordinal)) {
                context.Response.StatusCode = 409; await context.Response.WriteAsync(WireJson.Text(new WireError { error = RejectedDiagnosticsError })); return;
            }
            if (RejectedAdministrationError != null && context.Request.Path.Value.EndsWith("/participants", StringComparison.Ordinal)) {
                context.Response.StatusCode = 409; await context.Response.WriteAsync(WireJson.Text(new WireError { error = RejectedAdministrationError })); return;
            }
            if (RejectedJoinError != null && context.Request.Path.Value.EndsWith("/join", StringComparison.Ordinal)) {
                context.Response.StatusCode = 409; await context.Response.WriteAsync(WireJson.Text(new WireError { error = RejectedJoinError })); return;
            }
            if (string.Equals(context.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstream + context.Request.Path + context.Request.QueryString);
            if (context.Request.Headers.TryGetValue("Authorization", out var auth)) request.Headers.TryAddWithoutValidation("Authorization", auth.ToArray());
            if ((context.Request.Method == "POST" || context.Request.Method == "PUT")) request.Content = new StreamContent(context.Request.Body);
            try {
                using var response = await forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                if ((HideShortPollingCapability || ReportedGeneration != null) && context.Request.Path.Value.EndsWith("/capabilities", StringComparison.Ordinal)) {
                    var capability = WireJson.Decode<Capabilities>(await response.Content.ReadAsByteArrayAsync());
                    if (HideShortPollingCapability) capability.shortPollingVersion = 0;
                    if (ReportedGeneration != null) capability.generation = ReportedGeneration;
                    context.Response.StatusCode = (int)response.StatusCode; await context.Response.WriteAsync(WireJson.Text(capability)); return;
                }
                if (context.Request.Path.Value.EndsWith("/join", StringComparison.Ordinal) && ContinueJoinReply != null) {
                    JoinReplyStarted.TrySetResult(true); await ContinueJoinReply.Task.WaitAsync(context.RequestAborted);
                }
                bool asset = context.Request.Path.Value.Contains("/assets/", StringComparison.Ordinal);
                if (asset && context.Request.Method == "PUT" && DropNextAssetUploadResponse) { DropNextAssetUploadResponse = false; context.Abort(); return; }
                if (asset && context.Request.Method == "GET") {
                    AssetDownloadStarted.TrySetResult(true);
                    if (ContinueAssetDownload != null && AssetPauseAfterBytes == 0) await ContinueAssetDownload.Task.WaitAsync(context.RequestAborted);
                    if (DropNextAssetDownload) { DropNextAssetDownload = false; context.Abort(); return; }
                    if (CorruptAssetDownload) {
                        var corrupt = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsByteArrayAsync());
                        corrupt["vertices"][0] = 1000000;
                        await context.Response.WriteAsync(corrupt.ToJsonString()); return;
                    }
                }
                if (asset && context.Request.Method == "GET" && response.IsSuccessStatusCode && AssetPauseAfterBytes > 0) {
                    var bytes = await response.Content.ReadAsByteArrayAsync(); int prefix = Math.Min(AssetPauseAfterBytes, bytes.Length);
                    context.Response.ContentLength = bytes.Length; context.Response.ContentType = "application/json";
                    await context.Response.Body.WriteAsync(bytes.AsMemory(0, prefix), context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted); PartialAssetSent.TrySetResult(true);
                    if (ContinueAssetDownload != null) await ContinueAssetDownload.Task.WaitAsync(context.RequestAborted);
                    await context.Response.Body.WriteAsync(bytes.AsMemory(prefix), context.RequestAborted); return;
                }
                bool commit = context.Request.Path.Value.EndsWith("/commit", StringComparison.Ordinal);
                if (commit && DropNextCommitResponse) { DropNextCommitResponse = false; context.Abort(); return; }
                if (context.Request.Path.Value.EndsWith("/participants/remove", StringComparison.Ordinal) && DropNextRemovalResponse) { DropNextRemovalResponse = false; context.Abort(); return; }
                if (commit && CommitDelayMilliseconds > 0) await Task.Delay(CommitDelayMilliseconds, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "application/json";
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) { }
        });
    }
    public async Task StartAsync() {
        await app.StartAsync();
        Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(TimeSpan.FromSeconds(3)); await app.DisposeAsync(); forward.Dispose(); }
}
