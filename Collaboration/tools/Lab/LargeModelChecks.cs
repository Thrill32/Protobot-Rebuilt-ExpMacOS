using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task LargeModelChecks(string directory) {
        var seed = Fixture();
        var room = new Room(seed);
        var oldWire = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Encode(new JoinRequest { catalogHash = seed.catalog.hash, name = "Old player" }));
        oldWire.AsObject().Remove("geometryLimitsVersion");
        var legacy = WireJson.Decode<JoinRequest>(System.Text.Encoding.UTF8.GetBytes(oldWire.ToJsonString()));
        Check(legacy.geometryLimitsVersion == 0, "absent client geometry capability is not inferred from a field initializer");
        Reject(() => room.Join(legacy), "geometry-limits-client-required");
        Check(GeometryTransfer.ServerAssetLimit(new Capabilities()) == GeometryTransfer.LegacyMaxAssetBytes,
            "older helpers retain their actual 8 MiB transfer limit");
        Reject(() => GeometryTransfer.ServerAssetLimit(new Capabilities { geometryLimitsVersion = 999 }), "geometry-limits-unsupported");
        Check(GeometryTransfer.ServerAssetLimit(room.Capabilities) == 64 * 1024 * 1024,
            "updated helpers advertise the bounded 64 MiB model capability");

        string source = Path.Combine(directory, "16.3 MB precision STL.stl");
        using (var stream = File.Create(source)) using (var writer = new BinaryWriter(stream)) {
            writer.Write(new byte[80]); writer.Write(326000u);
            for (int i = 0; i < 326000; i++) {
                writer.Write(0f); writer.Write(0f); writer.Write(1f);
                float shift = (i % 997) / 1024f;
                foreach (float value in new[] { -1f + shift, -1f + shift, shift, 1f + shift, -1f + shift, shift, shift, 1f + shift, shift }) writer.Write(value);
                writer.Write((ushort)0);
            }
        }
        var parsed = Protobot.CustomModelImporter.Read(source, 1);
        var asset = new GeometryAsset { name = parsed.name, vertices = parsed.vertices.Select(v => (double)v).ToArray(), triangles = parsed.triangles };
        asset.hash = DocumentModel.GeometryHash(asset);
        var bytes = GeometryTransfer.Encode(asset);
        Check(new FileInfo(source).Length == 16300084 && bytes.Length > 32 * 1024 * 1024 && bytes.Length <= GeometryTransfer.MaxAssetBytes,
            "valid 16.3 MB binary STL expands beyond 32 MiB but fits the new encoded model limit");
        Reject(() => GeometryTransfer.Encode(asset, GeometryTransfer.LegacyMaxAssetBytes), "local-serialization-limit");
        string storage = Path.Combine(directory, "large-model-room");
        JoinResponse joined;
        await using (var server = new CollaborationServer(seed, dataDirectory: storage)) {
            await server.StartAsync();
            using var alice = new CollaborationClient(server.Url, seed.room, seed.catalog, CollaborationTransport.WebSocket);
            using var bob = new CollaborationClient(server.Url, seed.room, seed.catalog, CollaborationTransport.LongPolling);
            joined = await alice.JoinAsync("Large model importer"); await bob.JoinAsync("Large model peer");
            Check(alice.GeometryAssetByteLimit == GeometryTransfer.MaxAssetBytes && bob.GeometryAssetByteLimit == GeometryTransfer.MaxAssetBytes,
                "independent clients negotiate the larger server limit before admission");
            await alice.UploadAssetAsync(asset);
            Check(alice.Snapshot.revision == 0, "large immutable upload fits the unused budget without publishing a part");
            Check((await alice.SubmitAsync(alice.CreateTransaction("AddParts", new[] { Custom("large-model", asset) },
                assetWrites: new[] { GeometryTransfer.Reference(asset) }))).accepted, "large uploaded geometry commits through a reference-only transaction");
            await bob.WaitForRevisionAsync(1);
            Check(DocumentModel.Hash(alice.Snapshot) == DocumentModel.Hash(bob.Snapshot) && bob.Snapshot.assets.Single().vertices.SequenceEqual(asset.vertices),
                "real WebSocket and request clients converge on the complete large geometry");
            var lease = await bob.ReserveAsync(new[] { "large-model" });
            var moved = WireJson.Clone(bob.Snapshot.parts.Single()); moved.position[1] = 3;
            Check((await bob.SubmitAsync(bob.CreateTransaction("SetPoses", new[] { moved }, lease: lease.token))).accepted,
                "the joining editor can lease and edit the large model");
            await bob.ReleaseAsync(lease.token); await alice.WaitForRevisionAsync(2);
            File.Delete(source);
            await alice.PrepareReconnectAsync(); await alice.JoinAsync("Resumed large importer");
            Check(alice.Participant.id == joined.participant.id && alice.Snapshot.parts.Single().position[1] == 3,
                "large geometry reconnect preserves membership and accepted editing after source deletion");
        }
        using (var store = new RoomStore(storage)) {
            var recovered = new Room(null, storage: store);
            var resumed = recovered.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = joined.resumeToken });
            Check(recovered.Snapshot.revision == 2 && recovered.Snapshot.parts.Single().position[1] == 3 &&
                GeometryTransfer.Decode(recovered.GetAsset(resumed.token, asset.hash)).vertices.SequenceEqual(asset.vertices),
                "SQLite restart retains the large encoded model, poses and original membership");
        }
        Console.WriteLine("Large model: source=16300084 encoded=" + bytes.Length + " decodedArrays=" + GeometryTransfer.MaterializedBytes(asset));
    }
}
