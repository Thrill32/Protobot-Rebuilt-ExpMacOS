using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task LargeEditChecks(string directory) {
        const int buildParts = 800;
        var seed = Fixture();
        seed.document.parts = Enumerable.Range(0, buildParts).Select(i => {
            var part = Cube($"large-{i:D4}", i);
            part.version = 1;
            return part;
        }).ToArray();
        var ids = seed.document.parts.Select(part => part.id).ToArray();
        seed.document.chains = new[] { new ChainRecord { id = "route", version = 1, members = ids[..2] } };
        var storage = Path.Combine(directory, "large-edits");
        string listen, url, authorId, acceptedHash;
        string recovery = Path.Combine(directory, "large-edit-author", "session.json");
        using var certificate = TlsTestCertificate();
        string pfx = Path.Combine(directory, "large-edits.pfx"), config = Path.Combine(directory, "large-edits-tls.json");
        TlsFile(pfx, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, TlsPassword));
        TlsText(config, TlsJson(Path.GetFileName(pfx)));
        using var tls = await TlsConfiguration.LoadAsync(config);
        await using (var server = new CollaborationServer(seed, "https://127.0.0.1:0", dataDirectory: storage, tls: tls)) {
            await server.StartAsync();
            listen = server.Url;
            url = new UriBuilder(listen) { Host = "localhost" }.Uri.GetLeftPart(UriPartial.Authority);
            Reject(() => server.Room.Join(new JoinRequest { name = "Old player", catalogHash = seed.catalog.hash,
                largeEditsVersion = CollaborationProtocol.PreviousLargeEditsVersion }), "large-edits-client-required");
            using var editor = new CollaborationClient(url, seed.room, seed.catalog, CollaborationTransport.WebSocket, recoveryFile: recovery);
            using var peer = new CollaborationClient(url, seed.room, seed.catalog, CollaborationTransport.LongPolling);
            TlsFixtureClient(editor, certificate);
            TlsFixtureClient(peer, certificate);
            await editor.JoinAsync("Large editor");
            await peer.JoinAsync("Observer");
            authorId = editor.Participant.id;
            Check(editor.ReservationPartLimit == DocumentModel.MaxParts && peer.TransactionMutationLimit == DocumentModel.MaxMutations,
                "TLS clients negotiate the whole-room reservation and separate mutation budgets");
            await LargeEditRequestChecks(server.Room, url, certificate, seed.catalog.hash);
            await RejectAsync(() => editor.ReserveAsync(new[] { ids[0], ids[0] }), "invalid-reservation");

            var lease = await editor.ReserveAsync(ids);
            Check((await editor.ReserveAsync(ids.Reverse().ToArray(), lease.token)).token == lease.token, "renew the whole robot reservation");
            await RejectAsync(() => peer.ReserveAsync(new[] { ids[^1] }), "parts-reserved");
            var poses = ids.Take(CollaborationProtocol.LargePreviewParts).Select(id => new Part {
                id = id, position = new[] { -999999.999123, 999999.999123, .00000000123 }
            }).ToArray();
            var preview = new Preview { lease = lease.token, sequence = 1, parts = poses };
            await RejectAsync(() => editor.PreviewAsync(new Preview { lease = lease.token, sequence = 1, parts = poses[..^1] }),
                "preview-set-mismatch");
            var invalid = WireJson.Clone(preview);
            invalid.parts[^1].id = "outside-reservation";
            await RejectAsync(() => editor.PreviewAsync(invalid), "preview-set-mismatch");
            invalid = WireJson.Clone(preview);
            invalid.parts[^1].position[0] = 1000001;
            await RejectAsync(() => editor.PreviewAsync(invalid), "invalid-position");
            var presence = new TaskCompletionSource<EventBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Presence += batch => {
                if (batch.previews.Any(p => p.author == authorId && p.sequence == 1)) presence.TrySetResult(batch);
            };
            await editor.PreviewAsync(preview);
            var batch = await presence.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(batch.previews.Single().parts.Select(p => p.id).SequenceEqual(poses.Select(p => p.id)) &&
                WireJson.Encode(batch).Length < CollaborationProtocol.MaxPreviewRequestBytes && peer.Snapshot.revision == 0,
                "the bounded 128-pose sample reaches a TLS peer without changing the accepted document");

            var moved = editor.Snapshot.parts;
            foreach (var part in moved) part.position[1] += 8;
            var oversized = editor.CreateTransaction("SetPoses", moved.Select(part => {
                var tooWide = WireJson.Clone(part); tooWide.state = new string('x', 4096); return tooWide;
            }).ToArray(), lease: lease.token);
            await RejectAsync(() => editor.SubmitAsync(oversized), "edit-byte-limit");
            Check(editor.IsConnected && editor.PendingCount == 0 && server.Room.Revision == 0,
                "size refusal before sending does not record a pending outcome or disconnect the author");

            var receipt = await editor.SubmitAsync(editor.CreateTransaction("SetPoses", moved, lease: lease.token));
            await peer.WaitForRevisionAsync(1);
            Check(receipt.accepted && receipt.revision == 1 && peer.Snapshot.parts.All(part => part.position[1] == 8 && part.version == 2),
                "one 800-part move reaches both TLS clients in one revision");
            Check((await editor.SubmitAsync(editor.CreateTransaction("Undo", Array.Empty<Part>()))).revision == 2 &&
                editor.Snapshot.parts.All(part => part.position[1] == 0), "one undo restores every pose");
            Check((await editor.SubmitAsync(editor.CreateTransaction("Redo", Array.Empty<Part>()))).revision == 3 &&
                editor.Snapshot.parts.All(part => part.position[1] == 8), "one redo restores every moved pose");

            lease = await editor.ReserveAsync(ids);
            var sources = editor.Snapshot.parts;
            var copies = sources.Select(part => { var copy = WireJson.Clone(part); copy.id = "copy-" + part.id; return copy; }).ToArray();
            foreach (var source in sources) source.position[1] += 2;
            var route = Chain("copy-route", seed.document.chains.Single().members.Select(id => "copy-" + id).ToArray());
            var compound = editor.CreateTransaction("TransformAndCopy", sources.Concat(copies).ToArray(), lease: lease.token, chainWrites: new[] { route });
            receipt = await editor.SubmitAsync(compound);
            Check(DocumentModel.MutationCount(compound) == 1601 && receipt.accepted && receipt.revision == 4 &&
                DocumentModel.Chains(editor.Snapshot).Length == 2, "800 originals, 800 copies and a remapped route commit together");
            await peer.WaitForRevisionAsync(4);
            acceptedHash = DocumentModel.Hash(editor.Snapshot);
            Check(acceptedHash == DocumentModel.Hash(peer.Snapshot), "large edit converges across secure WebSocket and held HTTPS");
        }

        await using (var restarted = new CollaborationServer(null, listen: listen, dataDirectory: storage, tls: tls)) {
            await restarted.StartAsync();
            using var editor = new CollaborationClient(url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, recoveryFile: recovery);
            TlsFixtureClient(editor, certificate);
            await editor.JoinAsync("Recovered editor");
            Check(editor.Participant.id == authorId && DocumentModel.Hash(editor.Snapshot) == acceptedHash,
                "SQLite restart restores the large copy, route and original membership");
            var undo = await editor.SubmitAsync(editor.CreateTransaction("Undo", Array.Empty<Part>()));
            Check(undo.accepted && undo.revision == 5 && editor.Snapshot.parts.Length == ids.Length &&
                DocumentModel.Chains(editor.Snapshot).Length == 1 && editor.Snapshot.parts.All(part => part.position[1] == 8),
                "durable undo restores every original and removes every copy and its route");
            var redo = await editor.SubmitAsync(editor.CreateTransaction("Redo", Array.Empty<Part>()));
            Check(redo.accepted && redo.revision == 6 && editor.Snapshot.parts.Length == 2 * ids.Length &&
                DocumentModel.Chains(editor.Snapshot).Any(route => route.id == "copy-route"),
                "durable redo restores the entire large copy in one revision");
        }

        await WholeRoomScopeCheck();
        WholeRoomMutationCountCheck();
        LargeHistoryEntryCheck(Path.Combine(directory, "large-history-entry"));
        LargeRouteEditCheck();
    }

    private static async Task LargeEditRequestChecks(Room room, string url, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, string catalog) {
        var editor = room.Join(new JoinRequest { name = "Raw editor", catalogHash = catalog });
        var viewer = room.Join(new JoinRequest { name = "Raw viewer", catalogHash = catalog, viewer = true });
        using var http = TlsFixtureHttp(certificate);
        async Task Reply(string endpoint, string token, int bytes, string expected) {
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "/rooms/" + room.Name + "/" + endpoint) {
                Content = new ByteArrayContent(new byte[bytes])
            };
            request.Headers.ExpectContinue = true;
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            var error = WireJson.Decode<WireError>(await response.Content.ReadAsByteArrayAsync());
            Check(!response.IsSuccessStatusCode && error.error == expected, endpoint + " rejects at the correct boundary");
        }
        await Reply("commit", null, CollaborationProtocol.MaxEditRequestBytes + 1, "invalid-session");
        await Reply("lease", editor.token, CollaborationProtocol.MaxLeaseRequestBytes + 1, "reservation-byte-limit");
        await Reply("preview", viewer.token, CollaborationProtocol.MaxPreviewRequestBytes + 1, "viewer-read-only");
        await Reply("commit", editor.token, CollaborationProtocol.MaxEditRequestBytes + 1, "edit-byte-limit");
        await Reply("preview", editor.token, CollaborationProtocol.MaxPreviewRequestBytes + 1, "preview-byte-limit");
        Check(room.Revision == 0, "rejected HTTP bodies leave the accepted room unchanged");
        room.Leave(editor.token);
        room.Leave(viewer.token);
    }

    private static async Task WholeRoomScopeCheck() {
        var seed = Fixture();
        seed.document.parts = Enumerable.Range(0, DocumentModel.MaxParts).Select(i => {
            var part = Cube($"full-{i:D4}"); part.version = 1; return part;
        }).ToArray();
        long now = 1000;
        var room = new Room(seed, monotonicMilliseconds: () => now);
        var editor = room.Join(new JoinRequest { name = "Full room editor", catalogHash = seed.catalog.hash });
        var ids = seed.document.parts.Select(part => part.id).ToArray();
        Reject(() => room.Reserve(editor.token, new LeaseRequest { ids = ids.Append("extra").ToArray() }), "reservation-part-limit");
        var lease = room.Reserve(editor.token, new LeaseRequest { ids = ids });
        var sample = ids.Take(CollaborationProtocol.LargePreviewParts).Select(id => new Part { id = id }).ToArray();
        var preview = new Preview { lease = lease.token, sequence = 1, parts = sample };
        room.Preview(editor.token, preview);
        preview.sequence = 2;
        now += CollaborationProtocol.PreviewIntervalMilliseconds(ids.Length) - 1;
        room.Preview(editor.token, preview);
        var early = await room.ReadAsync(editor.token, 0, -1, default, wait: false);
        Check(early.previews.Single().sequence == 1, "server drops over-frequency large preview samples");
        ++now;
        room.Preview(editor.token, preview);
        var onTime = await room.ReadAsync(editor.token, 0, -1, default, wait: false);
        Check(onTime.previews.Single().sequence == 2, "the next sample publishes at the exact cadence");
        now = 1000 + Room.LeaseMilliseconds - 1;
        var renewed = room.Reserve(editor.token, new LeaseRequest { ids = ids, token = lease.token });
        Check(renewed.token == lease.token, "renewal during a slow upload retains its original fencing token");
        now += 2; // The original lease would have expired before the commit arrived.
        var poses = room.Snapshot.parts;
        foreach (var part in poses) part.position[1] = 5;
        var receipt = room.Commit(editor.token, Tx(room.Snapshot, "SetPoses", poses, lease: lease.token));
        Check(receipt.accepted && receipt.revision == 1 && room.Snapshot.parts.All(part => part.position[1] == 5),
            "all 2,000 live parts can move atomically");
        var committed = await room.ReadAsync(editor.token, 0, -1, default, wait: false);
        Check(committed.edits.Single().transaction.writes.Length == ids.Length && committed.previews.Length == 0 &&
            WireJson.Encode(committed).Length < WireJson.MaxMessageBytes, "reliable full-room event fits the wire and clears the preview");
    }

    private static void WholeRoomMutationCountCheck() {
        var seed = Fixture();
        var assets = Enumerable.Range(0, DocumentModel.MaxAssets).Select(i => Asset("boundary geometry " + i)).ToArray();
        var parts = Enumerable.Range(0, DocumentModel.MaxParts).Select(i =>
            i < assets.Length ? Custom("part-" + i, assets[i]) : Cube("part-" + i)).ToArray();
        var routes = Enumerable.Range(0, DocumentModel.MaxChains).Select(i =>
            Chain("route-" + i, parts[i].id, parts[(i + 1) % parts.Length].id)).ToArray();
        var command = Tx(seed.document, "AddParts", parts, chains: routes, assets: assets.Select(GeometryTransfer.Reference).ToArray());
        Check(DocumentModel.MutationCount(command) == DocumentModel.MaxMutations &&
            DocumentModel.Reduce(seed.document, command, seed.catalog).parts.Length == DocumentModel.MaxParts,
            "the mutation budget admits all room parts, routes and new immutable references together");
        command.chainWrites = command.chainWrites.Append(Chain("extra", parts[0].id, parts[2].id)).ToArray();
        Reject(() => DocumentModel.Reduce(seed.document, command, seed.catalog), "write-set-limit");
    }

    private static void LargeHistoryEntryCheck(string directory) {
        var seed = Fixture();
        seed.document.parts = Enumerable.Range(0, DocumentModel.MaxReservedParts).Select(i => {
            var part = Cube($"wide-{i:D4}".PadRight(DocumentModel.MaxEntityIdLength, 'x'));
            part.version = 1;
            return part;
        }).ToArray();
        int chars = (DocumentModel.MaxSnapshotBytes - WireJson.Encode(seed.document).Length - 2048) / seed.document.parts.Length;
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
