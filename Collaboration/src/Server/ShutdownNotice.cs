using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Protobot.Collaboration.Server {
    public sealed partial class CollaborationServer {
        private int stopping;
        // Runs before Kestrel's listener shutdown on every
        // Generic Host stop path: SIGTERM, Ctrl+C, private EOF and DisposeAsync.
        private sealed class ShutdownNotice : IHostedLifecycleService {
            private readonly CollaborationServer server;
            public ShutdownNotice(CollaborationServer server) { this.server = server; }
            public async Task StoppingAsync(CancellationToken cancellation) {
                if (Interlocked.Exchange(ref server.stopping, 1) != 0) return;
                server.Room?.BeginShutdown(); server.registry?.BeginShutdown();
                // Keep the listener briefly available for idle short polls too.
                // Delivery is best effort; this delay shares the five-second host
                // deadline, never extends it, and creates no queue/reconnect loop.
                try { await Task.Delay(1500, cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            public Task StartingAsync(CancellationToken cancellation) => Task.CompletedTask;
            public Task StartAsync(CancellationToken cancellation) => Task.CompletedTask;
            public Task StartedAsync(CancellationToken cancellation) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellation) => Task.CompletedTask;
            public Task StoppedAsync(CancellationToken cancellation) => Task.CompletedTask;
        }
    }
}
