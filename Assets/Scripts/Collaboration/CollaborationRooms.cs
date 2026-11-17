using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        private GameObject uiCreateRoom, uiOwnedRooms, uiServerAdmin, uiCreateConfirmation;
        private InputField uiCreateAddress, uiCreationKey, uiAdminAddress, uiServerAdminKey;
        private Dropdown uiCreationAuthority;
        private Text uiCreationPolicy, uiServerRooms;
        private Transform uiCreatedRooms;
        private CancellationTokenSource roomOperation;
        private string creationKey = "", serverAdminKey = "";
        private int creationAuthority; // 0 key, 1 explicit open, 2 server administrator
        private string createdRoomSignature;
        private CreatedRoom ownerJoin;
        private static string CreatedRoomsRoot => Path.Combine(CollaborationRecovery.Root, "created-rooms");

        private void InitializeRoomsUI(Transform form) {
            uiCreateRoom = UIColumn(form, "Create Room Page").gameObject;
            UIText(uiCreateRoom.transform, "Help", "Create a new room on a shared server from your current accepted build. Creation keeps your project and local file open. Join as owner afterward to start sharing.");
            uiCreateAddress = UIField(uiCreateRoom.transform, "Creation server address", endpoint, 256, value => endpoint = value);
            uiCreationAuthority = UIDropdown(uiCreateRoom.transform, "Creation authority", new[] { "Room creation key", "Explicitly open server", "Server administrator" }, 0, value => { creationAuthority = value; ClearRoomKeys(); });
            uiCreationKey = UIField(uiCreateRoom.transform, "Creation / administrator key (private)", "", 64, value => creationKey = value, true);
            UIText(uiCreateRoom.transform, "Authority Help", "A creation key only creates rooms. Each new room has a private owner credential retained in this local profile. The room password controls joining; it is separate from creation and administration.", 12);
            UIButton(uiCreateRoom.transform, "Check Creation Policy", "Check server creation policy", () => _ = InspectCreationAsync());
            uiCreationPolicy = UIText(uiCreateRoom.transform, "Creation Policy", "Check the operator's policy before creating.", 13);
            UIButton(uiCreateRoom.transform, "Prepare Room Creation", "Create room from current build...", () => { uiCreateConfirmation.SetActive(true); });
            uiCreateConfirmation = UIColumn(uiCreateRoom.transform, "Confirm Room Creation").gameObject;
            UIText(uiCreateConfirmation.transform, "Warning", "Publish a copy of the current accepted build as a new durable room? A blank room password allows password-free joining by room ID. This uses one of the server's eight room slots. Existing rooms are never replaced.");
            var actions = UIRow(uiCreateConfirmation.transform, "Creation Actions");
            UIButton(actions, "Confirm Create Room", "Create new room", () => _ = CreateRemoteRoomAsync(), true);
            UIButton(actions, "Cancel Create Room", "Cancel", () => uiCreateConfirmation.SetActive(false));
            uiCreateConfirmation.SetActive(false);
            UIButton(uiCreateRoom.transform, "View Owned Rooms", "View your owned / prepared rooms", () => SelectUITab(5));

            uiOwnedRooms = UIColumn(form, "Owned Rooms Page").gameObject;
            UIText(uiOwnedRooms.transform, "Ownership Help", "Rooms you own in this local profile. Check uses each record's original server address. Join opens its accepted content after normal unsaved-project handling; replacement publishes your current project after a separate confirmation. Neither action uses server-administrator authority.");
            UIText(uiOwnedRooms.transform, "Recovery Help", "After a failed or canceled creation or replacement, use Check to learn its outcome. No mutation is resent automatically. Keep this private profile to retain ownership.", 12);
            UIButton(uiOwnedRooms.transform, "Refresh Created Rooms", "Refresh private room records", () => { createdRoomSignature = null; RefreshCreatedRoomsUI(); });
            uiCreatedRooms = UIColumn(uiOwnedRooms.transform, "Private Created Rooms");
            InitializeReplacementUI(uiOwnedRooms.transform);

            uiServerAdmin = UIColumn(form, "Server Admin Page").gameObject;
            UIText(uiServerAdmin.transform, "Help", "Server administrator: inspect registered rooms, availability and process / room usage. This credential does not grant room membership or owner participant controls.");
            uiAdminAddress = UIField(uiServerAdmin.transform, "Administration server address", endpoint, 256, value => endpoint = value);
            uiServerAdminKey = UIField(uiServerAdmin.transform, "Server administrator key (private)", "", 64, value => serverAdminKey = value, true);
            UIButton(uiServerAdmin.transform, "Refresh Server Rooms", "Inspect server rooms and resources", () => _ = InspectServerAsync(), true);
            UIButton(uiServerAdmin.transform, "Admin Create Room", "Create a room as administrator...", () => { creationAuthority = 2; uiCreationAuthority.SetValueWithoutNotify(2); ClearRoomKeys(); SelectUITab(3); });
            UIText(uiServerAdmin.transform, "Supported Operations", "Room owners manage participants after joining, and can deliberately replace an owned room from their private room records. Server administration grants neither ownership nor replacement authority. Room deletion, password changes and role changes are unavailable.", 12);
            uiServerRooms = UIText(uiServerAdmin.transform, "Server Rooms", "Enter the separate server administrator key to inspect this server.", 12);
        }
        private void ClearRoomKeys() {
            creationKey = serverAdminKey = "";
            uiCreationKey?.SetTextWithoutNotify(""); uiServerAdminKey?.SetTextWithoutNotify("");
        }
        private void CancelRoomOperation() => roomOperation?.Cancel();
        private async Task RunRoomOperationAsync(string message, Func<CancellationToken, Task> work, bool replacing = false) {
            if (busy || IsShared || NativeFileDialogs.IsOpen || FindFirstObjectByType<BuildsManager>()?.IsFileOperationInProgress == true) return;
            busy = true; status = message; nextUIRefresh = 0;
            var active = new CancellationTokenSource(); roomOperation = active;
            try { await work(active.Token); }
            catch (OperationCanceledException) { status = replacing
                ? "Replacement operation canceled. Any prepared attempt is retained; use Check to learn its outcome. Your project and local file are unchanged."
                : "Room operation canceled. Any prepared owner record is retained. Use Check before another creation; your project and local file are unchanged."; }
            catch (Exception error) {
                string code = error is CollaborationException known ? known.Code : WireFailure.Code(error);
                status = CreationFeedback(code) + " Your project and local file are unchanged." + (replacing
                    ? " If an attempt was prepared, use Check; replacement is never resent automatically."
                    : code == "room-creation-key-required" || code == "server-admin-required" ? " Correct the authority before a new explicit confirmation." : " If a private record was prepared, use Check; do not resend creation.");
            }
            finally {
                roomOperation = null; active.Dispose(); ClearRoomKeys(); ClearPassword(); busy = false; nextUIRefresh = 0;
                createdRoomSignature = null;
            }
        }
        private Task InspectCreationAsync() => RunRoomOperationAsync("Checking server creation policy…", async token => {
            using var remote = new RemoteRoomCreation(endpoint, lan, await SelectedNetworkAsync(token));
            var caps = await remote.InspectAsync(token);
            uiCreationPolicy.text = "Server creation policy: " + caps.roomCreationPolicy + ". Maximum eight registered rooms; no public room directory. Separate room ownership is retained privately.";
            status = "Creation policy checked. Choose its matching authority before creating.";
        });
        private Task CreateRemoteRoomAsync() {
            uiCreateConfirmation.SetActive(false);
            return RunRoomOperationAsync("Preparing current build for a new shared-server room…", async token => {
                ValidateSettings(true); CancelTools();
                var content = CaptureRoomExport();
                DocumentModel.Require(creationAuthority != 1 || string.IsNullOrEmpty(creationKey), "open-creation-needs-no-key");
                using var remote = new RemoteRoomCreation(endpoint, lan, await SelectedNetworkAsync(token));
                var caps = await remote.InspectAsync(token);
                DocumentModel.Require(creationAuthority != 1 || caps.roomCreationPolicy == "open", "room-creation-key-required");
                DocumentModel.Require(creationAuthority != 0 || caps.roomCreationPolicy == "key", "creation-authority-mismatch");
                var record = await remote.CreateAsync(content, room, password, creationAuthority == 1 ? null : creationKey, creationAuthority == 2, CreatedRoomsRoot, token);
                status = "Room " + record.room + " created. You own this room; your local project is still open. Choose Join as owner to start sharing.";
                SelectUITab(5);
            });
        }
        private Task InspectServerAsync() => RunRoomOperationAsync("Inspecting server rooms and resources…", async token => {
            using var remote = new RemoteRoomCreation(endpoint, lan, await SelectedNetworkAsync(token));
            var result = await remote.ListAsync(serverAdminKey, token);
            string Metric(ResourceMetric m) => m == null ? "unavailable" : m.used + " / " + m.limit;
            string Bytes(ResourceMetric m) => m == null ? "unavailable" : $"{m.used / 1048576.0:0.00} / {m.limit / 1048576.0:0.00} MiB";
            var process = result.process;
            uiServerRooms.text = "Server snapshot: " + result.rooms.Length + " / 8 registered rooms\n" + (process == null ? "Process usage unavailable\n" :
                "Requests " + Metric(process.requests) + "; event streams " + Metric(process.eventStreams) + "; transfers " + Metric(process.transfers) +
                "\nLogical retained geometry " + Bytes(process.retainedGeometryBytes) + "; decoded " + Bytes(process.decodedArrayBytes) + "; live instances " + Bytes(process.liveInstanceArrayBytes) +
                $"\nMeasured managed / working set: {process.measuredManagedBytes / 1048576.0:0.00} / {process.measuredWorkingSetBytes / 1048576.0:0.00} MiB\n") +
                string.Join("\n\n", result.rooms.Select(r => {
                    var safe = DiagnosticExport.Room(new RoomDiagnostic { room = r.room, generation = r.generation, catalogHash = r.catalogHash,
                        revision = r.revision, status = r.status, action = r.action, usage = r.usage });
                    return safe.room + " | " + safe.status + " | revision " + (safe.revision?.ToString() ?? "unavailable") +
                        "\n" + safe.action + (safe.usage == null ? "\nUsage unavailable" : "\nParticipants " + Metric(safe.usage.participants) + "; geometry " + Bytes(safe.usage.retainedGeometryBytes) + "; history " + Bytes(safe.usage.historyBytes) + "; receipts " + Metric(safe.usage.receipts));
                }));
            status = "Server administrator inspection complete. No membership was created.";
        });
        private void RefreshCreatedRoomsUI() {
            if (uiCreatedRooms == null || createdRoomSignature != null) return;
            CreatedRoom[] records;
            bool unreadable;
            try { records = CreatedRoomRecovery.List(CreatedRoomsRoot, out unreadable); }
            catch { records = Array.Empty<CreatedRoom>(); unreadable = true; }
            string signature = string.Join("|", records.Select(r => r.origin + ":" + r.room + ":" + r.confirmed));
            if (signature == createdRoomSignature) return;
            createdRoomSignature = signature;
            foreach (Transform child in uiCreatedRooms) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            if (unreadable) UIText(uiCreatedRooms, "Unreadable", "Some private ownership records cannot be read. Their files are retained; no creation will be resent.");
            var attempts = ReplacementRecords(out bool unreadableAttempts);
            if (unreadableAttempts) UIText(uiCreatedRooms, "Unreadable Replacement Records", "Some private replacement records cannot be read. Their files and operation IDs are retained; no replacement is resent.");
            if (records.Length == 0) {
                UIText(uiCreatedRooms, "No Owned Rooms", "No owned rooms in this profile. Create a room to retain its owner credential here. Locally hosted rooms are on Local rooms; a server-administrator key does not recover room ownership.");
                UIButton(uiCreatedRooms, "Create First Owned Room", "Create a new room...", () => SelectUITab(3));
            }
            foreach (var record in records) {
                var row = UIColumn(uiCreatedRooms, "Created " + record.room);
                UIText(row, "Room", record.room + " | " + (record.confirmed ? "created" : record.authorizationRefused ? "authorization refused; correct the key and confirm Create again" : "prepared; outcome unconfirmed") + "\n" + record.origin, 12);
                var actions = UIRow(row, "Owner Actions");
                UIButton(actions, "Check Created " + record.room, "Check room", () => _ = CheckCreatedRoomAsync(record));
                UIButton(actions, "Join Owner " + record.room, "Join as owner...", () => RequestOwnerJoin(record));
                AddReplacementActions(row, record, attempts);
            }
        }
        private Task CheckCreatedRoomAsync(CreatedRoom record) => RunRoomOperationAsync("Checking prepared room ownership…", async token => {
            using var remote = new RemoteRoomCreation(record.origin, lan, await SelectedNetworkAsync(token));
            await remote.CheckAsync(record, token);
            record.confirmed = true; RecoveryFiles.Write(CreatedRoomRecovery.FileName(CreatedRoomsRoot, record.origin, record.room), record, CreatedRoomRecovery.MaxBytes);
            status = "Room " + record.room + " exists with your retained ownership. Join as owner when ready.";
        });
        private void RequestOwnerJoin(CreatedRoom record) {
            if (busy || IsShared) return;
            ownerJoin = record; endpoint = record.origin; room = record.room;
            RequestJoinCore();
        }
        private static string CreationFeedback(string code) => code switch {
            "room-replacement-unavailable" => "This helper does not support durable owner-authorized replacement. Use a compatible updated helper and the room's matching catalog.",
            "replacement-target-changed" => "The online room changed after it was inspected. Inspect it again before deciding whether to replace.",
            "room-replacement-limit" => "This room has reached its retained replacement-outcome limit. Existing outcomes and content remain intact.",
            "replacement-record-limit" => "This profile retains 64 replacement attempts. Preserve its private attempt files before moving completed records elsewhere.",
            "replacement-record-in-use" => "Another window is using this replacement record. Wait for it to settle, then use Check.",
            "invalid-replacement-record" => "The private replacement record could not be validated. Its file and operation identity are retained.",
            "room-creation-unavailable" => "This server does not advertise shared-server room creation v1. Update the helper with its matching release, or use an existing room.",
            "server-administration-unavailable" => "This address does not advertise a compatible multi-room server. Check its address and helper version.",
            "server-admin-required" => "Only the separate server administrator credential can perform this operation.",
            "room-creation-key-required" => "The server requires its room creation key, or the chosen open policy does not match.",
            "creation-authority-mismatch" => "Choose the authority matching the server's creation policy.",
            "invalid-creation-credential" => "Enter the 64-character private creation or administrator key.",
            "created-room-record-exists" => "A private record already exists for this address and room. Use Check or Join as owner.",
            "created-room-record-in-use" => "Another window is using this room's private ownership record. Wait for it to finish, then use Check.",
            "invalid-created-room-record" => "The private ownership record could not be validated. Its file is retained; no creation was sent.",
            "created-room-record-limit" => "This profile has reached its limit of 32 retained ownership records.",
            "created-room-mismatch" => "The server or room identity differs from the retained owner record. Check the address and matching build.",
            "room-already-registered" => "That room ID is already registered. Existing content cannot be overwritten.",
            "server-room-limit" => "This server has reached its limit of eight registered rooms.",
            "server-creation-busy" => "Another room is being created. Wait, then check your prepared record before trying a new room.",
            _ => FriendlyStatus(WireFailure.Code(code) ?? "room-operation-failed")
        };
    }
}
