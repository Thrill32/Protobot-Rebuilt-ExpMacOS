using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Protobot.Collaboration.Server {
    public sealed partial class CollaborationServer {
        private RoomDiagnostic RoomMetrics(Room room) {
            var value = room.Diagnostics(); AddRouteMetrics(room.Name, value.usage); return value;
        }
        private void AddRouteMetrics(string name, RoomUsage usage) {
            if (usage == null) return;
            roomLimits.TryGetValue(name, out var routes);
            usage.requests = new ResourceMetric(ProcessBudget.MaxRoomRequests - (routes?.requests.CurrentCount ?? ProcessBudget.MaxRoomRequests), ProcessBudget.MaxRoomRequests);
            usage.eventStreams = new ResourceMetric(10 - (routes?.events.CurrentCount ?? 10), 10);
            usage.transfers = new ResourceMetric(1 - (routes?.transfers.CurrentCount ?? 1), 1);
        }
        private HostedRoomList RoomList() {
            var list = registry.List();
            foreach (var room in list.rooms) AddRouteMetrics(room.room, room.usage);
            list.process = ProcessMetrics(list.rooms.Length); return list;
        }
        private ProcessUsage ProcessMetrics(int rooms) {
            var geometry = budget.Usage;
            using var process = Process.GetCurrentProcess();
            return new ProcessUsage {
                rooms = new ResourceMetric(rooms, ProcessBudget.MaxRooms), requests = new ResourceMetric(ProcessBudget.MaxRequests - requests.CurrentCount, ProcessBudget.MaxRequests),
                eventStreams = new ResourceMetric(ProcessBudget.MaxEvents - eventRequests.CurrentCount, ProcessBudget.MaxEvents),
                transfers = new ResourceMetric(ProcessBudget.MaxTransfers - budget.Transfers.CurrentCount, ProcessBudget.MaxTransfers),
                connections = new ResourceMetric(Interlocked.Read(ref connections), 128),
                retainedGeometryBytes = new ResourceMetric(geometry.retained, ProcessBudget.MaxRetainedBytes),
                decodedArrayBytes = new ResourceMetric(geometry.decoded, ProcessBudget.MaxDecodedBytes), liveInstanceArrayBytes = new ResourceMetric(geometry.scene, ProcessBudget.MaxSceneBytes),
                measuredManagedBytes = System.GC.GetTotalMemory(false), measuredWorkingSetBytes = process.WorkingSet64
            };
        }
        private DiagnosticReport OperatorDiagnostics() {
            var list = RoomList();
            return DiagnosticExport.Sanitize(new DiagnosticReport { source = "server-admin", durable = true, process = list.process,
                rooms = list.rooms.Select(r => new RoomDiagnostic { room = r.room, generation = r.generation, catalogHash = r.catalogHash, documentHash = r.documentHash,
                    revision = r.revision, status = r.status, usage = r.usage }).ToArray() });
        }
    }
}
