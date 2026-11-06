using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

// Failure controls are accessible only to this process, with a listener bound to loopback.
internal sealed class LoopbackProxy : IAsyncDisposable {
    private readonly WebApplication app;
    private readonly HttpClient forward = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public string Url { get; private set; }
    public bool DropNextCommitResponse;
    public int CommitDelayMilliseconds;
    public LoopbackProxy(string upstream) {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        app.Run(async context => {
            if (string.Equals(context.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstream + context.Request.Path + context.Request.QueryString);
            if (context.Request.Headers.TryGetValue("Authorization", out var auth)) request.Headers.TryAddWithoutValidation("Authorization", auth.ToArray());
            if (context.Request.Method == "POST") request.Content = new StreamContent(context.Request.Body);
            try {
                using var response = await forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                bool commit = context.Request.Path.Value.EndsWith("/commit", StringComparison.Ordinal);
                if (commit && DropNextCommitResponse) { DropNextCommitResponse = false; context.Abort(); return; }
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
