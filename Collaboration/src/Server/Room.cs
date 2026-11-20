using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;

namespace Protobot.Collaboration.Server {
    // Every room transition is serialized under one gate, including snapshot/event handoff.
    public sealed partial class Room {
        private sealed class Member : StoredMember {
            public long lastSeen;
            public string accessHash, sessionGeneration;
            public Dictionary<string, Receipt> receipts = new Dictionary<string, Receipt>();
            public List<StoredHistory> history = new List<StoredHistory>();
            public StoredMember Record() => new StoredMember { participant = participant, resumeHash = resumeHash,
                sequence = sequence, historyCursor = historyCursor, revoked = revoked };
        }
        private sealed class Lease {
            public string token, author;
            public string[] ids;
            public long expires;
            public Preview preview;
        }
        private readonly object gate = new object();
        private readonly CatalogManifest catalog;
        private readonly byte[] salt = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] passwordHash;
        private readonly string ownerHash;
        private readonly Dictionary<string, Member> members = new Dictionary<string, Member>();
        private readonly Dictionary<string, Member> sessions = new Dictionary<string, Member>();
        private readonly Dictionary<string, Lease> leases = new Dictionary<string, Lease>();
        private readonly List<AcceptedEdit> events = new List<AcceptedEdit>();
        private TaskCompletionSource<bool> changed = Signal();
        private Document document;
        private StoredRoomInfo roomInfo;
        private long transientVersion;
        private string diagnosticHash;
        private long historyBytes;
        private int receiptCount;
        private readonly Func<long> clock;
        private readonly RoomStore store;
        private volatile bool storageFailed;
        private volatile bool stopping;
        private ProcessBudget processBudget;
        private long SceneBytes(Document value) => value.parts.Where(p => p.catalogId == "CUSTOM").Sum(p => assetArrayBytes[p.assetHash]);
        internal (long bytes, long arrays, long instances) GeometryUsage => (assetBytes.Values.Sum(b => (long)b.Length), assetArrayBytes.Values.Sum(), SceneBytes(document));
        internal void AdoptBudget(ProcessBudget budget) => processBudget = budget;
        internal void AttachBudget(ProcessBudget budget) {
            using var reservation = budget.Reserve(assetBytes.Values.Sum(b => (long)b.Length), assetArrayBytes.Values.Sum(), SceneBytes(document));
            processBudget = budget; reservation.Commit();
        }
        internal void DetachBudget() {
            if (processBudget == null) return;
            using var release = processBudget.Reserve(-assetBytes.Values.Sum(b => (long)b.Length), -assetArrayBytes.Values.Sum(), -SceneBytes(document));
            release.Commit(); processBudget = null;
        }
        public string Name { get; }
        public bool IsDurable => store != null;
        public bool IsHealthy => !storageFailed;
        public bool Recovered { get; }
        public long Revision { get { lock (gate) return document.revision; } }
        public Document Snapshot { get { lock (gate) return WireJson.Clone(document); } }
        public Capabilities Capabilities { get { lock (gate) return new Capabilities { durable = IsDurable, participantAdministration = true,
            geometryLimitsVersion = GeometryTransfer.LimitsVersion, geometryAssetBytes = GeometryTransfer.MaxAssetBytes,
                customAppearanceVersion = CustomAppearance.Version,
            roomReplacementVersion = IsDurable ? RoomReplacement.Version : 0, roomIdentity = RoomStore.Identity(roomInfo),
            editorRoomReplacementVersion = RoomReplacement.EditorPolicyVersion, allowEditorProjectReplacement = roomInfo.allowEditorProjectReplacement,
            diagnosticsVersion = DiagnosticExport.Version, shortPollingVersion = CollaborationProtocol.ShortPollingVersion,
            transports = new[] { "websocket", "long-polling", "short-polling" }, room = Name, generation = document.generation, catalogHash = catalog.hash }; } }
        public string Incarnation { get; } = Guid.NewGuid().ToString("N");
        internal bool PasswordRequired { get; }
        public const int LeaseMilliseconds = 10000;

