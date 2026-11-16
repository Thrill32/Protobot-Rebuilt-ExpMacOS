using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace Protobot.Collaboration {
    // Asset JSON and hydrated local files have independent bounds. Ordinary messages stay at 4 MiB.
    public static class GeometryTransfer {
        public const int MaxAssetBytes = 8 * 1024 * 1024;
        public const int MaxRoomBytes = 64 * 1024 * 1024;
        public const int MaxSceneGeometryBytes = 128 * 1024 * 1024;
        public const int MaxMaterializedBytes = 128 * 1024 * 1024;
        public const int MaxUnusedBytes = 16 * 1024 * 1024;
        public const int MaxUnusedAssets = 16;
        public const int BufferBytes = 64 * 1024;
        public static bool HasGeometry(GeometryAsset asset) => asset?.vertices != null && asset.vertices.Length > 0;
        public static GeometryAsset Reference(GeometryAsset asset) => new GeometryAsset { hash = asset.hash, name = asset.name };
        public static void ValidateReference(GeometryAsset asset) {
            DocumentModel.Require(asset != null && DocumentModel.ValidAssetHash(asset.hash) && asset.name != null && asset.name.Length >= 1 &&
                asset.name.Length <= 200 && asset.vertices != null && asset.vertices.Length == 0 && asset.triangles != null && asset.triangles.Length == 0, "invalid-asset-reference");
        }
        public static long MaterializedBytes(GeometryAsset asset) => (long)(asset.vertices?.Length ?? 0) * sizeof(double) + (long)(asset.triangles?.Length ?? 0) * sizeof(int);
        public static Document CloneDocument(Document document) {
            var assets = DocumentModel.Assets(document);
            DocumentModel.Require(assets.Sum(MaterializedBytes) <= MaxMaterializedBytes, "asset-materialization-quota");
            var copy = WireJson.Clone(References(document));
            copy.assets = assets.Select(asset => new GeometryAsset { hash = asset.hash, name = asset.name, vertices = asset.vertices.ToArray(), triangles = asset.triangles.ToArray() }).ToArray();
            return copy;
        }
        public static Document References(Document document) {
            // Geometry arrays are immutable; clone only the bounded metadata here.
            var assets = document.assets;
            var copy = new Document { schema = document.schema, generation = document.generation, catalogHash = document.catalogHash, revision = document.revision,
                parts = document.parts, tombstones = document.tombstones, chains = document.chains, chainTombstones = document.chainTombstones,
                assets = (assets ?? Array.Empty<GeometryAsset>()).Select(Reference).ToArray() };
            return copy;
        }
        public static byte[] Encode(GeometryAsset asset) {
            DocumentModel.ValidateAsset(asset);
            // The established geometry hash uses IEEE float coordinates. Store exactly those values.
            var canonical = new GeometryAsset { hash = asset.hash, name = asset.name, vertices = asset.vertices.Select(v => v == 0 ? 0.0 : (double)(float)v).ToArray(), triangles = asset.triangles };
            return LocalJson.Encode(canonical, MaxAssetBytes);
        }
        public static GeometryAsset Decode(byte[] bytes) {
            var asset = LocalJson.Decode<GeometryAsset>(bytes, MaxAssetBytes);
            DocumentModel.ValidateAsset(asset); return asset;
        }
        // Count each live instance, even when its source asset is deduplicated. This also bounds accepted build-file expansion.
        public static void RequireSceneBudget(Document document, Func<string, long> arrayBytes) => DocumentModel.Require(
            document.parts.Where(p => p.catalogId == "CUSTOM").Sum(p => arrayBytes(p.assetHash)) <= MaxSceneGeometryBytes, "asset-scene-quota");
        public static void RequireHydrated(Document document) {
            DocumentModel.Require(DocumentModel.Assets(document).Sum(MaterializedBytes) <= MaxMaterializedBytes, "asset-materialization-quota");
            DocumentModel.Require(DocumentModel.Assets(document).All(HasGeometry), "missing-asset");
            var assets = DocumentModel.Assets(document).ToDictionary(a => a.hash);
            RequireSceneBudget(document, hash => MaterializedBytes(assets[hash]));
            DocumentModel.Require(DocumentModel.Assets(document).Sum(a => (long)Encode(a).Length) <= MaxRoomBytes, "asset-room-quota");
        }
    }
    public static class LocalJson {
        // 64 MiB asset budget + ordinary metadata/catalog overhead. Never used for event decoding.
        public const int MaxBytes = GeometryTransfer.MaxRoomBytes + 8 * 1024 * 1024;
        public static byte[] Encode<T>(T value, int maximum = MaxBytes) {
            using (var stream = new LimitedStream(maximum)) {
                Serializer<T>().WriteObject(stream, value); return stream.ToArray();
            }
        }
        public static T Decode<T>(byte[] bytes, int maximum = MaxBytes) {
            DocumentModel.Require(bytes != null && bytes.Length <= maximum, "local-serialization-limit");
            using (var stream = new MemoryStream(bytes)) return (T)Serializer<T>().ReadObject(stream);
        }
        public static T Clone<T>(T value) => Decode<T>(Encode(value));
        private static DataContractJsonSerializer Serializer<T>() => new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = int.MaxValue });
        private sealed class LimitedStream : MemoryStream {
            private readonly int maximum;
            public LimitedStream(int limit) { maximum = limit; }
            public override void Write(byte[] buffer, int offset, int count) {
                DocumentModel.Require(Length + count <= maximum, "local-serialization-limit"); base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value) { DocumentModel.Require(Length < maximum, "local-serialization-limit"); base.WriteByte(value); }
        }
    }
}
