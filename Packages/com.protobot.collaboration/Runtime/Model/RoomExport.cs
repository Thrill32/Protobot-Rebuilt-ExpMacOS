using System;
using System.Linq;

namespace Protobot.Collaboration {
    // Portable accepted content only. Credentials, leases and personal history
    // belong to the authority store and never travel in this file.
    [Serializable] public sealed class RoomExport {
        public int schema = 1;
        public string version = CollaborationProtocol.Version;
        public CatalogManifest catalog;
        public Document document;
        public void Validate() {
            DocumentModel.Require(schema == 1 && version == CollaborationProtocol.Version, "room-export-version-mismatch");
            DocumentModel.Validate(document, catalog);
            DocumentModel.Require(catalog.entries != null && catalog.entries.Length > 0 && catalog.entries.Length <= 1000 &&
                catalog.entries.All(e => e != null && DocumentModel.ValidId(e.id) && e.first != null && e.second != null && e.variants != null) &&
                catalog.entries.Select(e => e.id).Distinct(StringComparer.Ordinal).Count() == catalog.entries.Length, "invalid-catalog");
        }
        public Bootstrap CreateRoom(string room, string password, string ownerCredential) {
            Validate(); DocumentModel.Require(DocumentModel.ValidId(room), "invalid-room");
            var copy = WireJson.Clone(document);
            copy.generation = Guid.NewGuid().ToString("N"); copy.revision = 0; copy.tombstones = Array.Empty<VersionRef>();
            copy.chainTombstones = Array.Empty<VersionRef>();
            foreach (var part in copy.parts) part.version = 1;
            foreach (var chain in DocumentModel.Chains(copy)) chain.version = 1;
            return new Bootstrap { room = room, password = password, ownerCredential = ownerCredential, catalog = WireJson.Clone(catalog), document = copy };
        }
    }
}
