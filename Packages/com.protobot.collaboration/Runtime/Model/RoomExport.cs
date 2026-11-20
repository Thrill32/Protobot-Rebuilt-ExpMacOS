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
            DocumentModel.Require((schema == 1 && !GeometryTransfer.HasAppearance(document) ||
                schema == 2 && GeometryTransfer.HasAppearance(document)) && version == CollaborationProtocol.Version, "room-export-version-mismatch");
            DocumentModel.Require(catalog != null && catalog.entries != null && catalog.entries.Length > 0 && catalog.entries.Length <= 1000 &&
                catalog.entries.All(e => e != null && DocumentModel.ValidId(e.id) && e.first != null && e.second != null && e.variants != null &&
                    e.variants.All(v => v != null && v.second != null)) &&
                catalog.entries.Select(e => e.id).Distinct(StringComparer.Ordinal).Count() == catalog.entries.Length, "invalid-catalog");
            DocumentModel.Validate(document, catalog); GeometryTransfer.RequireHydrated(document);
        }
        public Bootstrap CreateRoom(string room, string password, string ownerCredential, bool allowEditorProjectReplacement = false) {
            Validate(); DocumentModel.Require(DocumentModel.ValidId(room), "invalid-room");
            var copy = GeometryTransfer.CloneDocument(document);
            copy.generation = Guid.NewGuid().ToString("N"); copy.revision = 0; copy.tombstones = Array.Empty<VersionRef>();
            copy.chainTombstones = Array.Empty<VersionRef>();
            foreach (var part in copy.parts) part.version = 1;
            foreach (var chain in DocumentModel.Chains(copy)) chain.version = 1;
            return new Bootstrap { room = room, password = password, ownerCredential = ownerCredential, catalog = WireJson.Clone(catalog), document = copy,
                allowEditorProjectReplacement = allowEditorProjectReplacement };
        }
    }
}
