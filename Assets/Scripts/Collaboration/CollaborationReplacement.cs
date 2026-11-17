using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        private GameObject uiReplacementConfirmation, uiResyncConfirmation, uiJoinResyncConfirmation;
        private Text uiReplacementWarning;
        private Button uiResynchronize, uiJoinResynchronize;
        private CreatedRoom replacementOwner;
        private RoomReplacementTarget replacementTarget;
        private RoomExport replacementContent;
        private string resyncJoinOrigin, resyncJoinRoom;
        private bool acceptReplacementJoin;
        private static string ReplacementRecordsRoot => Path.Combine(CollaborationRecovery.Root, "room-replacements");
        private bool NeedsSceneResynchronization => client != null && (client.RequiresResynchronization ||
            committed != null && client.AcceptedGeneration != null && committed.generation != client.AcceptedGeneration);

        private void InitializeReplacementUI(Transform parent) {
            uiReplacementConfirmation = UIColumn(parent, "Confirm Online Replacement").gameObject;
            uiReplacementWarning = UIText(uiReplacementConfirmation.transform, "Replacement Warning", "");
            var actions = UIRow(uiReplacementConfirmation.transform, "Replacement Actions");
            UIButton(actions, "Confirm Replace Online Room", "Replace room content", () => _ = ConfirmReplacementAsync(), true);
            UIButton(actions, "Cancel Replace Online Room", "Keep online room", ClearReplacementConfirmation);
            uiReplacementConfirmation.SetActive(false);
        }
        private void ClearReplacementConfirmation() {
            replacementOwner = null; replacementTarget = null; replacementContent = null;
            if (uiReplacementConfirmation != null) uiReplacementConfirmation.SetActive(false);
        }
        private Task InspectReplacementAsync(CreatedRoom record) => RunRoomOperationAsync("Checking owned room before replacement…", async token => {
            CancelTools();
            var content = CaptureRoomExport();
            var network = await SelectedNetworkAsync(token);
            using (var ownership = new RemoteRoomCreation(record.origin, lan, network)) await ownership.CheckAsync(record, token);
            using var remote = new RemoteRoomReplacement(record.origin, lan, network);
            var target = await remote.InspectAsync(record.room, record.ownerCredential, token);
            DocumentModel.Require(target.roomIdentity == record.generation && target.catalogHash == record.catalogHash && content.catalog.hash == target.catalogHash,
                "catalog-or-room-mismatch");
            token.ThrowIfCancellationRequested();
            replacementOwner = record; replacementTarget = target; replacementContent = content;
            uiReplacementWarning.text = "Replace online room \"" + record.room + "\" at\n" + record.origin + "?\n\n" +
                "Current online revision: " + target.revision + ". Publish this accepted local project: " + content.document.parts.Length + " parts, " +
                content.document.chains.Length + " chains, " + content.document.assets.Length + " custom models.\n\n" +
                "This starts a new generation and clears everyone's personal Undo history. Participants pause until they choose Resynchronize. " +
                "Any intervening online edit rejects this confirmation. Your local project, file and saved state stay unchanged.";
            uiReplacementConfirmation.SetActive(true); Canvas.ForceUpdateCanvases(); uiScroll.verticalNormalizedPosition = 0;
            status = "Owned target checked. Review the exact room and local project before confirming replacement.";
        }, replacing: true);
        private Task ConfirmReplacementAsync() {
            var record = replacementOwner; var target = replacementTarget; var content = replacementContent;
            if (record == null || target == null || content == null || busy || IsShared) return Task.CompletedTask;
            ClearReplacementConfirmation();
            return RunRoomOperationAsync("Publishing the confirmed room replacement…", async token => {
                using var remote = new RemoteRoomReplacement(record.origin, lan, await SelectedNetworkAsync(token));
                var result = await remote.ReplaceInProfileAsync(content, target, record.ownerCredential, ReplacementRecordsRoot, token);
                status = "Replaced room " + result.room + " at revision " + result.revision + ". Your local project and file remain open. Join as owner when ready; existing participants must resynchronize.";
            }, replacing: true);
        }
        private static (string file, RoomReplacementAttempt record)[] ReplacementRecords(out bool unreadable) {
            unreadable = false;
            if (!Directory.Exists(ReplacementRecordsRoot)) return Array.Empty<(string, RoomReplacementAttempt)>();
            var files = Directory.EnumerateFiles(ReplacementRecordsRoot, "*.json").Take(RemoteRoomReplacement.MaxRecords + 1).ToArray();
            if (files.Length > RemoteRoomReplacement.MaxRecords) { unreadable = true; return Array.Empty<(string, RoomReplacementAttempt)>(); }
            var records = new List<(string, RoomReplacementAttempt)>();
            foreach (string file in files) { try { records.Add((file, RemoteRoomReplacement.ReadAttempt(file))); } catch { unreadable = true; } }
            return records.ToArray();
        }
        private void AddReplacementActions(Transform row, CreatedRoom owner, (string file, RoomReplacementAttempt record)[] attempts) {
            UIButton(row, "Replace Owned " + owner.room, "Replace online room with this project...", () => _ = InspectReplacementAsync(owner));
            foreach (var attempt in attempts.Where(a => a.record.origin == owner.origin && a.record.target.room == owner.room && a.record.target.roomIdentity == owner.generation)) {
                string file = attempt.file;
                UIText(row, "Replacement Outcome", "Replacement " + (attempt.record.confirmed ? "accepted at revision " + attempt.record.receipt.revision :
                    attempt.record.notAccepted ? "not accepted; inspect the current target before a new confirmation" : "outcome unconfirmed; private attempt retained"), 12);
                UIButton(row, "Check Replacement " + owner.room + " " + attempt.record.operationId, "Check replacement outcome", () => _ = CheckReplacementAsync(owner, file));
            }
        }
        private Task CheckReplacementAsync(CreatedRoom owner, string file) => RunRoomOperationAsync("Checking the retained replacement outcome…", async token => {
            using var remote = new RemoteRoomReplacement(owner.origin, lan, await SelectedNetworkAsync(token));
            var outcome = await remote.CheckAsync(file, owner.ownerCredential, token);
            var record = RemoteRoomReplacement.ReadAttempt(file);
            status = outcome.found ? "Replacement was accepted at revision " + outcome.receipt.revision + ". Join as owner to open the current online content." :
                record.notAccepted ? "Replacement was not accepted, and its old target can no longer be overwritten. Inspect the current room before another confirmation." :
                "No authoritative replacement result is available yet. Its private attempt is kept; check again before deciding whether to replace.";
        }, replacing: true);
        private void InitializeResynchronizationUI() {
            uiResynchronize = UIButton(uiConnected.transform, "Resynchronize Replaced Room", "Resynchronize replaced room...", () => uiResyncConfirmation.SetActive(true), true);
            uiResyncConfirmation = UIColumn(uiConnected.transform, "Confirm Room Resynchronization").gameObject;
            UIText(uiResyncConfirmation.transform, "Resync Warning", "The owner replaced this room's content. Check your old pending edit outcomes and open the new accepted generation? Your local file path, saved bytes, camera and save marker stay independent. Personal Undo starts in the new generation.");
            var actions = UIRow(uiResyncConfirmation.transform, "Resync Actions");
            UIButton(actions, "Confirm Resynchronize Room", "Resynchronize", () => { uiResyncConfirmation.SetActive(false); _ = ResynchronizeAsync(); }, true);
            UIButton(actions, "Cancel Resynchronize Room", "Keep last received build", () => uiResyncConfirmation.SetActive(false));
            uiResyncConfirmation.SetActive(false);
            uiJoinResynchronize = UIButton(uiJoin.transform, "Resynchronize Saved Room", "Join replaced room...", () => uiJoinResyncConfirmation.SetActive(true));
            uiJoinResyncConfirmation = UIColumn(uiJoin.transform, "Confirm Saved Room Resynchronization").gameObject;
            UIText(uiJoinResyncConfirmation.transform, "Join Resync Warning", "Open this room's new generation using your retained membership and check old pending outcomes? Normal unsaved-project handling applies. Your membership is kept; old commands are never replayed.");
            var joinActions = UIRow(uiJoinResyncConfirmation.transform, "Join Resync Actions");
            UIButton(joinActions, "Confirm Join Replaced Room", "Open new generation", () => { uiJoinResyncConfirmation.SetActive(false); ownerJoin = null; acceptReplacementJoin = true; RequestJoinCore(); }, true);
            UIButton(joinActions, "Cancel Join Replaced Room", "Keep current project", () => uiJoinResyncConfirmation.SetActive(false));
            uiJoinResyncConfirmation.SetActive(false);
        }
        private Task ResynchronizeAsync() => ReconnectCoreAsync(true);
        private void RefreshResynchronizationUI() {
            bool needs = NeedsSceneResynchronization;
            uiResynchronize.gameObject.SetActive(needs && client != null && !client.IsMembershipRevoked); uiResynchronize.interactable = !busy;
            uiJoinResynchronize.gameObject.SetActive(!IsShared && resyncJoinOrigin == endpoint && resyncJoinRoom == room); uiJoinResynchronize.interactable = !busy;
            if (!needs) uiResyncConfirmation.SetActive(false);
        }
    }
}
