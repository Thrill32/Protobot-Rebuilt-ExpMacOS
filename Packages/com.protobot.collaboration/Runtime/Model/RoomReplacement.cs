using System;
using System.Linq;

namespace Protobot.Collaboration {
    [Serializable] public sealed class RoomReplacementTarget {
        public int version = 1;
        public string room, roomIdentity, catalogHash, generation, documentHash;
        public long revision;
    }
    [Serializable] public sealed class RoomReplacementRequest {
        public int version = 1;
        public string operationId = Guid.NewGuid().ToString("N");
        public RoomReplacementTarget expected;
        public RoomExport content;
    }
    [Serializable] public sealed class RoomReplacementReceipt {
        public int version = 1;
        public string operationId, room, roomIdentity, catalogHash;
        public string previousGeneration, previousHash, generation, documentHash;
        public long previousRevision, revision;
    }
    [Serializable] public sealed class RoomReplacementOutcome {
        public int version = 1;
        public bool found;
        public RoomReplacementReceipt receipt;
    }
    public static class RoomReplacement {
        public const int Version = 1, MaxReplacements = 64;
        public const int EditorPolicyVersion = 1;
        public static void ValidateTarget(RoomReplacementTarget target) => DocumentModel.Require(target != null && target.version == Version &&
            DocumentModel.ValidId(target.room) && DocumentModel.ValidId(target.roomIdentity) && DocumentModel.ValidId(target.generation) &&
            target.revision >= 0 && DocumentModel.ValidAssetHash(target.documentHash) && target.catalogHash != null &&
            target.catalogHash.Length > 0 && target.catalogHash.Length <= 128 && !target.catalogHash.Any(char.IsControl), "invalid-replacement-target");
        public static void ValidateReceipt(RoomReplacementReceipt receipt) {
            DocumentModel.Require(receipt != null && receipt.version == Version && DocumentModel.ValidId(receipt.operationId) &&
                DocumentModel.ValidId(receipt.previousGeneration) && DocumentModel.ValidAssetHash(receipt.previousHash) &&
                receipt.previousRevision >= 0 && receipt.previousRevision < long.MaxValue && receipt.revision == receipt.previousRevision + 1,
                "invalid-replacement-receipt");
            ValidateTarget(new RoomReplacementTarget { room = receipt.room, roomIdentity = receipt.roomIdentity, catalogHash = receipt.catalogHash,
                generation = receipt.generation, documentHash = receipt.documentHash, revision = receipt.revision });
            DocumentModel.Require(receipt.generation != receipt.previousGeneration, "invalid-replacement-receipt");
        }
        public static bool SameTarget(RoomReplacementTarget a, RoomReplacementTarget b) => a != null && b != null && a.version == b.version &&
            a.room == b.room && a.roomIdentity == b.roomIdentity && a.catalogHash == b.catalogHash && a.generation == b.generation && a.revision == b.revision && a.documentHash == b.documentHash;
    }
}