        internal static Room Recover(RoomStore storage, RoomArchive archive) => new Room(null, null, storage, archive);
        public Room(Bootstrap seed, Func<long> monotonicMilliseconds = null, RoomStore storage = null) : this(seed, monotonicMilliseconds, storage, null) { }
        private Room(Bootstrap seed, Func<long> monotonicMilliseconds, RoomStore storage, RoomArchive archive) {
            clock = monotonicMilliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency));
            store = storage;
            archive ??= store?.Load();
            if (archive != null) {
                DocumentModel.Require(seed == null || seed.room == archive.info.room && seed.catalog?.hash == archive.info.catalog.hash, "recovery-catalog-or-room-mismatch");
                Name = archive.info.room; catalog = archive.info.catalog; document = archive.document;
                roomInfo = archive.info;
                salt = archive.info.salt; passwordHash = archive.info.passwordHash; ownerHash = archive.info.ownerHash;
                assetBytes = archive.assets; foreach (var bytes in assetBytes.Values) IndexAsset(GeometryTransfer.Decode(bytes)); minimumCursor = archive.info.migrationRevision;
                events.AddRange(archive.events); historyBytes = archive.historyBytes; Recovered = true;
                foreach (var loaded in archive.members.Values) {
                    var data = loaded.member;
                    var member = new Member { participant = data.participant, resumeHash = data.resumeHash, sequence = data.sequence,
                        historyCursor = data.historyCursor, revoked = data.revoked, lastSeen = clock() - 60001,
                        history = loaded.history, receipts = loaded.receipts.ToDictionary(r => r.operationId) };
                    members.Add(member.resumeHash, member); receiptCount += member.receipts.Count;
                }
            }
            else {
                DocumentModel.Require(seed != null, "recovery-room-not-found");
                DocumentModel.Validate(seed.document, seed.catalog);
                GeometryTransfer.RequireHydrated(seed.document);
                foreach (var asset in DocumentModel.Assets(seed.document)) { assetBytes.Add(asset.hash, GeometryTransfer.Encode(asset)); IndexAsset(asset); }
                Name = seed.room; document = WireJson.Clone(GeometryTransfer.References(seed.document)); document.schema = 2; minimumCursor = document.revision; catalog = WireJson.Clone(seed.catalog);
                passwordHash = Password(seed.password ?? ""); ownerHash = string.IsNullOrEmpty(seed.ownerCredential) ? null : TokenHash(seed.ownerCredential);
                roomInfo = new StoredRoomInfo { room = Name, generation = document.generation, initialRevision = document.revision, migrationRevision = minimumCursor,
                    catalog = catalog, salt = salt, passwordHash = passwordHash, ownerHash = ownerHash,
                    allowEditorProjectReplacement = seed.allowEditorProjectReplacement };
                store?.Initialize(roomInfo, document, assetBytes, GeometryTransfer.HasAppearance(seed.document));
            }
            PasswordRequired = !CryptographicOperations.FixedTimeEquals(Password(""), passwordHash);
            diagnosticHash = DocumentModel.Hash(document);
        }

        public JoinResponse Join(JoinRequest request, bool requireGenerationProof = false) {
            lock (gate) {
                Healthy(); Expire();
                DocumentModel.Require(request != null && request.protocol == 1, "protocol-mismatch");
                DocumentModel.Require(request.room == null || request.room == Name, "catalog-or-room-mismatch");
                DocumentModel.Require(request.version == CollaborationProtocol.Version, "server-version-mismatch");
                DocumentModel.Require(request.geometryLimitsVersion == GeometryTransfer.LimitsVersion, "geometry-limits-client-required");
                DocumentModel.Require(request.customAppearanceVersion == CustomAppearance.Version, "appearance-client-required");
                DocumentModel.Require(request.catalogHash == catalog.hash, "catalog-mismatch");
                DocumentModel.Require(!requireGenerationProof || roomInfo.schema < 2 || request.expectedGeneration != null && request.roomIdentity != null,
                    "room-replacement-client-required");
                DocumentModel.Require(request.roomIdentity == null || request.roomIdentity == RoomStore.Identity(roomInfo), "saved-room-does-not-match");
                DocumentModel.Require(request.expectedGeneration == null || request.expectedGeneration == document.generation, "room-replaced");
                DocumentModel.Require(request.resynchronizeFromGeneration == null || !string.IsNullOrEmpty(request.resumeToken) &&
                    (roomInfo.replacements ?? Array.Empty<RoomReplacementReceipt>()).Any(r => r.previousGeneration == request.resynchronizeFromGeneration), "saved-room-does-not-match");
                Member member;
                string resume = request.resumeToken;
                if (!string.IsNullOrEmpty(resume)) {
                    DocumentModel.Require(resume.Length < 256 && members.TryGetValue(TokenHash(resume), out _), "invalid-session");
                    member = members[TokenHash(resume)]; DocumentModel.Require(!member.revoked, "membership-revoked"); Touch(member);
                    // Fence requests from the old transport before returning a fresh snapshot.
                    // A delayed old create cannot arrive after pending outcomes were resolved as absent.
                    if (member.accessHash != null) sessions.Remove(member.accessHash);
                    foreach (var key in leases.Where(l => l.Value.author == member.participant.id).Select(l => l.Key).ToArray()) leases.Remove(key);
                    Pulse();
                }
                else {
                    bool owner = ownerHash != null && !string.IsNullOrEmpty(request.ownerCredential) && TokenHash(request.ownerCredential) == ownerHash;
                    DocumentModel.Require(owner || CryptographicOperations.FixedTimeEquals(Password(request.password ?? ""), passwordHash), "wrong-password");
                    DocumentModel.Require(members.Count < 1000 && ActiveCount() < 10, "room-full");
                    DocumentModel.Require(!string.IsNullOrWhiteSpace(request.name) && request.name.Length <= 48 && !request.name.Any(char.IsControl), "invalid-name");
                    resume = NewToken();
                    member = new Member { participant = new Participant {
                        id = Guid.NewGuid().ToString("N"), name = request.name,
                        role = owner ? "owner" : request.viewer ? "viewer" : "editor"
                    }, lastSeen = clock(), resumeHash = TokenHash(resume) };
                    Persist(() => store.AddMember(member.Record()));
                    members.Add(member.resumeHash, member);
                    Pulse();
                }
                string token = NewToken(); member.accessHash = TokenHash(token); member.sessionGeneration = document.generation; sessions.Add(member.accessHash, member);
                return new JoinResponse { room = Name, token = token, resumeToken = resume, sequence = member.sequence,
                    incarnation = Incarnation, participant = WireJson.Clone(member.participant), document = WireJson.Clone(document) };
            }
        }

        public LeaseResponse Reserve(string token, LeaseRequest request) {
            lock (gate) {
                Expire(); var member = Editor(token);
                DocumentModel.Require(request?.ids != null && request.ids.Length > 0 && request.ids.Length <= DocumentModel.MaxWriteSet &&
                    request.ids.Distinct(StringComparer.Ordinal).Count() == request.ids.Length, "invalid-reservation");
                DocumentModel.Require(request.ids.All(id => document.parts.Any(p => p.id == id)), "missing-entity");
                if (!string.IsNullOrEmpty(request.token)) {
                    var old = OwnedLease(member, request.token);
                    // Set changes are all-or-none and use a fresh fencing token.
                    if (request.ids.OrderBy(x => x).SequenceEqual(old.ids.OrderBy(x => x))) {
                        old.expires = clock() + LeaseMilliseconds;
                        return new LeaseResponse { token = old.token, lifetimeMilliseconds = LeaseMilliseconds };
                    }
                }
                DocumentModel.Require(!leases.Values.Any(l => l.token != request.token && l.ids.Intersect(request.ids).Any()), "parts-reserved");
                if (!string.IsNullOrEmpty(request.token)) leases.Remove(request.token);
                var lease = new Lease { author = member.participant.id, ids = request.ids.ToArray(), token = Incarnation + "_" + NewToken(), expires = clock() + LeaseMilliseconds };
                leases.Add(lease.token, lease); Pulse();
                return new LeaseResponse { token = lease.token, lifetimeMilliseconds = LeaseMilliseconds };
            }
        }

        public void Release(string token, string leaseToken) {
            lock (gate) {
                Expire(); var member = Authenticate(token);
                if (leases.TryGetValue(leaseToken ?? "", out var lease)) {
                    DocumentModel.Require(lease.author == member.participant.id, "lease-owner");
                    leases.Remove(leaseToken); Pulse();
                }
            }
        }

        public void Preview(string token, Preview preview) {
            lock (gate) {
                Expire(); var member = Editor(token);
                DocumentModel.Require(preview != null, "invalid-preview");
                DocumentModel.Require(preview.sequence > 0, "invalid-preview-sequence");
                var lease = OwnedLease(member, preview.lease);
                if (lease.preview != null && preview.sequence <= lease.preview.sequence) return;
                DocumentModel.Require(preview.parts != null && preview.parts.Length == lease.ids.Length &&
                    preview.parts.Select(p => p?.id).OrderBy(x => x).SequenceEqual(lease.ids.OrderBy(x => x)), "preview-set-mismatch");
                foreach (var part in preview.parts) {
                    DocumentModel.ValidatePart(part, catalog);
                    DocumentModel.Require(part.catalogId != "CUSTOM" || DocumentModel.Assets(document).Any(asset => asset.hash == part.assetHash), "missing-asset");
                }
                var copy = WireJson.Clone(preview); copy.author = member.participant.id;
                copy.parts = copy.parts.Select(p => new Part { id = p.id, position = p.position, rotation = p.rotation }).ToArray();
                lease.preview = copy; Pulse();
            }
        }

        public Receipt Commit(string token, Transaction transaction) {
            lock (gate) {
                Expire(); var member = Editor(token);
                DocumentModel.Require(transaction != null && DocumentModel.ValidId(transaction.operationId), "invalid-operation");
                if (member.receipts.TryGetValue(transaction.operationId, out var previous)) return WireJson.Clone(previous);
                DocumentModel.Require(receiptCount < 20000 && events.Count < 20000, "alpha-history-limit");
                var receipt = new Receipt { operationId = transaction.operationId, revision = document.revision };
                try {
                    DocumentModel.Require(transaction.generation == document.generation, "generation-mismatch");
                    DocumentModel.Require(transaction.sequence > member.sequence, "sequence-reused");
                    bool undo = transaction.kind == "Undo", redo = transaction.kind == "Redo";
                    StoredHistory inverse = null;
                    var command = transaction;
                    if (undo || redo) {
                        DocumentModel.Require(transaction.writes != null && transaction.deletes != null && transaction.writes.Length == 0 && transaction.deletes.Length == 0 &&
                            DocumentModel.ChainWrites(transaction).Length == 0 && DocumentModel.ChainDeletes(transaction).Length == 0 &&
                            DocumentModel.AssetWrites(transaction).Length == 0, "invalid-history-command");
                        int index = undo ? member.historyCursor - 1 : member.historyCursor;
                        DocumentModel.Require(index >= 0 && index < member.history.Count, undo ? "nothing-to-undo" : "nothing-to-redo");
                        inverse = member.history[index];
                        inverse.chainIds ??= Array.Empty<string>(); inverse.chainsBefore ??= Array.Empty<ChainRecord>(); inverse.chainsAfter ??= Array.Empty<ChainRecord>();
                        DocumentModel.Require(!inverse.blocked, "history-changed-by-another-participant");
                        var expectedState = undo ? inverse.after : inverse.before;
                        DocumentModel.Require(inverse.ids.All(id => DocumentModel.SamePart(document.parts.FirstOrDefault(p => p.id == id),
                            expectedState.FirstOrDefault(p => p.id == id))), "stale-history");
                        var expectedChains = undo ? inverse.chainsAfter : inverse.chainsBefore;
                        DocumentModel.Require(inverse.chainIds.All(id => DocumentModel.SameChain(DocumentModel.Chains(document).FirstOrDefault(c => c.id == id),
                            expectedChains.FirstOrDefault(c => c.id == id))), "stale-history");
                        var inverseLeaseIds = inverse.ids.Concat(inverse.chainsBefore.Concat(inverse.chainsAfter).SelectMany(c => c.members)).Distinct().ToArray();
                        DocumentModel.Require(!leases.Values.Any(l => l.ids.Intersect(inverseLeaseIds).Any()), "parts-reserved");
                        var restore = undo ? inverse.before : inverse.after;
                        var restoreChains = undo ? inverse.chainsBefore : inverse.chainsAfter;
                        command = new Transaction { protocol = transaction.protocol, operationId = transaction.operationId, sequence = transaction.sequence,
                            expectedRevision = transaction.expectedRevision,
                            generation = transaction.generation, kind = "RestoreParts", writes = WireJson.Clone(restore),
                            deletes = inverse.ids.Where(id => !restore.Any(p => p.id == id)).ToArray(),
                            expected = inverse.ids.Concat(inverse.chainsBefore.Concat(inverse.chainsAfter).SelectMany(c => c.members)).Distinct().Select(id =>
                                new VersionRef { id = id, version = document.parts.FirstOrDefault(p => p.id == id)?.version ??
                                document.tombstones.FirstOrDefault(p => p.id == id)?.version ?? 0 }).ToArray(),
                            chainWrites = WireJson.Clone(restoreChains), chainDeletes = inverse.chainIds.Where(id => !restoreChains.Any(c => c.id == id)).ToArray(),
                            expectedChains = inverse.chainIds.Select(id => new VersionRef { id = id, version = DocumentModel.Chains(document).FirstOrDefault(c => c.id == id)?.version ??
                                DocumentModel.ChainTombstones(document).FirstOrDefault(c => c.id == id)?.version ?? 0 }).ToArray() };
                    }
                    else DocumentModel.Require(transaction.kind != "RestoreParts", "authority-only-mutation");
                    // The in-process adapter also supports small inline callers; HTTP commits require references.
                    if (DocumentModel.AssetWrites(command).Any(GeometryTransfer.HasGeometry)) {
                        foreach (var asset in DocumentModel.AssetWrites(command)) if (GeometryTransfer.HasGeometry(asset)) PutAsset(token, asset.hash, GeometryTransfer.Encode(asset));
                        command = LocalJson.Clone(command); command.assetWrites = command.assetWrites.Select(GeometryTransfer.Reference).ToArray();
                    }
                    foreach (var reference in DocumentModel.AssetWrites(command)) {
                        GeometryTransfer.ValidateReference(reference);
                        DocumentModel.Require(assetNames.TryGetValue(reference.hash, out var name) && name == reference.name, "missing-asset");
                    }
                    var next = DocumentModel.Reduce(document, command, catalog);
                    GeometryTransfer.RequireSceneBudget(next, hash => assetArrayBytes[hash]);
                    var changedPartIds = command.writes.Select(p => p.id).Concat(command.deletes).ToArray();
                    var changedChainIds = DocumentModel.ChainWrites(command).Select(c => c.id).Concat(DocumentModel.ChainDeletes(command)).ToArray();
                    var changedIds = changedPartIds.Concat(changedChainIds).ToArray();
                    var before = document.parts.Where(p => changedPartIds.Contains(p.id)).ToArray();
                    var after = next.parts.Where(p => changedPartIds.Contains(p.id)).ToArray();
                    var chainsBefore = DocumentModel.Chains(document).Where(c => changedChainIds.Contains(c.id)).ToArray();
                    var chainsAfter = DocumentModel.Chains(next).Where(c => changedChainIds.Contains(c.id)).ToArray();
                    int transactionBytes = WireJson.Encode(command).Length + (inverse == null ? WireJson.Encode(before).Length + WireJson.Encode(after).Length +
                        WireJson.Encode(chainsBefore).Length + WireJson.Encode(chainsAfter).Length : 0);
                    DocumentModel.Require(historyBytes + transactionBytes <= 64 * 1024 * 1024, "alpha-history-limit");
                    var existing = changedPartIds.Where(id => document.parts.Any(p => p.id == id));
                    var chainMembers = DocumentModel.ChainWrites(command).SelectMany(c => c.members)
                        .Concat(DocumentModel.ChainWrites(command).Where(c => DocumentModel.Chains(document).Any(old => old.id == c.id))
                            .SelectMany(c => DocumentModel.Chains(document).First(old => old.id == c.id).members))
                        .Concat(DocumentModel.ChainDeletes(command).SelectMany(id => DocumentModel.Chains(document).FirstOrDefault(c => c.id == id)?.members ?? Array.Empty<string>()));
                    // Fresh part IDs in a compound create transaction cannot be
                    // reserved yet. Existing endpoints still require the ordinary
                    // lease, while the reducer restricts new chains to endpoints
                    // created by that same atomic transaction.
                    var existingPartIds = document.parts.Select(p => p.id).ToHashSet(StringComparer.Ordinal);
                    var requiredLeaseIds = existing.Concat(chainMembers.Where(existingPartIds.Contains)).Distinct().ToArray();
                    Lease lease = null;
                    if (requiredLeaseIds.Length > 0 && inverse == null) {
                        lease = OwnedLease(member, transaction.lease);
                        DocumentModel.Require(requiredLeaseIds.All(lease.ids.Contains), "lease-set-mismatch");
                    }
                    var accepted = WireJson.Clone(command); accepted.lease = null;
                    var edit = new AcceptedEdit { revision = next.revision, author = member.participant.id, transaction = accepted, hash = DocumentModel.Hash(next) };
                    var entry = inverse == null ? new StoredHistory { ids = changedPartIds, before = WireJson.Clone(before), after = WireJson.Clone(after),
                        chainIds = changedChainIds, chainsBefore = WireJson.Clone(chainsBefore), chainsAfter = WireJson.Clone(chainsAfter) } : null;
                    var blocked = members.Values.Where(m => m != member).SelectMany(m => m.history.Select((h, i) => (member: m.participant.id, index: i, entry: h)))
                        .Where(h => !h.entry.blocked && h.entry.ids.Concat(h.entry.chainIds ?? Array.Empty<string>()).Intersect(changedIds).Any()).Select(h => (h.member, h.index)).ToArray();
                    var recorded = member.Record(); recorded.sequence = transaction.sequence;
                    recorded.historyCursor = inverse != null ? member.historyCursor + (undo ? -1 : 1) : member.historyCursor + 1;
                    // Reserve process capacity before disk publication; roll it back on a failed commit.
                    long sceneDelta = SceneBytes(next) - SceneBytes(document);
                    using var capacity = processBudget?.Reserve(0, 0, Math.Max(0, sceneDelta));
                    receipt.accepted = true; receipt.revision = next.revision;
                    // Commit document journal, receipt, identity sequence and undo state together before publication/acknowledgment.
                    Persist(() => store.Commit(recorded, receipt, edit, next, entry, member.historyCursor, blocked, transactionBytes, historyBytes + transactionBytes));
                    document = next; diagnosticHash = edit.hash; capacity?.Commit();
                    if (sceneDelta < 0) { using var release = processBudget?.Reserve(0, 0, sceneDelta); release?.Commit(); }
                    historyBytes += transactionBytes;
                    // Only the authority supplies inverse content. Another participant touching any of its
                    // entities permanently fences an older inverse, even if they later restore equal values.
                    foreach (var other in members.Values.Where(m => m != member))
                        foreach (var item in other.history.Where(h => h.ids.Concat(h.chainIds ?? Array.Empty<string>()).Intersect(changedIds).Any())) item.blocked = true;
                    if (inverse != null) member.historyCursor += undo ? -1 : 1;
                    else {
                        member.history.RemoveRange(member.historyCursor, member.history.Count - member.historyCursor);
                        member.history.Add(entry);
                        member.historyCursor = member.history.Count;
                    }
                    events.Add(edit);
                    if (lease != null) leases.Remove(lease.token);
                    member.sequence = transaction.sequence;
                    Pulse();
                }
                catch (CollaborationException error) { if (storageFailed) throw; receipt.error = error.Code; }
                if (!receipt.accepted) Persist(() => store.RecordReceipt(member.participant.id, receipt));
                member.receipts.Add(transaction.operationId, receipt);
                receiptCount++;
                return WireJson.Clone(receipt);
            }
        }

        public Receipt Outcome(string token, string operationId, string generation = null) {
            lock (gate) {
                var member = Authenticate(token);
                DocumentModel.Require(DocumentModel.ValidId(operationId), "invalid-operation");
                DocumentModel.Require(generation == null || generation == document.generation ||
                    (roomInfo.replacements ?? Array.Empty<RoomReplacementReceipt>()).Any(r => r.previousGeneration == generation), "saved-room-does-not-match");
                return member.receipts.TryGetValue(operationId, out var receipt) ? WireJson.Clone(receipt) :
                    new Receipt { operationId = operationId, error = generation != null && generation != document.generation ?
                        "operation-not-accepted-before-replacement" : "Edit was not accepted; retry explicitly.", revision = document.revision };
            }
        }

        public async Task<EventBatch> ReadAsync(string token, long after, long transient, CancellationToken cancellation, bool wait = true) {
            cancellation.ThrowIfCancellationRequested();
            Task wake;
            lock (gate) {
                Expire(); Authenticate(token);
                DocumentModel.Require(after >= minimumCursor && after <= document.revision, "resync-required");
                if (!wait || events.Any(e => e.revision > after) || transient != transientVersion) return Batch(after);
                wake = changed.Task;
            }
            // Held HTTPS requests wake immediately for edits/previews; timer ticks only expire leases.
            for (int tick = 0; tick < 20; tick++) {
                await Task.WhenAny(wake, Task.Delay(1000, cancellation)).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                lock (gate) {
                    Expire(); Authenticate(token);
                    if (wake.IsCompleted || transient != transientVersion || tick == 19) return Batch(after);
                }
            }
            throw new InvalidOperationException();
        }

        public void Leave(string token) {
            lock (gate) {
                // A paused old generation may end its own membership without
                // opening the replacement. This grants no document/read authority.
                var member = Authenticate(token, touch: false, allowRetired: true);
                Revoke(member);
            }
        }

        public MembershipList Memberships(string token) {
            lock (gate) {
                Owner(token);
                return new MembershipList { memberships = members.Values.OrderBy(m => m.participant.id, StringComparer.Ordinal)
                    .Select(m => new Membership { participant = WireJson.Clone(m.participant), revoked = m.revoked,
                        active = !m.revoked && clock() - m.lastSeen < 60000 }).ToArray() };
            }
        }

        public RemovalResult RemoveParticipant(string token, RemoveParticipant request) {
            lock (gate) {
                Owner(token);
                DocumentModel.Require(request != null && DocumentModel.ValidId(request.participantId), "invalid-participant-id");
                var target = members.Values.FirstOrDefault(m => m.participant.id == request.participantId);
                DocumentModel.Require(target != null, "participant-not-found");
                DocumentModel.Require(target.participant.role != "owner", "owner-removal-protected");
                bool alreadyRevoked = target.revoked;
                if (!alreadyRevoked) Revoke(target);
                return new RemovalResult { participantId = target.participant.id, alreadyRevoked = alreadyRevoked };
            }
        }

        private void Owner(string token) {
            Healthy();
            // Dedicated operators use the separate room capability without creating a membership.
            if (ownerHash != null && token != null && token.Length < 256 && TokenHash(token) == ownerHash) return;
            DocumentModel.Require(Authenticate(token).participant.role == "owner", "owner-required");
        }

        private void Revoke(Member member) {
            var revoked = member.Record(); revoked.revoked = true;
            // Same gate and durable transition as Leave. Publication follows successful storage;
            // a failed write freezes the room, and restart decides from the committed database.
            Persist(() => store.Revoke(revoked));
            member.revoked = true;
            // Retain the current access hash only to return an explicit terminal reason.
            // Authenticate rejects it; resume rejects the durable revoked flag as well.
            foreach (var key in leases.Where(l => l.Value.author == member.participant.id).Select(l => l.Key).ToArray()) leases.Remove(key);
            Pulse();
        }

        private EventBatch Batch(long after) => new EventBatch {
            edits = events.Where(e => e.revision > after).Take(1).ToArray(),
            previews = leases.Values.Where(l => l.preview != null).Select(l => {
                var preview = WireJson.Clone(l.preview); preview.lease = null; return preview;
            }).ToArray(), participants = members.Values.Where(m => !m.revoked && clock() - m.lastSeen < 60000).Select(m => m.participant).ToArray(), transientVersion = transientVersion
        };
        private Member Authenticate(string token, bool touch = true, bool allowRetired = false) {
            Healthy();
            DocumentModel.Require(token != null && token.Length < 256 && sessions.TryGetValue(TokenHash(token), out _), "invalid-session");
            var member = sessions[TokenHash(token)]; DocumentModel.Require(!member.revoked, "membership-revoked");
            DocumentModel.Require(allowRetired || member.sessionGeneration == document.generation, "room-replaced");
            if (touch) Touch(member); return member;
        }
        private int ActiveCount() => members.Values.Count(m => !m.revoked && clock() - m.lastSeen < 60000);
        private void Touch(Member member) {
            DocumentModel.Require(clock() - member.lastSeen < 60000 || ActiveCount() < 10, "room-full");
            member.lastSeen = clock();
        }
        internal void BeginShutdown() {
            // Fence new work and wake existing reads without waiting on a room's
            // synchronous durable write. An already admitted atomic write settles
            // normally; shutdown never rewrites its receipt or revokes a member.
            stopping = true; Volatile.Read(ref changed).TrySetResult(true);
        }
        internal void CloseStorage() {
            // Kestrel can abort a request while its synchronous durable write is
            // still settling. Never dispose SQLite/budget underneath that write.
            lock (gate) { DetachBudget(); store?.Dispose(); }
        }
        private void Healthy() {
            DocumentModel.Require(!stopping, "server-stopping");
            DocumentModel.Require(!storageFailed, "room-storage-failed-restart-required");
        }
        private void Persist(Action write) {
            if (store == null) return;
            try { write(); }
            catch { storageFailed = true; Pulse(); throw new CollaborationException("room-storage-failed-restart-required"); }
        }
        private Member Editor(string token) {
            var member = Authenticate(token); DocumentModel.Require(member.participant.role != "viewer", "viewer-read-only"); return member;
        }
        private Lease OwnedLease(Member member, string token) {
            DocumentModel.Require(token != null && leases.TryGetValue(token, out _), "lease-expired");
            var lease = leases[token]; DocumentModel.Require(lease.author == member.participant.id, "lease-owner"); return lease;
        }
        private void Expire() {
            bool any = false;
            foreach (var key in leases.Where(l => l.Value.expires <= clock()).Select(l => l.Key).ToArray()) { leases.Remove(key); any = true; }
            // Keep identity/receipts for this ephemeral room. Presence omission does not revoke resume credentials.
            if (any) Pulse();
        }
        private void Pulse() { transientVersion++; var old = changed; changed = Signal(); old.TrySetResult(true); }
        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] Password(string password) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        private static string TokenHash(string token) => DocumentModel.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        private static string NewToken() => DocumentModel.Hex(RandomNumberGenerator.GetBytes(24));
    }
}
