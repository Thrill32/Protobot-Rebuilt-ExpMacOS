using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Protobot.Collaboration {
    public static class DocumentModel {
        public const int MaxParts = 2000;
        public const int MaxWriteSet = 256;

        public static void Validate(Document document, CatalogManifest catalog) {
            Require(document != null && catalog != null && document.schema == 1 && catalog.protocol == 1, "schema-mismatch");
            Require(ValidId(document.generation) && document.revision >= 0, "invalid-document");
            Require(!string.IsNullOrEmpty(catalog.hash) && document.catalogHash == catalog.hash, "catalog-mismatch");
            Require(document.parts != null && document.parts.Length <= MaxParts && document.tombstones != null, "document-limit");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in document.parts) {
                ValidatePart(part, catalog);
                Require(part.version > 0 && ids.Add(part.id), "duplicate-or-invalid-id");
            }
            foreach (var tombstone in document.tombstones)
                Require(tombstone != null && ValidId(tombstone.id) && tombstone.version > 0 && ids.Add(tombstone.id), "invalid-tombstone");
            Require(WireJson.Encode(document).Length <= 2 * 1024 * 1024, "snapshot-byte-limit");
        }

        public static Document Reduce(Document current, Transaction tx, CatalogManifest catalog) {
            Require(tx != null && tx.protocol == 1, "protocol-mismatch");
            Require(tx.generation == current.generation, "generation-mismatch");
            Require(ValidId(tx.operationId) && tx.sequence > 0, "invalid-operation");
            Require(tx.expected != null && tx.writes != null && tx.deletes != null &&
                tx.writes.Length + tx.deletes.Length > 0 && tx.writes.Length + tx.deletes.Length <= MaxWriteSet &&
                tx.expected.Length <= MaxWriteSet * 2, "write-set-limit");
            Require(tx.kind == "AddParts" || tx.kind == "SetPoses" || tx.kind == "SetParts" || tx.kind == "DeleteParts" ||
                tx.kind == "TransformAndCopy" || tx.kind == "RestoreParts", "unsupported-mutation");
            var parts = current.parts.ToDictionary(p => p.id, StringComparer.Ordinal);
            var tombstones = current.tombstones.ToDictionary(p => p.id, p => p.version, StringComparer.Ordinal);
            var expected = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var dependency in tx.expected) {
                Require(dependency != null && ValidId(dependency.id) && !expected.ContainsKey(dependency.id), "invalid-dependency");
                expected.Add(dependency.id, dependency.version);
                var actual = parts.TryGetValue(dependency.id, out var part) ? part.version :
                    tombstones.TryGetValue(dependency.id, out var deleted) ? deleted : 0;
                Require(actual == dependency.version, "stale-entity");
            }
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var write in tx.writes) {
                ValidatePart(write, catalog);
                Require(changed.Add(write.id) && expected.ContainsKey(write.id), "missing-write-version");
                bool exists = parts.TryGetValue(write.id, out var before);
                if (tx.kind == "AddParts") {
                    Require(!exists && !tombstones.ContainsKey(write.id) && expected[write.id] == 0 && tx.deletes.Length == 0, "identity-in-use");
                }
                else if (tx.kind == "RestoreParts") {
                    Require(exists || tombstones.ContainsKey(write.id), "missing-entity");
                }
                else if (tx.kind == "TransformAndCopy" && !exists) {
                    Require(!tombstones.ContainsKey(write.id) && expected[write.id] == 0 && tx.deletes.Length == 0, "identity-in-use");
                }
                else {
                    Require(exists && tx.kind != "DeleteParts" && tx.deletes.Length == 0, "missing-entity");
                    if (tx.kind != "SetParts") Require(before.catalogId == write.catalogId && before.state == write.state && before.hasPaint == write.hasPaint &&
                        before.color.SequenceEqual(write.color), "pose-only");
                }
                var accepted = WireJson.Clone(write);
                accepted.version = exists ? before.version + 1 : tombstones.TryGetValue(write.id, out var dead) ? dead + 1 : 1;
                // Canonical signs and negative zero are normalized by the authority.
                accepted.rotation = NormalizeRotation(accepted.rotation);
                parts[accepted.id] = accepted;
                tombstones.Remove(accepted.id);
            }
            foreach (var id in tx.deletes) {
                Require((tx.kind == "RestoreParts" || tx.kind == "DeleteParts" && tx.writes.Length == 0) && ValidId(id) && changed.Add(id) &&
                    expected.ContainsKey(id) && parts.ContainsKey(id), "invalid-delete");
                tombstones[id] = parts[id].version + 1;
                parts.Remove(id);
            }
            Require(parts.Count <= MaxParts && tombstones.Count <= 20000, "document-limit");
            var next = new Document {
                schema = current.schema, generation = current.generation, catalogHash = current.catalogHash,
                revision = checked(current.revision + 1), parts = parts.Values.OrderBy(p => p.id, StringComparer.Ordinal).ToArray(),
                tombstones = tombstones.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new VersionRef { id = p.Key, version = p.Value }).ToArray()
            };
            Require(WireJson.Encode(next).Length <= 2 * 1024 * 1024, "snapshot-byte-limit");
            return next;
        }

        // History compares logical content; accepted entity versions always increase, including resurrection.
        public static bool SamePart(Part a, Part b) => a == null || b == null ? a == b :
            a.id == b.id && a.catalogId == b.catalogId && (a.state ?? "") == (b.state ?? "") && a.hasPaint == b.hasPaint &&
            a.position.SequenceEqual(b.position) && NormalizeRotation(a.rotation).SequenceEqual(NormalizeRotation(b.rotation)) && a.color.SequenceEqual(b.color);

        public static void ValidatePart(Part part, CatalogManifest catalog) {
            Require(part != null && ValidId(part.id) && part.catalogId != null && part.catalogId.Length <= 200 &&
                (part.state == null || part.state.Length <= 4096), "invalid-part");
            Require(part.position != null && part.position.Length == 3 && part.position.All(v => Finite(v) && Math.Abs(v) <= 1000000), "invalid-position");
            Require(part.rotation != null && part.rotation.Length == 4 && part.rotation.All(Finite) &&
                Math.Abs(part.rotation.Sum(v => v * v) - 1) < 0.0001, "invalid-rotation");
            Require(part.color != null && part.color.Length == 3 && part.color.All(v => Finite(v) && v >= 0 && v <= 1), "invalid-color");
            var values = part.catalogId.Split('-');
            var entry = catalog.entries.FirstOrDefault(e => e.id == values[0]);
            Require(entry != null, "unknown-part");
            Require(values.Length == 1 + (entry.first.used ? 1 : 0) + (entry.second.used ? 1 : 0), "invalid-parameters");
            if (entry.first.used) {
                ValidateParameter(entry.first, values[1]);
                if (entry.second.used) {
                    var variant = entry.variants.FirstOrDefault(v => v.first == values[1]);
                    ValidateParameter(variant?.second ?? entry.second, values[2]);
                }
            }
        }

        private static void ValidateParameter(ParameterRule rule, string value) {
            if (rule.custom) {
                Require(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    Finite(number) && number >= rule.min && number <= rule.max && (!rule.integer || number == Math.Round(number)), "invalid-parameter");
            }
            else Require(rule.options.Contains(value, StringComparer.Ordinal), "invalid-parameter");
        }

        // This hash deliberately includes IDs, versions and tombstones, unlike file-dirty comparison.
        public static string Hash(Document document) {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) {
                    writer.Write(document.schema); writer.Write(document.generation); writer.Write(document.catalogHash);
                    writer.Write(document.revision); writer.Write(document.parts.Length);
                    foreach (var part in document.parts.OrderBy(p => p.id, StringComparer.Ordinal)) {
                        writer.Write(part.id); writer.Write(part.version); writer.Write(part.catalogId);
                        writer.Write(part.state ?? ""); writer.Write(part.hasPaint);
                        foreach (var n in part.position.Concat(NormalizeRotation(part.rotation)).Concat(part.color)) writer.Write(n == 0 ? 0.0 : n);
                    }
                    writer.Write(document.tombstones.Length);
                    foreach (var dead in document.tombstones.OrderBy(p => p.id, StringComparer.Ordinal)) { writer.Write(dead.id); writer.Write(dead.version); }
                }
                using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream.ToArray()));
            }
        }

        public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        private static double[] NormalizeRotation(double[] value) {
            // q and -q are identical. Do not renormalize floats repeatedly across runtimes.
            var sign = value[3] != 0 ? Math.Sign(value[3]) : value[2] != 0 ? Math.Sign(value[2]) : value[1] != 0 ? Math.Sign(value[1]) : Math.Sign(value[0]);
            return value.Select(v => v == 0 ? 0.0 : v * sign).ToArray();
        }
        public static bool ValidId(string id) => id != null && id.Length >= 1 && id.Length <= 80 && id.All(c =>
            c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-');
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static void Require(bool value, string error) { if (!value) throw new CollaborationException(error); }
    }

    public interface IEditSession {
        Document Snapshot { get; }
        System.Threading.Tasks.Task<Receipt> SubmitAsync(Transaction transaction, System.Threading.CancellationToken cancellation = default);
    }

    public sealed class OfflineEditSession : IEditSession {
        private Document document;
        private readonly CatalogManifest catalog;
        public OfflineEditSession(Document initial, CatalogManifest manifest) {
            DocumentModel.Validate(initial, manifest); document = WireJson.Clone(initial); catalog = WireJson.Clone(manifest);
        }
        public Document Snapshot => WireJson.Clone(document);
        public System.Threading.Tasks.Task<Receipt> SubmitAsync(Transaction transaction, System.Threading.CancellationToken cancellation = default) {
            cancellation.ThrowIfCancellationRequested();
            document = DocumentModel.Reduce(document, transaction, catalog);
            return System.Threading.Tasks.Task.FromResult(new Receipt { accepted = true, operationId = transaction.operationId, revision = document.revision });
        }
    }
}
