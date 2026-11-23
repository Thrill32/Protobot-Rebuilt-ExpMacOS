using System;

namespace Protobot.Collaboration {
    public static class ServerRooms {
        public const int CreationVersion = 1;
        public const int DirectoryVersion = 1, DirectoryLimit = 8;
        public static bool ValidDirectoryPolicy(string policy) => policy == "hidden" || policy == "public";
        public static bool ValidPolicy(string policy) => policy == "admin-only" || policy == "key" || policy == "open";
        public static void RequireCreation(Capabilities value) => DocumentModel.Require(value != null && value.protocol == 1 &&
            value.version == CollaborationProtocol.Version && value.multiRoom && value.registryVersion == 1 &&
            value.roomCreationVersion == CreationVersion && ValidPolicy(value.roomCreationPolicy) && DocumentModel.ValidId(value.serverId), "room-creation-unavailable");
    }
    // Public discovery is deliberately independent of administrator diagnostics.
    [Serializable] public sealed class RoomDirectoryEntry {
        public string room;
        public bool passwordRequired;
    }
    [Serializable] public sealed class RoomDirectory {
        public int directoryVersion = ServerRooms.DirectoryVersion;
        public string serverId;
        public RoomDirectoryEntry[] rooms = Array.Empty<RoomDirectoryEntry>();
    }
    [Serializable] public sealed class RoomCreationKey {
        public string format;
        public int schema;
        public string serverId, credential;
    }
    [Serializable] public sealed class HostedRoomStatus {
        public string room, generation, catalogHash, status, action;
        public long? revision;
        public string documentHash;
        public RoomUsage usage;
    }
    [Serializable] public sealed class HostedRoomList {
        public int registryVersion = 1;
        public int diagnosticsVersion = DiagnosticExport.Version;
        public ProcessUsage process;
        public HostedRoomStatus[] rooms = Array.Empty<HostedRoomStatus>();
    }
}
