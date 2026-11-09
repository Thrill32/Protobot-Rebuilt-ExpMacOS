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
    public sealed class Room {
        private sealed class Member {
            public Participant participant;
            public long lastSeen;
            public long sequence;
            public Dictionary<string, Receipt> receipts = new Dictionary<string, Receipt>();
            public List<HistoryEntry> history = new List<HistoryEntry>();
            public int historyCursor;
        }
        private sealed class HistoryEntry {
            public string[] ids;
            public Part[] before, after;
            public bool blocked;
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
        private readonly Dictionary<string, Lease> leases = new Dictionary<string, Lease>();
        private readonly List<AcceptedEdit> events = new List<AcceptedEdit>();
        private TaskCompletionSource<bool> changed = Signal();
        private Document document;
        private long transientVersion;
        private long historyBytes;
        private int receiptCount;
        private readonly Func<long> clock;
        public string Incarnation { get; } = Guid.NewGuid().ToString("N");
        public const int LeaseMilliseconds = 10000;

        public Room(Bootstrap seed, Func<long> monotonicMilliseconds = null) {
            DocumentModel.Validate(seed.document, seed.catalog);
            document = WireJson.Clone(seed.document); catalog = WireJson.Clone(seed.catalog);
            passwordHash = Password(seed.password ?? ""); ownerHash = string.IsNullOrEmpty(seed.ownerCredential) ? null : TokenHash(seed.ownerCredential);
            clock = monotonicMilliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency));
        }

        public JoinResponse Join(JoinRequest request) {
            lock (gate) {
                Expire();
                DocumentModel.Require(request != null && request.protocol == 1, "protocol-mismatch");
                DocumentModel.Require(request.catalogHash == catalog.hash, "catalog-mismatch");
                Member member;
                string token = request.resumeToken;
                if (!string.IsNullOrEmpty(token)) {
                    member = Authenticate(token);
                    // Fence requests from the old transport before returning a fresh snapshot.
                    // A delayed old create cannot arrive after pending outcomes were resolved as absent.
                    members.Remove(TokenHash(token)); token = NewToken(); members.Add(TokenHash(token), member);
                    foreach (var key in leases.Where(l => l.Value.author == member.participant.id).Select(l => l.Key).ToArray()) leases.Remove(key);
                    Pulse();
                }
                else {
                    DocumentModel.Require(CryptographicOperations.FixedTimeEquals(Password(request.password ?? ""), passwordHash), "wrong-password");
                    DocumentModel.Require(members.Count < 1000 && members.Values.Count(m => clock() - m.lastSeen < 60000) < 10, "room-full");
                    DocumentModel.Require(!string.IsNullOrWhiteSpace(request.name) && request.name.Length <= 48 && !request.name.Any(char.IsControl), "invalid-name");
                    token = NewToken();
                    member = new Member { participant = new Participant {
                        id = Guid.NewGuid().ToString("N"), name = request.name,
                        role = ownerHash != null && !string.IsNullOrEmpty(request.ownerCredential) && TokenHash(request.ownerCredential) == ownerHash ? "owner" : request.viewer ? "viewer" : "editor"
                    }, lastSeen = clock() };
                    members.Add(TokenHash(token), member);
                    Pulse();
                }
                return new JoinResponse { token = token, incarnation = Incarnation, participant = WireJson.Clone(member.participant), document = WireJson.Clone(document) };
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
                foreach (var part in preview.parts) DocumentModel.ValidatePart(part, catalog);
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
                    DocumentModel.Require(transaction.sequence > member.sequence, "sequence-reused");
                    bool undo = transaction.kind == "Undo", redo = transaction.kind == "Redo";
                    HistoryEntry inverse = null;
                    var command = transaction;
                    if (undo || redo) {
                        DocumentModel.Require(transaction.writes != null && transaction.deletes != null && transaction.writes.Length == 0 && transaction.deletes.Length == 0, "invalid-history-command");
                        int index = undo ? member.historyCursor - 1 : member.historyCursor;
                        DocumentModel.Require(index >= 0 && index < member.history.Count, undo ? "nothing-to-undo" : "nothing-to-redo");
                        inverse = member.history[index];
                        DocumentModel.Require(!inverse.blocked, "history-changed-by-another-participant");
                        var expectedState = undo ? inverse.after : inverse.before;
                        DocumentModel.Require(inverse.ids.All(id => DocumentModel.SamePart(document.parts.FirstOrDefault(p => p.id == id),
                            expectedState.FirstOrDefault(p => p.id == id))), "stale-history");
                        DocumentModel.Require(!leases.Values.Any(l => l.ids.Intersect(inverse.ids).Any()), "parts-reserved");
                        var restore = undo ? inverse.before : inverse.after;
                        command = new Transaction { operationId = transaction.operationId, sequence = transaction.sequence,
                            generation = transaction.generation, kind = "RestoreParts", writes = WireJson.Clone(restore),
                            deletes = inverse.ids.Where(id => !restore.Any(p => p.id == id)).ToArray(),
                            expected = inverse.ids.Select(id => new VersionRef { id = id, version = document.parts.FirstOrDefault(p => p.id == id)?.version ??
                                document.tombstones.FirstOrDefault(p => p.id == id)?.version ?? 0 }).ToArray() };
                    }
                    else DocumentModel.Require(transaction.kind != "RestoreParts", "authority-only-mutation");
                    var next = DocumentModel.Reduce(document, command, catalog);
                    var changedIds = command.writes.Select(p => p.id).Concat(command.deletes).ToArray();
                    var before = document.parts.Where(p => changedIds.Contains(p.id)).ToArray();
                    var after = next.parts.Where(p => changedIds.Contains(p.id)).ToArray();
                    int transactionBytes = WireJson.Encode(command).Length + (inverse == null ? WireJson.Encode(before).Length + WireJson.Encode(after).Length : 0);
                    DocumentModel.Require(historyBytes + transactionBytes <= 64 * 1024 * 1024, "alpha-history-limit");
                    var existing = changedIds.Where(id => document.parts.Any(p => p.id == id)).ToArray();
                    Lease lease = null;
                    if (existing.Length > 0 && inverse == null) {
                        lease = OwnedLease(member, transaction.lease);
                        DocumentModel.Require(existing.All(lease.ids.Contains), "lease-set-mismatch");
                    }
                    // Only version preconditions constrain unrelated edits, never the global revision.
                    document = next;
                    historyBytes += transactionBytes;
                    // Only the authority supplies inverse content. Another participant touching any of its
                    // entities permanently fences an older inverse, even if they later restore equal values.
                    foreach (var other in members.Values.Where(m => m != member))
                        foreach (var item in other.history.Where(h => h.ids.Intersect(changedIds).Any())) item.blocked = true;
                    if (inverse != null) member.historyCursor += undo ? -1 : 1;
                    else {
                        member.history.RemoveRange(member.historyCursor, member.history.Count - member.historyCursor);
                        member.history.Add(new HistoryEntry { ids = changedIds, before = WireJson.Clone(before), after = WireJson.Clone(after) });
                        member.historyCursor = member.history.Count;
                    }
                    var accepted = WireJson.Clone(command);
                    // Receipts/events never disclose the ephemeral reservation credential.
                    accepted.lease = null;
                    events.Add(new AcceptedEdit { revision = next.revision, author = member.participant.id, transaction = accepted, hash = DocumentModel.Hash(next) });
                    receipt.accepted = true; receipt.revision = next.revision;
                    if (lease != null) leases.Remove(lease.token);
                    member.sequence = transaction.sequence;
                    Pulse();
                }
                catch (CollaborationException error) { receipt.error = error.Code; }
                member.receipts.Add(transaction.operationId, receipt);
                receiptCount++;
                return WireJson.Clone(receipt);
            }
        }

        public Receipt Outcome(string token, string operationId) {
            lock (gate) {
                var member = Authenticate(token);
                DocumentModel.Require(DocumentModel.ValidId(operationId), "invalid-operation");
                return member.receipts.TryGetValue(operationId, out var receipt) ? WireJson.Clone(receipt) :
                    new Receipt { operationId = operationId, error = "Edit was not accepted; retry explicitly.", revision = document.revision };
            }
        }

        public async Task<EventBatch> ReadAsync(string token, long after, long transient, CancellationToken cancellation) {
            Task wake;
            lock (gate) {
                Expire(); Authenticate(token);
                DocumentModel.Require(after >= 0 && after <= document.revision, "resync-required");
                if (events.Any(e => e.revision > after) || transient != transientVersion) return Batch(after);
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
                var member = Authenticate(token);
                members.Remove(TokenHash(token));
                foreach (var key in leases.Where(l => l.Value.author == member.participant.id).Select(l => l.Key).ToArray()) leases.Remove(key);
                Pulse();
            }
        }

        private EventBatch Batch(long after) => new EventBatch {
            edits = events.Where(e => e.revision > after).Take(1).ToArray(),
            previews = leases.Values.Where(l => l.preview != null).Select(l => {
                var preview = WireJson.Clone(l.preview); preview.lease = null; return preview;
            }).ToArray(), participants = members.Values.Where(m => clock() - m.lastSeen < 60000).Select(m => m.participant).ToArray(), transientVersion = transientVersion
        };
        private Member Authenticate(string token) {
            DocumentModel.Require(token != null && token.Length < 256 && members.TryGetValue(TokenHash(token), out _), "invalid-session");
            var member = members[TokenHash(token)];
            DocumentModel.Require(clock() - member.lastSeen < 60000 || members.Values.Count(m => clock() - m.lastSeen < 60000) < 10, "room-full");
            member.lastSeen = clock(); return member;
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
