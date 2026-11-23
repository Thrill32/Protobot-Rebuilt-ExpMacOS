using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    public sealed partial class CollaborationClient {
        public const int MinimumShortPollMilliseconds = 250;
        public const int IdleShortPollMilliseconds = 1000;
        private async Task PumpRequestsAsync(CancellationToken cancellation) {
            bool shortPolling = Transport == CollaborationTransport.ShortPolling;
            ActiveTransport = shortPolling ? "short-polling" : "long-polling";
            while (!cancellation.IsCancellationRequested) {
                var batch = await Request<EventBatch>(HttpMethod.Get, EventUri(), null, cancellation).ConfigureAwait(false);
                await ApplyAsync(batch, cancellation).ConfigureAwait(false);
                // No overlapping reads or retry queue. Network/materialization work
                // adds to this minimum spacing; idle clients issue at most one read/s.
                if (shortPolling) await Task.Delay(batch.edits.Length > 0 ? MinimumShortPollMilliseconds : IdleShortPollMilliseconds, cancellation).ConfigureAwait(false);
            }
        }
    }
}
