using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task ReplacementLargeChecks(string directory) {
        var source = LargeAsset("generation-old-0");
        int encoded = GeometryTransfer.Encode(source).Length;
        Check(encoded * 7L < GeometryTransfer.MaxRoomBytes && encoded * 14L > GeometryTransfer.MaxRoomBytes,
            "two independently valid generations exceed one cumulative geometry cache quota");
        GeometryAsset[] Assets(string generation) => Enumerable.Range(0, 7).Select(i => {
            var asset = new GeometryAsset { name = "generation-" + generation + "-" + i, vertices = source.vertices, triangles = source.triangles };
            asset.hash = DocumentModel.GeometryHash(asset); return asset;
        }).ToArray();
        var seed = HostedSeed("large-replacement"); seed.document.assets = Assets("old");
        seed.document.parts = seed.document.assets.Select((a, i) => { var part = Custom("old-" + i, a); part.version = 1; return part; }).ToArray();
        var next = new RoomExport { catalog = seed.catalog, document = new Document { generation = "large-source", catalogHash = seed.catalog.hash, assets = Assets("new") } };
        next.document.parts = next.document.assets.Select((a, i) => { var part = Custom("new-" + i, a); part.version = 1; return part; }).ToArray();
        await using var server = new CollaborationServer(seed, dataDirectory: Path.Combine(directory, "large-replacement")); await server.StartAsync();
        using var client = new CollaborationClient(server.Url, seed.room, seed.catalog, CollaborationTransport.LongPolling);
        await client.JoinAsync("Large generation", seed.password);
        string original = DocumentModel.Hash(client.Snapshot);
        using var remote = new RemoteRoomReplacement(server.Url);
        var target = await remote.InspectAsync(seed.room, seed.ownerCredential, CancellationToken.None);
        var replaced = await remote.ReplaceAsync(next, target, seed.ownerCredential, Path.Combine(directory, "large-remote-attempt.json"), CancellationToken.None);
        await client.PrepareReconnectAsync();
        using (var cancel = new CancellationTokenSource()) {
            var progress = new ReplacementProgress { OnReport = value => {
                if (value.Stage == ConnectionStage.DownloadingGeometry && value.CompletedItems >= 3) cancel.Cancel();
            } };
            await Canceled(client.JoinAsync("Canceled large resync", cancellation: cancel.Token, progress: progress, acceptReplacement: true));
        }
        var retained = client.Materialize(GeometryTransfer.References(client.Snapshot));
        Check(DocumentModel.Hash(client.Snapshot) == original && retained.assets.All(GeometryTransfer.HasGeometry),
            "canceling large candidate hydration preserves all geometry needed by the previous accepted generation");
        await client.JoinAsync("Complete large resync", acceptReplacement: true);
        Check(client.IsConnected && client.Snapshot.generation == replaced.generation && client.Snapshot.assets.Length == 7 &&
            client.Snapshot.assets.All(a => a.name.StartsWith("generation-new-", StringComparison.Ordinal) && GeometryTransfer.HasGeometry(a)),
            "a complete valid replacement hydrates without accumulating old-generation geometry against its quota");
    }
}
