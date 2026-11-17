using System;
using System.Collections.Generic;

namespace Protobot.Collaboration {
    // Fixed wire rejection vocabulary. Arbitrary origin/proxy bodies never become
    // exceptions, receipt feedback, progress, UI text or logs.
    public static class WireFailure {
        private static readonly HashSet<string> Codes = new HashSet<string>(StringComparer.Ordinal) {
            "alpha-history-limit", "asset-already-exists", "asset-byte-limit", "asset-hash-mismatch", "asset-materialization-quota",
            "asset-room-quota", "asset-scene-quota", "asset-transfer-busy", "asset-unused-quota", "authority-only-mutation",
            "catalog-mismatch", "catalog-or-room-mismatch", "chain-identity-in-use", "custom-content-immutable", "document-limit",
            "duplicate-asset", "duplicate-chain", "duplicate-or-invalid-id", "generation-mismatch", "history-changed-by-another-participant",
            "identity-in-use", "invalid-asset", "invalid-asset-hash", "invalid-asset-mutation", "invalid-asset-reference",
            "invalid-catalog", "invalid-catalog-part", "invalid-chain", "invalid-chain-delete", "invalid-chain-dependency",
            "invalid-chain-mutation", "invalid-chain-route", "invalid-chain-tombstone", "invalid-color", "invalid-custom-mount",
            "invalid-custom-part", "invalid-delete", "invalid-dependency", "invalid-document", "invalid-history-command",
            "invalid-name", "invalid-operation", "invalid-parameter", "invalid-parameters", "invalid-part",
            "invalid-participant-id", "invalid-position", "invalid-preview", "invalid-preview-sequence", "invalid-reservation",
            "invalid-room", "invalid-rotation", "invalid-session", "invalid-tombstone", "lease-expired",
            "lease-owner", "lease-set-mismatch", "local-serialization-limit", "long-polling", "membership-revoked",
            "missing-asset", "missing-chain", "missing-chain-member", "missing-chain-member-version", "missing-chain-version",
            "missing-entity", "missing-history-asset", "missing-write-version", "mounts-only", "nothing-to-redo",
            "nothing-to-undo", "owner-removal-protected", "owner-required", "participant-not-found", "parts-reserved",
            "pose-only", "preview-set-mismatch", "protocol-mismatch", "recovery-catalog-or-room-mismatch", "recovery-room-not-found",
            "resync-required", "room-event-limit", "room-export-version-mismatch", "room-full", "room-not-found",
            "room-request-limit", "room-storage-failed-restart-required", "room-unavailable", "schema-mismatch", "sequence-reused",
            "server-event-limit", "server-geometry-quota", "server-request-limit", "server-transfer-busy", "server-version-mismatch",
            "server-stopping", "short-polling", "snapshot-byte-limit", "stale-chain", "stale-document", "stale-entity",
            "stale-history", "unknown-part", "unreferenced-asset", "unsupported-mutation", "viewer-read-only",
            "server-admin-required", "write-set-limit", "wrong-password", "join-rate-limit", "connection-attempt-timeout", "safe-retry-exhausted",
            "room-creation-key-required", "server-creation-busy", "server-room-limit", "room-already-registered", "duplicate-room-identity",
            "registry-destination-exists", "invalid-room-bootstrap", "server-storage-write-failed",
            "room-replaced", "room-replacement-busy", "room-replacement-limit", "room-replacement-requires-durable-room",
            "room-replacement-client-required",
            "replacement-target-changed", "invalid-replacement-target", "invalid-replacement-request", "invalid-replacement-receipt",
            "operation-not-accepted-before-replacement", "saved-room-does-not-match", "unresolved-previous-generation-operations", "client-connection-changed",
        };
        public static string Code(string value) => value == null ? null : Codes.Contains(value) ? value : "server-rejected-request";
        public static string Code(Exception error) => error is CollaborationException known && Codes.Contains(known.Code ?? "") ? known.Code : DiagnosticExport.Code(error);
    }
}
