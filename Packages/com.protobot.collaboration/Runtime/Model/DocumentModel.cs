using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Protobot.Collaboration {
    public static class DocumentModel {
        public const int MaxParts = 2000;
        public const int MaxChains = 2000;
        public const int MaxAssets = 256;
        public const int MaxMountsPerPart = 4096;
        public const int MaxWriteSet = 256;

        public static void Validate(Document document, CatalogManifest catalog) {
            Require(document != null && catalog != null && document.schema == 1 && catalog.protocol == 1, "schema-mismatch");
            Require(ValidId(document.generation) && document.revision >= 0, "invalid-document");
            Require(!string.IsNullOrEmpty(catalog.hash) && document.catalogHash == catalog.hash, "catalog-mismatch");
            Require(document.parts != null && document.parts.Length <= MaxParts && document.tombstones != null &&
                Chains(document).Length <= MaxChains && Assets(document).Length <= MaxAssets, "document-limit");
            var assets = new Dictionary<string, GeometryAsset>(StringComparer.Ordinal);
            foreach (var asset in Assets(document)) {
                ValidateAsset(asset);
                Require(assets.TryAdd(asset.hash, asset), "duplicate-asset");
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in document.parts) {
                ValidatePart(part, catalog);
                Require(!IsCustom(part) || assets.ContainsKey(part.assetHash), "missing-asset");
                Require(part.version > 0 && ids.Add(part.id), "duplicate-or-invalid-id");
            }
            foreach (var tombstone in document.tombstones)
                Require(tombstone != null && ValidId(tombstone.id) && tombstone.version > 0 && ids.Add(tombstone.id), "invalid-tombstone");
            foreach (var chain in Chains(document)) {
                ValidateChain(chain, document.parts);
                Require(chain.version > 0 && ids.Add(chain.id), "duplicate-or-invalid-id");
            }
            Require(Chains(document).Select(ChainTopology).Distinct(StringComparer.Ordinal).Count() == Chains(document).Length,
                "duplicate-chain");
            foreach (var tombstone in ChainTombstones(document))
                Require(tombstone != null && ValidId(tombstone.id) && tombstone.version > 0 && ids.Add(tombstone.id), "invalid-chain-tombstone");
            Require(WireJson.Encode(document).Length <= 2 * 1024 * 1024, "snapshot-byte-limit");
        }

        public static Document Reduce(Document current, Transaction tx, CatalogManifest catalog) {
            Require(tx != null && tx.protocol == 1, "protocol-mismatch");
            Require(tx.generation == current.generation, "generation-mismatch");
            Require(!tx.expectedRevision.HasValue || tx.expectedRevision.Value == current.revision, "stale-document");
            Require(ValidId(tx.operationId) && tx.sequence > 0, "invalid-operation");
            var chainWrites = ChainWrites(tx); var chainDeletes = ChainDeletes(tx); var expectedChains = ExpectedChains(tx); var assetWrites = AssetWrites(tx);
            int mutations = tx.writes?.Length + tx.deletes?.Length + chainWrites.Length + chainDeletes.Length + assetWrites.Length ?? 0;
            Require(tx.expected != null && tx.writes != null && tx.deletes != null && mutations > 0 && mutations <= MaxWriteSet &&
                tx.expected.Length <= MaxWriteSet * 2 && expectedChains.Length <= MaxWriteSet * 2, "write-set-limit");
            Require(tx.kind == "AddParts" || tx.kind == "SetPoses" || tx.kind == "SetParts" || tx.kind == "SetCustomMounts" ||
                tx.kind == "DeleteParts" || tx.kind == "TransformAndCopy" || tx.kind == "SetChains" || tx.kind == "RestoreParts",
                "unsupported-mutation");
            bool createsPartsAndChains = (tx.kind == "AddParts" || tx.kind == "TransformAndCopy") && tx.writes.Length > 0 && tx.deletes.Length == 0 &&
                chainWrites.Length > 0 && chainDeletes.Length == 0;
            Require(tx.kind == "SetChains" ? tx.writes.Length == 0 && tx.deletes.Length == 0 && chainWrites.Length + chainDeletes.Length > 0 :
                tx.kind == "RestoreParts" || createsPartsAndChains || chainWrites.Length == 0 && chainDeletes.Length == 0 ||
                tx.kind == "DeleteParts" && chainWrites.Length == 0,
                "invalid-chain-mutation");
            Require(assetWrites.Length == 0 || (tx.kind == "AddParts" || tx.kind == "TransformAndCopy") && tx.writes.Length > 0 && tx.deletes.Length == 0,
                "invalid-asset-mutation");
            Require(assetWrites.All(asset => asset != null && tx.writes.Any(part => part?.assetHash == asset.hash)), "unreferenced-asset");
            var parts = current.parts.ToDictionary(p => p.id, StringComparer.Ordinal);
            var tombstones = current.tombstones.ToDictionary(p => p.id, p => p.version, StringComparer.Ordinal);
            var chains = Chains(current).ToDictionary(c => c.id, StringComparer.Ordinal);
            var chainTombstones = ChainTombstones(current).ToDictionary(c => c.id, c => c.version, StringComparer.Ordinal);
            var assets = Assets(current).ToDictionary(a => a.hash, StringComparer.Ordinal);
            foreach (var write in assetWrites) {
                ValidateAsset(write);
                Require(!assets.ContainsKey(write.hash), "asset-already-exists");
                assets.Add(write.hash, WireJson.Clone(write));
            }
            var expected = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var dependency in tx.expected) {
                Require(dependency != null && ValidId(dependency.id) && !expected.ContainsKey(dependency.id), "invalid-dependency");
                expected.Add(dependency.id, dependency.version);
                var actual = parts.TryGetValue(dependency.id, out var part) ? part.version :
                    tombstones.TryGetValue(dependency.id, out var deleted) ? deleted : 0;
                Require(actual == dependency.version, "stale-entity");
            }
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var created = new HashSet<string>(StringComparer.Ordinal);
            foreach (var write in tx.writes) {
                ValidatePart(write, catalog);
                Require(!IsCustom(write) || assets.ContainsKey(write.assetHash), "missing-asset");
                Require(changed.Add(write.id) && expected.ContainsKey(write.id), "missing-write-version");
                bool exists = parts.TryGetValue(write.id, out var before);
                if (tx.kind == "AddParts") {
                    Require(!exists && !tombstones.ContainsKey(write.id) && !chains.ContainsKey(write.id) && !chainTombstones.ContainsKey(write.id) &&
                        expected[write.id] == 0 && tx.deletes.Length == 0, "identity-in-use");
                }
                else if (tx.kind == "RestoreParts") {
                    Require(exists || tombstones.ContainsKey(write.id), "missing-entity");
                }
                else if (tx.kind == "TransformAndCopy" && !exists) {
                    Require(!tombstones.ContainsKey(write.id) && !chains.ContainsKey(write.id) && !chainTombstones.ContainsKey(write.id) &&
                        expected[write.id] == 0 && tx.deletes.Length == 0, "identity-in-use");
                }
                else {
                    Require(exists && tx.kind != "DeleteParts" && tx.deletes.Length == 0, "missing-entity");
                    if (tx.kind == "SetCustomMounts")
                        Require(IsCustom(before) && IsCustom(write) && SameExceptMounts(before, write), "mounts-only");
                    else if (tx.kind == "SetParts")
                        Require(IsCustom(before) == IsCustom(write) && (!IsCustom(before) || SameCustom(before, write)), "custom-content-immutable");
                    else Require(before.catalogId == write.catalogId && before.state == write.state && before.hasPaint == write.hasPaint &&
                        before.color.SequenceEqual(write.color) && SameCustom(before, write), "pose-only");
                }
                var accepted = WireJson.Clone(write);
                accepted.version = exists ? before.version + 1 : tombstones.TryGetValue(write.id, out var dead) ? dead + 1 : 1;
                // Canonical signs and negative zero are normalized by the authority.
                accepted.rotation = NormalizeRotation(accepted.rotation);
                accepted.mounts = Mounts(accepted).Select(NormalizeMount).ToArray();
                parts[accepted.id] = accepted;
                tombstones.Remove(accepted.id);
                if (!exists) created.Add(accepted.id);
            }
            foreach (var id in tx.deletes) {
                Require((tx.kind == "RestoreParts" || tx.kind == "DeleteParts" && tx.writes.Length == 0) && ValidId(id) && changed.Add(id) &&
                    expected.ContainsKey(id) && parts.ContainsKey(id), "invalid-delete");
                tombstones[id] = parts[id].version + 1;
                parts.Remove(id);
            }
            var chainExpected = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var dependency in expectedChains) {
                Require(dependency != null && ValidId(dependency.id) && !chainExpected.ContainsKey(dependency.id), "invalid-chain-dependency");
                chainExpected.Add(dependency.id, dependency.version);
                var actual = chains.TryGetValue(dependency.id, out var chain) ? chain.version :
                    chainTombstones.TryGetValue(dependency.id, out var deleted) ? deleted : 0;
                Require(actual == dependency.version, "stale-chain");
            }
            var chainMemberDependencies = chainWrites.SelectMany(c => c?.members ?? Array.Empty<string>())
                .Concat(chainWrites.Where(c => c != null && chains.ContainsKey(c.id)).SelectMany(c => chains[c.id].members))
                .Concat(chainDeletes.Where(chains.ContainsKey).SelectMany(id => chains[id].members)).Distinct(StringComparer.Ordinal).ToArray();
            Require(chainMemberDependencies.All(expected.ContainsKey), "missing-chain-member-version");
            var changedChains = new HashSet<string>(StringComparer.Ordinal);
            foreach (var write in chainWrites) {
                ValidateChain(write, parts.Values);
                Require(changedChains.Add(write.id) && chainExpected.ContainsKey(write.id) && !parts.ContainsKey(write.id) && !tombstones.ContainsKey(write.id),
                    "missing-chain-version");
                bool exists = chains.TryGetValue(write.id, out var before);
                if (tx.kind == "AddParts" || tx.kind == "TransformAndCopy")
                    Require(!exists && write.members.All(created.Contains), "invalid-chain-mutation");
                if (tx.kind == "RestoreParts") Require(exists || chainTombstones.ContainsKey(write.id), "missing-chain");
                else if (!exists) Require(!chainTombstones.ContainsKey(write.id) && chainExpected[write.id] == 0, "chain-identity-in-use");
                var accepted = WireJson.Clone(write);
                accepted.version = exists ? before.version + 1 : chainTombstones.TryGetValue(write.id, out var dead) ? dead + 1 : 1;
                accepted.members = accepted.members.OrderBy(id => id, StringComparer.Ordinal).ToArray();
                chains[accepted.id] = accepted; chainTombstones.Remove(accepted.id);
            }
            foreach (var id in chainDeletes) {
                Require((tx.kind == "RestoreParts" || tx.kind == "SetChains" || tx.kind == "DeleteParts") && ValidId(id) && changedChains.Add(id) &&
                    chainExpected.ContainsKey(id) && chains.ContainsKey(id), "invalid-chain-delete");
                chainTombstones[id] = chains[id].version + 1; chains.Remove(id);
            }
            Require(chains.Values.All(chain => chain.members.All(parts.ContainsKey)), "missing-chain-member");
            Require(chains.Values.Select(ChainTopology).Distinct(StringComparer.Ordinal).Count() == chains.Count, "duplicate-chain");
            Require(parts.Count <= MaxParts && tombstones.Count <= 20000 && chains.Count <= MaxChains && chainTombstones.Count <= 20000 &&
                assets.Count <= MaxAssets, "document-limit");
            var next = new Document {
                schema = current.schema, generation = current.generation, catalogHash = current.catalogHash,
                revision = checked(current.revision + 1), parts = parts.Values.OrderBy(p => p.id, StringComparer.Ordinal).ToArray(),
                tombstones = tombstones.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new VersionRef { id = p.Key, version = p.Value }).ToArray(),
                chains = chains.Values.OrderBy(c => c.id, StringComparer.Ordinal).ToArray(),
                chainTombstones = chainTombstones.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new VersionRef { id = c.Key, version = c.Value }).ToArray(),
                assets = assets.Values.OrderBy(a => a.hash, StringComparer.Ordinal).ToArray()
            };
            Require(WireJson.Encode(next).Length <= 2 * 1024 * 1024, "snapshot-byte-limit");
            return next;
        }

        // History compares logical content; accepted entity versions always increase, including resurrection.
        public static bool SamePart(Part a, Part b) => a == null || b == null ? a == b :
            a.id == b.id && a.catalogId == b.catalogId && (a.state ?? "") == (b.state ?? "") && a.hasPaint == b.hasPaint &&
            a.position.SequenceEqual(b.position) && NormalizeRotation(a.rotation).SequenceEqual(NormalizeRotation(b.rotation)) && a.color.SequenceEqual(b.color) &&
            SameCustom(a, b);

        public static bool SameChain(ChainRecord a, ChainRecord b) => a == null || b == null ? a == b :
            a.id == b.id && (a.members ?? Array.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual((b.members ?? Array.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal));

        private static string ChainTopology(ChainRecord chain) => string.Join("\0",
            (chain.members ?? Array.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal));

        public static void ValidateChain(ChainRecord chain, IEnumerable<Part> parts) {
            Require(chain != null && ValidId(chain.id) && chain.members != null && chain.members.Length >= 2 &&
                chain.members.Length <= MaxWriteSet && chain.members.All(ValidId) && chain.members.Distinct(StringComparer.Ordinal).Count() == chain.members.Length,
                "invalid-chain");
            var available = new HashSet<string>(parts.Select(p => p.id), StringComparer.Ordinal);
            Require(chain.members.All(available.Contains), "missing-chain-member");
        }

        public static void ValidatePart(Part part, CatalogManifest catalog) {
            Require(part != null && ValidId(part.id) && part.catalogId != null && part.catalogId.Length <= 200 &&
                (part.state == null || part.state.Length <= 4096), "invalid-part");
            Require(part.position != null && part.position.Length == 3 && part.position.All(v => Finite(v) && Math.Abs(v) <= 1000000), "invalid-position");
            Require(part.rotation != null && part.rotation.Length == 4 && part.rotation.All(Finite) &&
                Math.Abs(part.rotation.Sum(v => v * v) - 1) < 0.0001, "invalid-rotation");
            Require(part.color != null && part.color.Length == 3 && part.color.All(v => Finite(v) && v >= 0 && v <= 1), "invalid-color");
            if (IsCustom(part)) {
                Require(ValidAssetHash(part.assetHash) && Mounts(part).Length <= MaxMountsPerPart, "invalid-custom-part");
                foreach (var mount in Mounts(part)) ValidateMount(mount);
                return;
            }
            Require(string.IsNullOrEmpty(part.assetHash) && Mounts(part).Length == 0, "invalid-catalog-part");
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

        public static void ValidateAsset(GeometryAsset asset) {
            Require(asset != null && ValidAssetHash(asset.hash) && asset.name != null && asset.name.Length >= 1 && asset.name.Length <= 200 &&
                asset.vertices != null && asset.vertices.Length >= 9 && asset.vertices.Length % 3 == 0 && asset.vertices.Length / 3 <= 1500000 &&
                asset.triangles != null && asset.triangles.Length >= 3 && asset.triangles.Length % 3 == 0 && asset.triangles.Length / 3 <= 500000,
                "invalid-asset");
            Require(asset.vertices.All(v => Finite(v) && Math.Abs(v) <= 1000000) &&
                asset.triangles.All(i => i >= 0 && i < asset.vertices.Length / 3), "invalid-asset");
            Require(GeometryHash(asset) == asset.hash, "asset-hash-mismatch");
            // Geometry is currently carried once in the bounded document snapshot.
            // A later asset endpoint can raise this without changing part references.
            Require(WireJson.Encode(asset).Length <= 1536 * 1024, "asset-byte-limit");
        }

        public static void ValidateMount(CustomMount mount) {
            Require(mount != null && mount.position != null && mount.position.Length == 3 &&
                mount.position.All(v => Finite(v) && Math.Abs(v) <= 1000000) && mount.rotation != null && mount.rotation.Length == 4 &&
                mount.rotation.All(Finite) && Math.Abs(mount.rotation.Sum(v => v * v) - 1) < 0.0001 &&
                Finite(mount.diameter) && Finite(mount.depth) && mount.diameter > 0 && mount.diameter <= 100 && mount.depth > 0 && mount.depth <= 100,
                "invalid-custom-mount");
        }

        public static string GeometryHash(GeometryAsset asset) {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) {
                    writer.Write(asset?.name ?? "");
                    var vertices = asset?.vertices ?? Array.Empty<double>(); writer.Write(vertices.Length);
                    foreach (var value in vertices) writer.Write(value == 0 ? 0f : (float)value);
                    var triangles = asset?.triangles ?? Array.Empty<int>(); writer.Write(triangles.Length);
                    foreach (var value in triangles) writer.Write(value);
                }
                using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream.ToArray()));
            }
        }

        private static bool SameCustom(Part a, Part b) => (a.assetHash ?? "") == (b.assetHash ?? "") && Mounts(a).Length == Mounts(b).Length &&
            Mounts(a).Zip(Mounts(b), SameMount).All(equal => equal);
        private static bool SameExceptMounts(Part a, Part b) => a.id == b.id && a.catalogId == b.catalogId && (a.state ?? "") == (b.state ?? "") &&
            a.hasPaint == b.hasPaint && a.position.SequenceEqual(b.position) && NormalizeRotation(a.rotation).SequenceEqual(NormalizeRotation(b.rotation)) &&
            a.color.SequenceEqual(b.color) && (a.assetHash ?? "") == (b.assetHash ?? "");
        private static bool SameMount(CustomMount a, CustomMount b) => a != null && b != null && a.position.SequenceEqual(b.position) &&
            NormalizeRotation(a.rotation).SequenceEqual(NormalizeRotation(b.rotation)) && a.diameter == b.diameter && a.depth == b.depth && a.threaded == b.threaded;
        private static CustomMount NormalizeMount(CustomMount mount) { var copy = WireJson.Clone(mount); copy.rotation = NormalizeRotation(copy.rotation); return copy; }
        private static bool IsCustom(Part part) => part?.catalogId == "CUSTOM";
        private static bool ValidAssetHash(string hash) => hash != null && hash.Length == 64 && hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        private static void ValidateParameter(ParameterRule rule, string value) {
            if (rule.custom) {
                Require(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    Finite(number) && number >= rule.min && number <= rule.max && (!rule.integer || number == Math.Round(number)), "invalid-parameter");
            }
            else Require(rule.options.Contains(value, StringComparer.Ordinal), "invalid-parameter");
        }

        public static ChainRecord[] Chains(Document document) => document?.chains ?? Array.Empty<ChainRecord>();
        public static VersionRef[] ChainTombstones(Document document) => document?.chainTombstones ?? Array.Empty<VersionRef>();
        public static ChainRecord[] ChainWrites(Transaction transaction) => transaction?.chainWrites ?? Array.Empty<ChainRecord>();
        public static string[] ChainDeletes(Transaction transaction) => transaction?.chainDeletes ?? Array.Empty<string>();
        public static VersionRef[] ExpectedChains(Transaction transaction) => transaction?.expectedChains ?? Array.Empty<VersionRef>();
        public static GeometryAsset[] Assets(Document document) => document?.assets ?? Array.Empty<GeometryAsset>();
        public static GeometryAsset[] AssetWrites(Transaction transaction) => transaction?.assetWrites ?? Array.Empty<GeometryAsset>();
        public static CustomMount[] Mounts(Part part) => part?.mounts ?? Array.Empty<CustomMount>();

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
                        writer.Write(part.assetHash ?? ""); writer.Write(Mounts(part).Length);
                        foreach (var mount in Mounts(part)) {
                            foreach (var n in mount.position.Concat(NormalizeRotation(mount.rotation))) writer.Write(n == 0 ? 0.0 : n);
                            writer.Write(mount.diameter == 0 ? 0.0 : mount.diameter); writer.Write(mount.depth == 0 ? 0.0 : mount.depth); writer.Write(mount.threaded);
                        }
                    }
                    writer.Write(document.tombstones.Length);
                    foreach (var dead in document.tombstones.OrderBy(p => p.id, StringComparer.Ordinal)) { writer.Write(dead.id); writer.Write(dead.version); }
                    writer.Write(Chains(document).Length);
                    foreach (var chain in Chains(document).OrderBy(c => c.id, StringComparer.Ordinal)) {
                        writer.Write(chain.id); writer.Write(chain.version);
                        var members = chain.members.OrderBy(id => id, StringComparer.Ordinal).ToArray(); writer.Write(members.Length);
                        foreach (var member in members) writer.Write(member);
                    }
                    writer.Write(ChainTombstones(document).Length);
                    foreach (var dead in ChainTombstones(document).OrderBy(c => c.id, StringComparer.Ordinal)) { writer.Write(dead.id); writer.Write(dead.version); }
                    writer.Write(Assets(document).Length);
                    foreach (var asset in Assets(document).OrderBy(a => a.hash, StringComparer.Ordinal)) {
                        writer.Write(asset.hash); writer.Write(asset.name); writer.Write(asset.vertices.Length);
                        foreach (var n in asset.vertices) writer.Write(n == 0 ? 0.0 : n);
                        writer.Write(asset.triangles.Length); foreach (var index in asset.triangles) writer.Write(index);
                    }
                }
                using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream.ToArray()));
            }
        }

        // 0.4 durable rooms included chains but predated document geometry.
        public static string LegacyAssetHash(Document document) {
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
                    writer.Write(Chains(document).Length);
                    foreach (var chain in Chains(document).OrderBy(c => c.id, StringComparer.Ordinal)) {
                        writer.Write(chain.id); writer.Write(chain.version);
                        var members = chain.members.OrderBy(id => id, StringComparer.Ordinal).ToArray(); writer.Write(members.Length);
                        foreach (var member in members) writer.Write(member);
                    }
                    writer.Write(ChainTombstones(document).Length);
                    foreach (var dead in ChainTombstones(document).OrderBy(c => c.id, StringComparer.Ordinal)) { writer.Write(dead.id); writer.Write(dead.version); }
                }
                using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream.ToArray()));
            }
        }

        // 0.3 durable rooms did not have chain fields. RoomStore uses this only
        // while replaying an old, chain-free checkpoint/journal into 0.4.
        public static string LegacyHash(Document document) {
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
