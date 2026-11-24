using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task LargeEditChecks(string directory) {
        var seed = Fixture();
        seed.document.parts = Enumerable.Range(0, DocumentModel.MaxReservedParts + 1).Select(i => {
            var part = Cube($"large-{i:D4}", i);
            part.version = 1;
            return part;
        }).ToArray();
        var ids = seed.document.parts.Select(part => part.id).ToArray();
        seed.document.chains = Enumerable.Range(0, 3).Select(i => new ChainRecord {
            id = $"route-{i}", version = 1, members = new[] { ids[i * 2], ids[i * 2 + 1] }
        }).ToArray();
        var storage = Path.Combine(directory, "large-edits");
        string url, authorId, acceptedHash;
        string recovery = Path.Combine(directory, "large-edit-author", "session.json");
        await using (var server = new CollaborationServer(seed, dataDirectory: storage)) {
            await server.StartAsync();
            url = server.Url;
            Reject(() => server.Room.Join(new JoinRequest { name = "Old player", catalogHash = seed.catalog.hash, largeEditsVersion = 0 }),
                "large-edits-client-required");
            using var editor = new CollaborationClient(url, seed.room, seed.catalog, CollaborationTransport.WebSocket, recoveryFile: recovery);
            using var peer = new CollaborationClient(url, seed.room, seed.catalog, CollaborationTransport.LongPolling);
            await editor.JoinAsync("Large editor");
            await peer.JoinAsync("Observer");
            authorId = editor.Participant.id;
            Check(editor.ReservationPartLimit == DocumentModel.MaxReservedParts && peer.TransactionMutationLimit == DocumentModel.MaxMutations,
                "clients negotiate the separate reservation and mutation limits");
            await RejectAsync(() => editor.ReserveAsync(ids), "reservation-part-limit");
            await RejectAsync(() => editor.ReserveAsync(new[] { ids[0], ids[0] }), "invalid-reservation");

            var leased = ids[..^1];
            var lease = await editor.ReserveAsync(leased);
            Check((await editor.ReserveAsync(leased.Reverse().ToArray(), lease.token)).token == lease.token, "renew the exact max-sized scope");
            await RejectAsync(() => peer.ReserveAsync(new[] { leased[^1], ids[^1] }), "parts-reserved");
            var independent = await peer.ReserveAsync(new[] { ids[^1] });
            await peer.ReleaseAsync(independent.token);
            var poses = leased.Select(id => new Part { id = id, position = new[] { -999999.999123, 999999.999123, .00000000123 } }).ToArray();
            var preview = new Preview { lease = lease.token, sequence = 1, parts = poses };
            await RejectAsync(() => editor.PreviewAsync(new Preview { lease = lease.token, sequence = 1, parts = poses[..^1] }),
                "preview-set-mismatch");
            var invalid = WireJson.Clone(preview);
            invalid.parts[^1].position[0] = 1000001;
            await RejectAsync(() => editor.PreviewAsync(invalid), "invalid-position");
            var presence = new TaskCompletionSource<EventBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Presence += batch => {
                if (batch.previews.Any(p => p.author == authorId && p.sequence == 1)) presence.TrySetResult(batch);
            };
            await editor.PreviewAsync(preview);
            var batch = await presence.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(batch.previews.Single().parts.Select(p => p.id).SequenceEqual(leased) && WireJson.Encode(batch).Length < 256 * 1024 &&
                WireJson.Encode(preview).Length < 256 * 1024 && peer.Snapshot.revision == 0,
                "max-sized compact preview crosses the wire without changing the accepted document");

            var moved = editor.Snapshot.parts.Where(part => leased.Contains(part.id)).ToArray();
            foreach (var part in moved) part.position[1] += 8;
            var receipt = await editor.SubmitAsync(editor.CreateTransaction("SetPoses", moved, lease: lease.token));
            Check(receipt.accepted && receipt.revision == 1, "512-part move produces a single accepted revision");
            await peer.WaitForRevisionAsync(1);
            Check(peer.Snapshot.parts.Count(part => part.position[1] == 8 && part.version == 2) == leased.Length,
                "the peer applies every covered part exactly once");
            Check((await editor.SubmitAsync(editor.CreateTransaction("Undo", Array.Empty<Part>()))).revision == 2 &&
                editor.Snapshot.parts.All(part => part.position[1] == 0), "one undo restores all 512 poses");
            Check((await editor.SubmitAsync(editor.CreateTransaction("Redo", Array.Empty<Part>()))).revision == 3 &&
                editor.Snapshot.parts.Count(part => part.position[1] == 8) == leased.Length, "one redo restores all 512 moved poses");

            // 511 source writes, 511 duplicate writes and two copied routes use one transaction budget.
            var scope = leased[..^1];
            lease = await editor.ReserveAsync(scope);
            var sources = editor.Snapshot.parts.Where(part => scope.Contains(part.id)).ToArray();
            var copies = sources.Select(part => { var copy = WireJson.Clone(part); copy.id = "copy-" + part.id; return copy; }).ToArray();
            foreach (var source in sources) source.position[1] += 2;
            var routes = seed.document.chains.Select(route => Chain("copy-" + route.id, route.members.Select(id => "copy-" + id).ToArray())).ToArray();
            var oversized = editor.CreateTransaction("TransformAndCopy", sources.Concat(copies).ToArray(), lease: lease.token, chainWrites: routes);
            var before = DocumentModel.Hash(editor.Snapshot);
            receipt = await editor.SubmitAsync(oversized);
            Check(DocumentModel.MutationCount(oversized) == DocumentModel.MaxMutations + 1 && !receipt.accepted &&
                receipt.error == "write-set-limit" && receipt.revision == 3 && DocumentModel.Hash(server.Room.Snapshot) == before,
                "a third route exceeds the mutation budget without changing the room or consuming the lease");
            var compound = editor.CreateTransaction("TransformAndCopy", sources.Concat(copies).ToArray(), lease: lease.token, chainWrites: routes[..^1]);
            receipt = await editor.SubmitAsync(compound);
            Check(DocumentModel.MutationCount(compound) == DocumentModel.MaxMutations && receipt.accepted && receipt.revision == 4 &&
                DocumentModel.Chains(editor.Snapshot).Length == 5, "1,024 combined mutations commit together");
            await peer.WaitForRevisionAsync(4);
            acceptedHash = DocumentModel.Hash(editor.Snapshot);
            Check(acceptedHash == DocumentModel.Hash(peer.Snapshot), "large transaction converges over both transports");
        }

        await using (var restarted = new CollaborationServer(null, listen: url, dataDirectory: storage)) {
            await restarted.StartAsync();
            using var editor = new CollaborationClient(url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, recoveryFile: recovery);
            await editor.JoinAsync("Recovered editor");
            Check(editor.Participant.id == authorId && DocumentModel.Hash(editor.Snapshot) == acceptedHash,
                "SQLite restart restores the accepted event and original editor");
            var undo = await editor.SubmitAsync(editor.CreateTransaction("Undo", Array.Empty<Part>()));
            Check(undo.accepted && undo.revision == 5 && editor.Snapshot.parts.Length == ids.Length &&
                DocumentModel.Chains(editor.Snapshot).Length == 3 && editor.Snapshot.parts.Count(part => part.position[1] == 8) == ids.Length - 1,
                "durable undo restores all originals, removes all copies and removes both copied routes");
            var redo = await editor.SubmitAsync(editor.CreateTransaction("Redo", Array.Empty<Part>()));
            Check(redo.accepted && redo.revision == 6 && editor.Snapshot.parts.Length == 2 * ids.Length - 2 &&
                DocumentModel.Chains(editor.Snapshot).Count(route => route.id.StartsWith("copy-", StringComparison.Ordinal)) == 2,
                "durable redo restores the entire compound edit in a single revision");
        }

        LargeHistoryEntryCheck(Path.Combine(directory, "large-history-entry"));
        LargeRouteEditCheck();
    }

    private static void LargeHistoryEntryCheck(string directory) {
        var seed = Fixture();
        seed.document.parts = Enumerable.Range(0, DocumentModel.MaxReservedParts).Select(i => {
            var part = Cube($"wide-{i:D4}".PadRight(80, 'x'));
            part.version = 1;
            return part;
        }).ToArray();
        int chars = (2 * 1024 * 1024 - WireJson.Encode(seed.document).Length - 2048) / seed.document.parts.Length;
        foreach (var part in seed.document.parts) part.state = new string('x', chars);
        string hash, resume;
        using (var store = new RoomStore(directory)) {
            var room = new Room(seed, storage: store);
            var editor = room.Join(new JoinRequest { name = "Large history", catalogHash = seed.catalog.hash });
            resume = editor.resumeToken;
            var poses = room.Snapshot.parts;
            var lease = room.Reserve(editor.token, new LeaseRequest { ids = poses.Select(part => part.id).ToArray() });
            foreach (var pose in poses) pose.position[0] = 1;
            var move = Tx(room.Snapshot, "SetPoses", poses, lease: lease.token);
            Check(room.Commit(editor.token, move).accepted, "a move near the snapshot byte ceiling fits on the wire");
            hash = DocumentModel.Hash(room.Snapshot);
        }
        using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "room.sqlite")};Mode=ReadOnly;Pooling=False")) {
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT length(data) FROM history";
            Check(Convert.ToInt64(query.ExecuteScalar()) > WireJson.MaxMessageBytes, "SQLite history can exceed one wire message");
        }
        using (var store = new RoomStore(directory)) {
            var room = new Room(null, storage: store);
            var editor = room.Join(new JoinRequest { name = "Large history", catalogHash = seed.catalog.hash, resumeToken = resume });
            Check(DocumentModel.Hash(room.Snapshot) == hash, "the larger history record reopens from its checkpoint");
            Check(room.Commit(editor.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 2)).accepted &&
                room.Snapshot.parts.All(part => part.position[0] == 0), "undo restores the large persistent history record");
        }
    }

    private static void LargeRouteEditCheck() {
        var seed = Fixture();
        seed.catalog.entries = seed.catalog.entries.Concat(new[] {
            new CatalogEntry { id = RopeRecord.DrumId }, new CatalogEntry { id = RopeRecord.PulleyId }
        }).ToArray();
        var drum = new Part { id = "drum", catalogId = RopeRecord.DrumId, version = 1 };
        Part[] Run(string prefix) => Enumerable.Range(1, DocumentModel.MaxRouteMembers - 1).Select(i => new Part {
            id = prefix + i, catalogId = i == DocumentModel.MaxRouteMembers - 1 ? "CUBE" : RopeRecord.PulleyId, version = 1
        }).ToArray();
        var oldRun = Run("old-"); var newRun = Run("new-");
        seed.document.parts = new[] { drum }.Concat(oldRun).Concat(newRun).ToArray();
        var route = new ChainRecord {
            id = "rope", version = 1, members = oldRun.Select(part => part.id).Prepend(drum.id).ToArray(),
            wraps = new int[DocumentModel.MaxRouteMembers - 1],
            rope = new RopeRecord { start = new float[3], end = new float[3], length = 48 }
        };
        seed.document.chains = new[] { route };
        var room = new Room(seed);
        var editor = room.Join(new JoinRequest { name = "Route editor", catalogHash = seed.catalog.hash });
        var changed = WireJson.Clone(route);
        changed.members = newRun.Select(part => part.id).Prepend(drum.id).ToArray();
        var union = route.members.Union(changed.members).ToArray();
        var lease = room.Reserve(editor.token, new LeaseRequest { ids = changed.members });
        Transaction Update(string token, long sequence) {
            var transaction = Tx(room.Snapshot, "SetChains", Array.Empty<Part>(), lease: token, sequence: sequence, chains: new[] { changed });
            transaction.expected = union.Select(id => new VersionRef { id = id, version = 1 }).ToArray();
            return transaction;
        }
        var incomplete = room.Commit(editor.token, Update(lease.token, 1));
        Check(!incomplete.accepted && incomplete.error == "lease-set-mismatch", "a rope edit must reserve the removed guides as well as the new guides");
        lease = room.Reserve(editor.token, new LeaseRequest { ids = union, token = lease.token });
        var edit = Update(lease.token, 2);
        Check(union.Length == 2 * DocumentModel.MaxRouteMembers - 1 && DocumentModel.MutationCount(edit) == 1 &&
            room.Commit(editor.token, edit).accepted, "replacing 511 distinct route endpoints uses a 511-part lease and one mutation");
        Check(!DocumentModel.ValidRoute(Enumerable.Range(0, DocumentModel.MaxRouteMembers + 1).Select(i => "sprocket-" + i).ToArray(), 0, null),
            "raising edit limits preserves the per-route member ceiling");
    }
}
