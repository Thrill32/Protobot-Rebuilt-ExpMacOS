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
        public const int LegacyEditLimit = 256;
        public const int PreviousReservationPartLimit = 512;
        public const int PreviousTransactionMutationLimit = 1024;
        public const int MaxReservedParts = MaxParts;
        public const int MaxMutations = MaxParts + MaxChains + MaxAssets;
        public const int MaxRouteMembers = 256;
        public const int MaxEntityIdLength = 80;
        public const int MaxSnapshotBytes = 2 * 1024 * 1024;
        private const int MaxDependencies = 2 * MaxParts;

        public static void Validate(Document document, CatalogManifest catalog) {
            Require(document != null && catalog != null && (document.schema == 1 || document.schema == 2) && catalog.protocol == 1, "schema-mismatch");
            Require(ValidId(document.generation) && document.revision >= 0, "invalid-document");
            Require(!string.IsNullOrEmpty(catalog.hash) && document.catalogHash == catalog.hash, "catalog-mismatch");
            Require(document.parts != null && document.parts.Length <= MaxParts && document.tombstones != null &&
                Chains(document).Length <= MaxChains && Assets(document).Length <= MaxAssets, "document-limit");
            var assets = new Dictionary<string, GeometryAsset>(StringComparer.Ordinal);
            foreach (var asset in Assets(document)) {
                if (document.schema == 1 || GeometryTransfer.HasGeometry(asset)) ValidateAsset(asset); else GeometryTransfer.ValidateReference(asset);
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
            var routeParts = document.parts.ToDictionary(part => part.id, StringComparer.Ordinal);
            foreach (var chain in Chains(document)) {
                ValidateRouteRecord(chain, routeParts);
                Require(chain.version > 0 && ids.Add(chain.id), "duplicate-or-invalid-id");
            }
            Require(Chains(document).Select(ChainTopology).Distinct(StringComparer.Ordinal).Count() == Chains(document).Length,
                "duplicate-chain");
            foreach (var tombstone in ChainTombstones(document))
                Require(tombstone != null && ValidId(tombstone.id) && tombstone.version > 0 && ids.Add(tombstone.id), "invalid-chain-tombstone");
            Require(WireJson.Encode(document.schema == 2 ? GeometryTransfer.References(document) : document).Length <= MaxSnapshotBytes, "snapshot-byte-limit");
        }

        public static int MutationCount(Transaction tx) =>
            (tx.writes?.Length ?? 0) + (tx.deletes?.Length ?? 0) + ChainWrites(tx).Length + ChainDeletes(tx).Length + AssetWrites(tx).Length;

        public static Document Reduce(Document current, Transaction tx, CatalogManifest catalog) {
            Require(tx != null && tx.protocol == 1, "protocol-mismatch");
            Require(tx.generation == current.generation, "generation-mismatch");
            Require(!tx.expectedRevision.HasValue || tx.expectedRevision.Value == current.revision, "stale-document");
            Require(ValidId(tx.operationId) && tx.sequence > 0, "invalid-operation");
            var chainWrites = ChainWrites(tx); var chainDeletes = ChainDeletes(tx); var expectedChains = ExpectedChains(tx); var assetWrites = AssetWrites(tx);
            int mutations = MutationCount(tx);
            Require(tx.expected != null && tx.writes != null && tx.deletes != null && mutations > 0 && mutations <= MaxMutations &&
                tx.expected.Length <= MaxDependencies && expectedChains.Length <= MaxDependencies, "write-set-limit");
            Require(tx.kind == "AddParts" || tx.kind == "SetPoses" || tx.kind == "SetParts" || tx.kind == "SetCustomMounts" ||
                tx.kind == "DeleteParts" || tx.kind == "TransformAndCopy" || tx.kind == "SetChains" || tx.kind == "RestoreParts" ||
                tx.kind == "MirrorParts" || tx.kind == "MirrorAndCopy",
                "unsupported-mutation");
            bool addsParts = tx.kind == "AddParts" || tx.kind == "MirrorAndCopy";
            bool createsPartsAndChains = (addsParts || tx.kind == "TransformAndCopy") && tx.writes.Length > 0 && tx.deletes.Length == 0 &&
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
                if (current.schema == 1 || GeometryTransfer.HasGeometry(write)) ValidateAsset(write); else GeometryTransfer.ValidateReference(write);
                if (current.schema == 2 && !GeometryTransfer.HasGeometry(write) && assets.TryGetValue(write.hash, out var known)) {
                    Require(known.name == write.name, "asset-hash-mismatch"); continue;
                }
                Require(!assets.ContainsKey(write.hash), "asset-already-exists");
                assets.Add(write.hash, LocalJson.Clone(write));
            }
            var expected = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var dependency in tx.expected) {
                Require(dependency != null && ValidId(dependency.id) && !expected.ContainsKey(dependency.id), "invalid-dependency");
                expected.Add(dependency.id, dependency.version);
                var actual = parts.TryGetValue(dependency.id, out var part) ? part.version :
                    tombstones.TryGetValue(dependency.id, out var deleted) ? deleted : 0;
                Require(actual == dependency.version, "stale-entity");
            }
            if (tx.kind == "MirrorAndCopy")
                Require(expected.Keys.Count(parts.ContainsKey) == tx.writes.Length && tx.writes.Length > 0, "copy-map-mismatch");
            if (tx.kind == "MirrorParts")
                Require(tx.writes.Length > 0 && tx.deletes.Length == 0, "invalid-mirror");
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var created = new HashSet<string>(StringComparer.Ordinal);
            foreach (var write in tx.writes) {
                ValidatePart(write, catalog);
                Require(!IsCustom(write) || assets.ContainsKey(write.assetHash), "missing-asset");
                Require(changed.Add(write.id) && expected.ContainsKey(write.id), "missing-write-version");
                bool exists = parts.TryGetValue(write.id, out var before);
                if (addsParts) {
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
                        Require(before.mirrored == write.mirrored && IsCustom(before) == IsCustom(write) &&
                            (!IsCustom(before) || SameCustom(before, write)), "custom-content-immutable");
                    else Require(before.catalogId == write.catalogId && before.state == write.state && before.hasPaint == write.hasPaint &&
                        (tx.kind == "MirrorParts" ? before.mirrored != write.mirrored : before.mirrored == write.mirrored) &&
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
                ValidateRouteRecord(write, parts);
                Require(changedChains.Add(write.id) && chainExpected.ContainsKey(write.id) && !parts.ContainsKey(write.id) && !tombstones.ContainsKey(write.id),
                    "missing-chain-version");
                bool exists = chains.TryGetValue(write.id, out var before);
                Require(!exists || (before.rope == null) == (write.rope == null), "invalid-chain-mutation");
                if (addsParts || tx.kind == "TransformAndCopy")
                    Require(!exists && write.members.All(created.Contains), "invalid-chain-mutation");
                if (tx.kind == "RestoreParts") Require(exists || chainTombstones.ContainsKey(write.id), "missing-chain");
                else if (!exists) Require(!chainTombstones.ContainsKey(write.id) && chainExpected[write.id] == 0, "chain-identity-in-use");
                var accepted = WireJson.Clone(write);
                accepted.version = exists ? before.version + 1 : chainTombstones.TryGetValue(write.id, out var dead) ? dead + 1 : 1;
                NormalizeRoute(accepted);
                chains[accepted.id] = accepted; chainTombstones.Remove(accepted.id);
            }
            foreach (var id in chainDeletes) {
                Require((tx.kind == "RestoreParts" || tx.kind == "SetChains" || tx.kind == "DeleteParts") && ValidId(id) && changedChains.Add(id) &&
                    chainExpected.ContainsKey(id) && chains.ContainsKey(id), "invalid-chain-delete");
                chainTombstones[id] = chains[id].version + 1; chains.Remove(id);
            }
            Require(chains.Values.All(chain => chain.members.All(parts.ContainsKey)), "missing-chain-member");
            foreach (var rope in chains.Values.Where(chain => chain.rope != null)) ValidateRopeParts(rope, parts);
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
            Require(WireJson.Encode(next.schema == 2 ? GeometryTransfer.References(next) : next).Length <= MaxSnapshotBytes, "snapshot-byte-limit");
            return next;
        }

        // History compares logical content; accepted entity versions always increase, including resurrection.
        public static bool SamePart(Part a, Part b) => a == null || b == null ? a == b :
            a.id == b.id && a.catalogId == b.catalogId && (a.state ?? "") == (b.state ?? "") && a.hasPaint == b.hasPaint && a.mirrored == b.mirrored &&
            a.position.SequenceEqual(b.position) && NormalizeRotation(a.rotation).SequenceEqual(NormalizeRotation(b.rotation)) && a.color.SequenceEqual(b.color) &&
            SameCustom(a, b);

        public static bool SameChain(ChainRecord a, ChainRecord b) => a == null || b == null ? a == b :
            a.id == b.id && (a.rope == null && b.rope == null
                ? RouteKey(a.members, a.routeVersion, a.wraps) == RouteKey(b.members, b.routeVersion, b.wraps)
                : a.rope != null && b.rope != null && a.members.SequenceEqual(b.members) && a.wraps.SequenceEqual(b.wraps) &&
                    a.rope.length == b.rope.length && a.rope.start.SequenceEqual(b.rope.start) && a.rope.end.SequenceEqual(b.rope.end));

        public static bool ValidRoute(string[] members, int version, int[] wraps) =>
            members != null && members.Length >= 2 && members.Length <= MaxRouteMembers &&
            members.All(id => !string.IsNullOrEmpty(id)) && members.Distinct(StringComparer.Ordinal).Count() == members.Length &&
            (version == 0 && (wraps == null || wraps.Length == 0) ||
             version == 1 && wraps != null && wraps.Length == members.Length && wraps.All(w => w == 0 || w == 1));

        // Only cyclic rotations and reversal are equivalent. Wraps always move
        // with their member; sorting an explicit route would change its shape.
        public static void NormalizeRoute(ChainRecord chain) {
            if (chain.rope != null) return;
            if (chain.routeVersion == 0) { chain.members = chain.members.OrderBy(id => id, StringComparer.Ordinal).ToArray(); return; }
            int count = chain.members.Length;
            int start = Array.IndexOf(chain.members, chain.members.OrderBy(id => id, StringComparer.Ordinal).First());
            int step = string.CompareOrdinal(chain.members[(start + 1) % count], chain.members[(start + count - 1) % count]) <= 0 ? 1 : -1;
            var indices = Enumerable.Range(0, count).Select(i => (start + step * i + count) % count).ToArray();
            chain.members = indices.Select(i => chain.members[i]).ToArray();
            chain.wraps = indices.Select(i => chain.wraps[i]).ToArray();
        }
        public static string RouteKey(string[] members, int version, int[] wraps) {
            if (!ValidRoute(members, version, wraps)) return "invalid:" + version;
            var chain = new ChainRecord { members = members.ToArray(), routeVersion = version, wraps = wraps?.ToArray() };
            NormalizeRoute(chain);
            return version == 0 ? string.Join("|", chain.members) : "route1:" +
                string.Join("|", chain.members.Select((id, i) => id + ":" + chain.wraps[i]));
        }

        private static string ChainTopology(ChainRecord chain) => chain.rope != null ? "rope:" + chain.members[0] :
            string.Join("\0", chain.members.OrderBy(id => id, StringComparer.Ordinal));

        private static void ValidateRouteRecord(ChainRecord record, IReadOnlyDictionary<string, Part> parts) {
            ValidateRouteMetadata(record);
            Require(record.members.All(parts.ContainsKey), "missing-chain-member");
            if (record.rope != null) ValidateRopeParts(record, parts);
        }

        // Historical routes can refer to parts that are currently deleted.
        public static void ValidateRouteMetadata(ChainRecord record) {
            Require(record != null && ValidId(record.id) && record.members != null && record.members.Length >= 2 &&
                record.members.Length <= MaxRouteMembers && record.members.All(ValidId) &&
                record.members.Distinct(StringComparer.Ordinal).Count() == record.members.Length, "invalid-chain");
            var rope = record.rope;
            if (rope == null) {
                Require(ValidRoute(record.members, record.routeVersion, record.wraps), "invalid-chain-route");
                return;
            }
            Require(record.routeVersion == 0 && record.members.Length >= 3 &&
                record.wraps != null && record.wraps.Length == record.members.Length - 1 && record.wraps.All(w => w == 0 || w == 1) &&
                Finite(rope.length) && rope.length > 0 && ValidAttachment(rope.start) && ValidAttachment(rope.end), "invalid-rope-route");
        }

        private static void ValidateRopeParts(ChainRecord record, IReadOnlyDictionary<string, Part> parts) =>
            Require(parts[record.members[0]].catalogId == RopeRecord.DrumId &&
                record.members.Skip(1).Take(record.members.Length - 2).All(id => parts[id].catalogId == RopeRecord.PulleyId), "invalid-rope-route");

        private static bool ValidAttachment(float[] point) => point != null && point.Length == 3 && point.All(value => Finite(value));

        public static void ValidateChain(ChainRecord chain, IEnumerable<Part> parts) {
            ValidateRouteMetadata(chain);
            Require(chain.rope == null, "invalid-chain-route");
            var available = new HashSet<string>(parts.Select(p => p.id), StringComparer.Ordinal);
            Require(chain.members.All(available.Contains), "missing-chain-member");
        }

        public static void ValidatePose(Part part) {
            Require(part != null && ValidId(part.id), "invalid-part");
            Require(part.position != null && part.position.Length == 3 && part.position.All(v => Finite(v) && Math.Abs(v) <= 1000000), "invalid-position");
            Require(part.rotation != null && part.rotation.Length == 4 && part.rotation.All(Finite) &&
                Math.Abs(part.rotation.Sum(v => v * v) - 1) < 0.0001, "invalid-rotation");
        }

        public static void ValidatePart(Part part, CatalogManifest catalog) {
            Require(part != null && part.catalogId != null && part.catalogId.Length <= 200 &&
                (part.state == null || part.state.Length <= 4096), "invalid-part");
            ValidatePose(part);
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

        public static void ValidateAsset(GeometryAsset asset, int maximum = GeometryTransfer.MaxAssetBytes) {
            Require(maximum > 0 && maximum <= GeometryTransfer.MaxAssetBytes, "asset-byte-limit");
            Require(asset != null && ValidAssetHash(asset.hash) && asset.name != null && asset.name.Length >= 1 && asset.name.Length <= 200 &&
                asset.vertices != null && asset.vertices.Length >= 9 && asset.vertices.Length % 3 == 0 && asset.vertices.Length / 3 <= 1500000 &&
                asset.triangles != null && asset.triangles.Length >= 3 && asset.triangles.Length % 3 == 0 && asset.triangles.Length / 3 <= 500000,
                "invalid-asset");
            Require(asset.vertices.All(v => Finite(v) && Math.Abs(v) <= 1000000) &&
                asset.triangles.All(i => i >= 0 && i < asset.vertices.Length / 3), "invalid-asset");
            asset.appearance?.Validate(asset.vertices.Length / 3, asset.triangles.Length / 3);
            Require(GeometryHash(asset) == asset.hash, "asset-hash-mismatch");
            Require(LocalJson.Encode(asset, maximum).Length <= maximum, "asset-byte-limit");
        }

        public static void ValidateMount(CustomMount mount) {
            Require(mount != null && mount.position != null && mount.position.Length == 3 &&
                mount.position.All(v => Finite(v) && Math.Abs(v) <= 1000000) && mount.rotation != null && mount.rotation.Length == 4 &&
                mount.rotation.All(Finite) && Math.Abs(mount.rotation.Sum(v => v * v) - 1) < 0.0001 &&
                Finite(mount.diameter) && Finite(mount.depth) && mount.diameter > 0 && mount.diameter <= 100 && mount.depth > 0 && mount.depth <= 100,
                "invalid-custom-mount");
        }

        public static string GeometryHash(GeometryAsset asset) {
            using (var sha = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write)) {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) {
                    writer.Write(asset?.name ?? "");
                    var vertices = asset?.vertices ?? Array.Empty<double>(); writer.Write(vertices.Length);
                    foreach (var value in vertices) writer.Write(value == 0 ? 0f : (float)value);
                    var triangles = asset?.triangles ?? Array.Empty<int>(); writer.Write(triangles.Length);
                    foreach (var value in triangles) writer.Write(value);
                    asset?.appearance?.WriteHash(writer);
                }
                stream.FlushFinalBlock();
                return Hex(sha.Hash);
            }
        }

        private static bool SameCustom(Part a, Part b) => (a.assetHash ?? "") == (b.assetHash ?? "") && Mounts(a).Length == Mounts(b).Length &&
            Mounts(a).Zip(Mounts(b), SameMount).All(equal => equal);
        private static bool SameExceptMounts(Part a, Part b) => a.id == b.id && a.catalogId == b.catalogId && (a.state ?? "") == (b.state ?? "") &&
            a.hasPaint == b.hasPaint && a.mirrored == b.mirrored && a.position.SequenceEqual(b.position) && NormalizeRotation(a.rotation).SequenceEqual(NormalizeRotation(b.rotation)) &&
            a.color.SequenceEqual(b.color) && (a.assetHash ?? "") == (b.assetHash ?? "");
        private static bool SameMount(CustomMount a, CustomMount b) => a != null && b != null && a.position.SequenceEqual(b.position) &&
            NormalizeRotation(a.rotation).SequenceEqual(NormalizeRotation(b.rotation)) && a.diameter == b.diameter && a.depth == b.depth && a.threaded == b.threaded;
        private static CustomMount NormalizeMount(CustomMount mount) { var copy = WireJson.Clone(mount); copy.rotation = NormalizeRotation(copy.rotation); return copy; }
        private static bool IsCustom(Part part) => part?.catalogId == "CUSTOM";
        public static bool ValidAssetHash(string hash) => hash != null && hash.Length == 64 && hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

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
                        if (chain.rope != null) {
                            writer.Write("rope1");
                            foreach (var member in chain.members) writer.Write(member);
                            foreach (var wrap in chain.wraps) writer.Write(wrap);
                            foreach (var n in chain.rope.start.Concat(chain.rope.end).Append(chain.rope.length)) writer.Write(n == 0 ? 0f : n);
                        }
                        else if (chain.routeVersion != 0) writer.Write(RouteKey(chain.members, chain.routeVersion, chain.wraps));
                    }
                    writer.Write(ChainTombstones(document).Length);
                    foreach (var dead in ChainTombstones(document).OrderBy(c => c.id, StringComparer.Ordinal)) { writer.Write(dead.id); writer.Write(dead.version); }
                    writer.Write(Assets(document).Length);
                    foreach (var asset in Assets(document).OrderBy(a => a.hash, StringComparer.Ordinal)) {
                        writer.Write(asset.hash); writer.Write(asset.name);
                        if (document.schema == 2) continue;
                        writer.Write(asset.vertices.Length);
                        foreach (var n in asset.vertices) writer.Write(n == 0 ? 0.0 : n);
                        writer.Write(asset.triangles.Length); foreach (var index in asset.triangles) writer.Write(index);
                    }
                    // Keep existing unmirrored checkpoints and document identities stable.
                    var mirrored = document.parts.Where(part => part.mirrored).OrderBy(part => part.id, StringComparer.Ordinal).ToArray();
                    if (mirrored.Length > 0) {
                        writer.Write("mirror1");
                        writer.Write(mirrored.Length);
                        foreach (var part in mirrored) writer.Write(part.id);
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
        public static bool ValidId(string id) => id != null && id.Length >= 1 && id.Length <= MaxEntityIdLength && id.All(c =>
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
            DocumentModel.Validate(initial, manifest); document = GeometryTransfer.CloneDocument(initial); catalog = WireJson.Clone(manifest);
        }
        public Document Snapshot => GeometryTransfer.CloneDocument(document);
        public System.Threading.Tasks.Task<Receipt> SubmitAsync(Transaction transaction, System.Threading.CancellationToken cancellation = default) {
            cancellation.ThrowIfCancellationRequested();
            document = DocumentModel.Reduce(document, transaction, catalog);
            return System.Threading.Tasks.Task.FromResult(new Receipt { accepted = true, operationId = transaction.operationId, revision = document.revision });
        }
    }
}
