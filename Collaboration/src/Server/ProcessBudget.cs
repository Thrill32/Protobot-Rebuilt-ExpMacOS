using System;
using System.Threading;

namespace Protobot.Collaboration.Server {
    // Short accounting locks only. Never hold this gate while doing room work or I/O.
    public sealed class ProcessBudget {
        public const int MaxRooms = 8, MaxRequests = 64, MaxRoomRequests = 32, MaxEvents = 32, MaxTransfers = 2;
        public const long MaxRetainedBytes = 128L * 1024 * 1024;
        public const long MaxDecodedBytes = 256L * 1024 * 1024;
        public const long MaxSceneBytes = 256L * 1024 * 1024;
        private readonly object gate = new object();
        private long retained, decoded, scene;
        public (long retained, long decoded, long scene) Usage { get { lock (gate) return (retained, decoded, scene); } }
        public SemaphoreSlim Transfers { get; } = new SemaphoreSlim(MaxTransfers, MaxTransfers);
        public Reservation Reserve(long bytes, long arrays, long instances) {
            lock (gate) {
                DocumentModel.Require(retained + bytes <= MaxRetainedBytes && decoded + arrays <= MaxDecodedBytes && scene + instances <= MaxSceneBytes,
                    "server-geometry-quota");
                retained += bytes; decoded += arrays; scene += instances;
                return new Reservation(this, bytes, arrays, instances);
            }
        }
        public sealed class Reservation : IDisposable {
            private ProcessBudget budget;
            private readonly long bytes, arrays, instances;
            internal Reservation(ProcessBudget budget, long bytes, long arrays, long instances) {
                this.budget = budget; this.bytes = bytes; this.arrays = arrays; this.instances = instances;
            }
            public void Commit() => budget = null;
            public void Dispose() {
                var owner = Interlocked.Exchange(ref budget, null);
                if (owner != null) lock (owner.gate) { owner.retained -= bytes; owner.decoded -= arrays; owner.scene -= instances; }
            }
        }
    }
}
