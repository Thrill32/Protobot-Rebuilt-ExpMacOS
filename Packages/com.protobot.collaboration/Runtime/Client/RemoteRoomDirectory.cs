using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    // Read-only discovery; no credential, membership, mutation or admin-list fallback.
    public sealed class RemoteRoomDirectory : IDisposable {
        private readonly HttpClient http;
        public string Origin { get; }
        public RemoteRoomDirectory(string origin, bool allowLanHttp = false, NetworkPolicy network = null) {
            Origin = ConnectionDoctor.Origin(origin, allowLanHttp);
            http = (network ?? NetworkPolicy.SystemDefault).CreateHttpClient();
        }
        public async Task<RoomDirectory> ReadAsync(CancellationToken cancellation) {
            var caps = await ConnectionRetry.GetAsync<Capabilities>(http, Origin + "/capabilities", cancellation).ConfigureAwait(false);
            DocumentModel.Require(caps != null && caps.protocol == 1 && caps.version == CollaborationProtocol.Version, "server-version-mismatch");
            DocumentModel.Require(caps.multiRoom && caps.roomDirectoryVersion == ServerRooms.DirectoryVersion, "room-directory-unavailable");
            DocumentModel.Require(caps.roomDirectoryPolicy == "public", "room-directory-disabled");
            DocumentModel.Require(DocumentModel.ValidId(caps.serverId), "invalid-server-response");
            var value = await ConnectionRetry.GetAsync<RoomDirectory>(http, Origin + "/rooms", cancellation, maximum: 4096).ConfigureAwait(false);
            DocumentModel.Require(value != null && value.directoryVersion == ServerRooms.DirectoryVersion && value.serverId == caps.serverId &&
                value.rooms != null && value.rooms.Length <= ServerRooms.DirectoryLimit && value.rooms.All(r => r != null && DocumentModel.ValidId(r.room)) &&
                value.rooms.Select(r => r.room).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.rooms.Length, "invalid-server-response");
            return value;
        }
        public void Dispose() => http.Dispose();
    }
}
