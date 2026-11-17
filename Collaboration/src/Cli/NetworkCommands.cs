using System;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;

internal static partial class Program {
    private static async Task<NetworkPolicy> Network(Options options, CancellationToken cancellation) {
        DocumentModel.Require(!options.Has("--network-file") || !options.Has("--network-route"), "proxy-auth-route-mismatch");
        if (options.Has("--network-file")) return await NetworkPolicy.LoadAsync(options.Get("--network-file"), path => {
            try { RequirePrivateCredential(path); }
            catch { throw new CollaborationException("proxy-config-private-file-required"); }
        }, cancellation);
        string route = options.Get("--network-route", "system");
        DocumentModel.Require(route == "system" || route == "direct", "proxy-route-unsupported");
        return route == "direct" ? NetworkPolicy.Direct : NetworkPolicy.SystemDefault;
    }
    private static CancellationTokenSource CommandCancellation() {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; source.Cancel(); }; return source;
    }
}
