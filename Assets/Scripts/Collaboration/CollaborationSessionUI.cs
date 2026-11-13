using System;
using System.IO;
using System.Linq;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Protobot {
    // Runtime-only UI: the production scene and ordinary offline builds need no migration.
    public sealed partial class CollaborationSession {
        private GameObject uiRoot, uiModal, uiTabs, uiHost, uiJoin, uiRecovery, uiConnected, uiLeaveConfirmation;
        private RectTransform uiCard, uiContent, uiBadge;
        private CanvasGroup uiFormGroup, uiUtilityGroup;
        private Text uiBadgeText, uiStatus, uiConnection, uiMembers, uiSaveStatus, uiRecoveryNotice, uiLeaveWarning, uiReachability;
        private Text uiDiagnostics, uiTestReport;
        private InputField uiName, uiRoom, uiPassword, uiEndpoint, uiPort;
        private Toggle uiLan, uiViewer;
        private Dropdown uiTransport;
        private Button uiReconnect, uiSave, uiSaveAs, uiLeave;
        private Transform uiRecoveryRows;
        private ScrollRect uiScroll;
        private int uiTab;
        private bool joinAsViewer, uiDiagnosticsVisible;
        private float nextUIRefresh;
        private string recoverySignature;
        private readonly Color uiAccent = new Color(.14f, .5f, .75f);

        private bool ContainsUIPointer => uiRoot != null && (uiModal.activeSelf || RectTransformUtility.RectangleContainsScreenPoint(uiBadge, MouseInput.Position));
        internal static bool OwnsUIFocus(GameObject selected) => instance != null && instance.uiRoot != null && selected != null && selected.transform.IsChildOf(instance.uiRoot.transform);

        private void InitializeUI() {
            uiRoot = new GameObject("Collaboration UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = uiRoot.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 220;
            var scaler = uiRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900); scaler.matchWidthOrHeight = .5f;

            var badge = UIButton(uiRoot.transform, "Collaboration", "Collaboration (alpha)", () => { if (panel) ClosePanel(); else OpenPanel(); });
            uiBadge = (RectTransform)badge.transform;
            uiBadge.anchorMin = uiBadge.anchorMax = uiBadge.pivot = Vector2.one;
            uiBadge.anchoredPosition = new Vector2(-14, -214); uiBadge.sizeDelta = new Vector2(235, 36);
            uiBadgeText = badge.GetComponentInChildren<Text>();

            uiModal = UIRect("Collaboration Window", uiRoot.transform).gameObject;
            UIStretch((RectTransform)uiModal.transform); UIImage(uiModal.transform, new Color(0, 0, 0, .58f));
            uiCard = UIRect("Window", uiModal.transform); uiCard.anchorMin = uiCard.anchorMax = uiCard.pivot = new Vector2(.5f, .5f);
            UIImage(uiCard, new Color(.055f, .065f, .08f, 1));
            var title = UIText(uiCard, "Title", "Collaboration", 23); UIPlace(title.rectTransform, 22, 12, -90, 34, true);
            var subtitle = UIText(uiCard, "Subtitle", "Development alpha  |  Shared build editing", 13); UIPlace(subtitle.rectTransform, 22, 49, -44, 26, true);
            var close = UIButton(uiCard, "Close", "Close", ClosePanel); UIPlace((RectTransform)close.transform, -86, 14, 66, 32);
            ((RectTransform)close.transform).anchorMin = ((RectTransform)close.transform).anchorMax = Vector2.one;

            uiTabs = UIRow(uiCard, "Pages").gameObject; UIPlace((RectTransform)uiTabs.transform, 22, 83, -44, 36, true);
            UIButton(uiTabs.transform, "Host Tab", "Host", () => SelectUITab(0));
            UIButton(uiTabs.transform, "Join Tab", "Join", () => SelectUITab(1));
            UIButton(uiTabs.transform, "Recovery Tab", "Recovered rooms", () => { RefreshRecoveredRooms(); SelectUITab(2); });

            var viewport = UIRect("Scroll View", uiCard); viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(22, 103); viewport.offsetMax = new Vector2(-22, -130);
            UIImage(viewport, new Color(0, 0, 0, .04f)); viewport.gameObject.AddComponent<RectMask2D>();
            uiContent = UIRect("Content", viewport); uiContent.anchorMin = Vector2.up; uiContent.anchorMax = Vector2.one; uiContent.pivot = Vector2.up; uiContent.sizeDelta = Vector2.zero;
            UILayout(uiContent, 12); var fitter = uiContent.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            uiScroll = viewport.gameObject.AddComponent<ScrollRect>(); uiScroll.viewport = viewport; uiScroll.content = uiContent;
            uiScroll.horizontal = false; uiScroll.movementType = ScrollRect.MovementType.Clamped; uiScroll.scrollSensitivity = 28;
            var scrollTrack = UIRect("Scrollbar", uiCard); scrollTrack.anchorMin = Vector2.right; scrollTrack.anchorMax = Vector2.one;
            scrollTrack.offsetMin = new Vector2(-14, 103); scrollTrack.offsetMax = new Vector2(-7, -130);
            UIImage(scrollTrack, new Color(.1f, .13f, .17f));
            var handle = UIRect("Handle", scrollTrack); UIStretch(handle); var handleImage = UIImage(handle, new Color(.36f, .44f, .53f));
            var scrollbar = scrollTrack.gameObject.AddComponent<Scrollbar>(); scrollbar.handleRect = handle; scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop; scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
            uiScroll.verticalScrollbar = scrollbar; uiScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            var form = UIColumn(uiContent, "Setup"); uiFormGroup = form.gameObject.AddComponent<CanvasGroup>();
            uiHost = UIColumn(form, "Host Page").gameObject; uiJoin = UIColumn(form, "Join Page").gameObject; uiRecovery = UIColumn(form, "Recovery Page").gameObject;

            UIText(uiHost.transform, "Help", "Publish the current accepted build. Each participant saves their own local file; accepted edits are stored for room recovery.");
            // Common settings stay in one place when switching between Host, Join and recovery.
            var settings = UIColumn(form, "Settings"); settings.SetAsFirstSibling();
            var identity = UIRow(settings, "Identity");
            uiName = UIField(UIColumn(identity, "Your name"), "Your name", displayName, 48, value => displayName = value);
            uiRoom = UIField(UIColumn(identity, "Room ID"), "Room ID", room, 80, value => room = value);
            uiPassword = UIField(settings, "Room password (optional)", "", 128, value => password = value, true);
            uiLan = UIToggle(settings, "Allow LAN HTTP", "Allow unencrypted LAN HTTP", value => lan = value);
            uiLan.isOn = lan;
            UIText(settings, "LAN Help", "Leave off for HTTPS or same-computer testing. LAN hosting requires a reachable address and firewall permission.", 12);

            uiPort = UIField(uiHost.transform, "Host port (0 chooses a free port)", port, 5, value => { port = value; portEdited = true; });
            UIButton(uiHost.transform, "Start Hosting", "Host current build", () => _ = HostAsync(), true);
            UIText(uiJoin.transform, "Help", "Joining opens the room after the normal Save / Discard prompt. Your first local save uses Save As.");
            uiEndpoint = UIField(uiJoin.transform, "Server address", endpoint, 256, value => endpoint = value);
            UIButton(uiJoin.transform, "Paste Invite", "Paste invite from clipboard", PasteInvite);
            uiViewer = UIToggle(uiJoin.transform, "Join as Viewer", "Join as viewer (read-only)", value => joinAsViewer = value);
            UIText(uiJoin.transform, "Viewer Help", "A saved membership keeps its existing role and personal history. Forget it below to join as a new member.", 12);
            uiTransport = UIDropdown(uiJoin.transform, "Connection transport", new[] { "Auto (WebSocket, then requests)", "WebSocket only", "HTTPS / HTTP requests only" }, (int)transport, value => transport = (CollaborationTransport)value);
            UIButton(uiJoin.transform, "Join Room", "Join room", RequestJoin, true);
            UIButton(uiJoin.transform, "Test Connection", "Test server connection (read-only)", () => _ = TestServerAsync());
            uiTestReport = UIText(uiJoin.transform, "Connection Test Results", "", 12);
            UIButton(uiJoin.transform, "Forget Membership", "Forget saved membership for this address / room", () => {
                try { ValidateSettings(true); CollaborationClient.ForgetRecovery(CollaborationRecovery.SessionFile(endpoint, room, CollaborationScene.Catalog.hash)); status = "Saved identity cleared. Join will create a new membership."; }
                catch (Exception error) { Report(error); }
            });

            UIText(uiRecovery.transform, "Help", "Reopen a locally hosted room from its accepted recovery data. This uses Save As and starts a listener only after you choose Resume.");
            UIText(uiRecovery.transform, "Settings Help", "Your name and LAN setting apply. The saved port is reused unless you change the host port on the Host tab.", 12);
            uiRecoveryNotice = UIText(uiRecovery.transform, "Recovery Notice", "");
            uiRecoveryRows = UIColumn(uiRecovery.transform, "Saved Rooms");
            UIButton(uiRecovery.transform, "Refresh Rooms", "Refresh recovered rooms", () => { RefreshRecoveredRooms(); recoverySignature = null; });

            uiConnected = UIColumn(uiContent, "Session Page").gameObject;
            uiConnection = UIText(uiConnected.transform, "Connection", "", 18);
            uiReachability = UIText(uiConnected.transform, "Reachability", "", 13);
            var invite = UIRow(uiConnected.transform, "Invites");
            UIButton(invite, "Copy Invite", "Copy invite", () => { GUIUtility.systemCopyBuffer = CollaborationInvite.Format(endpoint, room); status = "Invite copied. Send the password separately, if required."; });
            UIButton(invite, "Copy Server", "Copy server address", () => { GUIUtility.systemCopyBuffer = endpoint; status = "Server address copied."; });
            uiMembers = UIText(uiConnected.transform, "Participants", "");
            uiSaveStatus = UIText(uiConnected.transform, "Local File", "");
            var saves = UIRow(uiConnected.transform, "Local Save");
            uiSave = UIButton(saves, "Save Local", "Save local file", () => { ClosePanel(); FindFirstObjectByType<BuildsManager>().Save(); });
            uiSaveAs = UIButton(saves, "Save Local As", "Save local file as...", () => { ClosePanel(); FindFirstObjectByType<BuildsManager>().SaveAs(); });
            UIText(uiConnected.transform, "Coverage", "Available: " + string.Join(", ", mutations) + ".\nCustom geometry and existing mounting points are shared; live mounting-point edits are not yet shared.", 12);
            uiReconnect = UIButton(uiConnected.transform, "Reconnect", "Reconnect", () => _ = ReconnectAsync(), true);
            uiLeave = UIButton(uiConnected.transform, "Leave", "Leave and continue offline...", () => uiLeaveConfirmation.SetActive(true));
            uiLeaveConfirmation = UIColumn(uiConnected.transform, "Confirm Leave").gameObject;
            uiLeaveWarning = UIText(uiLeaveConfirmation.transform, "Warning", "");
            var leaveActions = UIRow(uiLeaveConfirmation.transform, "Actions");
            UIButton(leaveActions, "Confirm Leave", "Continue offline", () => { Detach(); uiLeaveConfirmation.SetActive(false); SelectUITab(0); });
            UIButton(leaveActions, "Cancel Leave", "Stay in room", () => uiLeaveConfirmation.SetActive(false));
            uiLeaveConfirmation.SetActive(false);

            var testing = UIColumn(uiContent, "Testing");
            uiUtilityGroup = testing.gameObject.AddComponent<CanvasGroup>();
            UIButton(testing, "Export Room", "Export current build for CLI...", () => _ = ExportRoomAsync());
            var another = UIButton(testing, "Open Local Player", "Open another local test window", OpenLocalPlayer);
            another.interactable = !Application.isEditor;
            UIText(testing, "Help", Application.isEditor ? "Additional test windows are available in the built desktop app." : "Uses a separate local profile. In that window, choose Join and enter the password if required.", 12);
            UIButton(testing, "Diagnostics", "Show / hide connection details", () => uiDiagnosticsVisible = !uiDiagnosticsVisible);
            uiDiagnostics = UIText(testing, "Details", "", 12); uiDiagnostics.gameObject.SetActive(false);

            var footer = UIRect("Footer", uiCard); footer.anchorMin = Vector2.zero; footer.anchorMax = Vector2.right;
            footer.pivot = Vector2.zero; footer.anchoredPosition = new Vector2(22, 12); footer.sizeDelta = new Vector2(-44, 78);
            uiStatus = UIText(footer, "Status", status, 13); UIStretch(uiStatus.rectTransform);
            SelectUITab(0); uiModal.SetActive(false);
        }

        private void OpenPanel(int tab = -1) {
            if (!panel) { CancelTools(); ClearUIFocus(); uiLeaveConfirmation.SetActive(false); }
            if (tab >= 0) SelectUITab(tab);
            panel = true; nextUIRefresh = 0; RefreshUI();
        }
        private void ClosePanel() {
            connectionTest?.Cancel();
            panel = false; ClearUIFocus();
            if (uiModal != null) uiModal.SetActive(busy);
        }
        private void SelectUITab(int tab) {
            ClearUIFocus(); uiTab = tab;
            uiHost.SetActive(tab == 0); uiJoin.SetActive(tab == 1); uiRecovery.SetActive(tab == 2);
            uiScroll.verticalNormalizedPosition = 1;
            for (int i = 0; i < uiTabs.transform.childCount; i++) uiTabs.transform.GetChild(i).GetComponent<Image>().color = i == tab ? uiAccent : new Color(.15f, .19f, .24f);
        }
        private void ClearUIFocus() {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected != null && selected.TryGetComponent<InputField>(out var field)) field.DeactivateInputField();
            EventSystem.current?.SetSelectedGameObject(null);
        }
        private void ClearPassword() { password = ""; if (uiPassword != null) uiPassword.SetTextWithoutNotify(""); }
        private void ValidateSettings(bool joining) {
            displayName = displayName.Trim(); room = room.Trim();
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 48 || displayName.Any(char.IsControl)) throw new ArgumentException("Enter a name of 1 to 48 characters.");
            if (!DocumentModel.ValidId(room)) throw new ArgumentException("Room ID must use 1 to 80 letters, numbers, hyphens or underscores.");
            if (joining) endpoint = CollaborationInvite.ValidateOrigin(endpoint);
        }
        private void PasteInvite() {
            try {
                CollaborationInvite.Parse(GUIUtility.systemCopyBuffer, out var address, out var roomId);
                endpoint = address; room = roomId;
                uiEndpoint.SetTextWithoutNotify(endpoint); uiRoom.SetTextWithoutNotify(room);
                status = "Invite filled in. Check LAN HTTP if needed, enter the password, then Join.";
            }
            catch (Exception error) { Report(error); }
        }
        private void OpenLocalPlayer() {
            try {
                string address = host?.Url ?? CollaborationInvite.ValidateOrigin(endpoint);
                string profile = Path.Combine(CollaborationRecovery.Root, "TestWindows", Guid.NewGuid().ToString("N"));
                using var child = CollaborationTestWindow.Launch(profile, address, room);
                status = "Opened an independent test window. Its profile will retain its recovery identity.";
            }
            catch (Exception error) { Report(error); }
        }

        private void LateUpdate() { if (Time.unscaledTime >= nextUIRefresh) { nextUIRefresh = Time.unscaledTime + .25f; RefreshUI(); } }
        private void RefreshUI() {
            if (uiRoot == null) return;
            var canvasRect = (RectTransform)uiRoot.transform;
            uiCard.sizeDelta = new Vector2(Mathf.Min(710, canvasRect.rect.width - 24), Mathf.Min(760, canvasRect.rect.height - 28));
            uiModal.SetActive(panel || busy);
            string shortRoom = room.Length > 18 ? room.Substring(0, 16) + "..." : room;
            uiBadgeText.text = busy ? "Collaboration | working..." : !IsShared ? "Collaboration (alpha)" : client.IsConnected ? $"{shortRoom} | {Math.Max(1, participants.Length)} online" : "Collaboration | disconnected";
            if (!uiModal.activeSelf) return;
            uiTabs.SetActive(!IsShared); uiFormGroup.gameObject.SetActive(!IsShared); uiConnected.SetActive(IsShared);
            uiFormGroup.interactable = !busy; uiFormGroup.blocksRaycasts = !busy;
            uiUtilityGroup.interactable = !busy;
            foreach (var button in uiTabs.GetComponentsInChildren<Button>()) button.interactable = !busy;
            // Connection details use the existing session; changing setup cannot retarget a live client.
            uiName.SetTextWithoutNotify(displayName); uiRoom.SetTextWithoutNotify(room); uiEndpoint.SetTextWithoutNotify(endpoint);
            uiStatus.text = FriendlyStatus(status);
            if (IsShared) {
                uiConnection.text = $"{room}  |  {(client.IsConnected ? "Connected" : "Disconnected - editing paused")}\nRevision {committed.revision}  |  {client.ActiveTransport ?? "connecting"}  |  {client.Participant.role}";
                uiReachability.text = "Server: " + endpoint + "\n" + (host != null ? host.AllowLan ? "LAN host. Share a reachable LAN address; internet access is not configured." : "Local host. Only players on this computer can connect." : "Connected to a remote host.") + "\n" + (client.IsDurable ? "Room recovery enabled." : "Temporary room. Save accepted content locally.");
                var members = participants.Length == 0 ? new[] { client.Participant } : participants;
                uiMembers.text = $"Participants ({members.Length}/10)\n" + string.Join("\n", members.Select(p => p.name + (p.id == client.Participant.id ? " (you)" : "") + "  -  " + p.role));
                var builds = FindFirstObjectByType<BuildsManager>();
                bool fileBusy = builds == null || builds.IsFileOperationInProgress || NativeFileDialogs.IsOpen;
                uiSaveStatus.text = "Your local file: " + (string.IsNullOrEmpty(builds?.buildPath) ? "Unsaved - first save uses Save As" : builds.GetFileName() + (builds.CurrentBuildIsSaved ? " - saved" : " - unsaved changes")) + (client.PendingCount > 0 ? $"\n{client.PendingCount} edit(s) awaiting an outcome." : "");
                uiSave.interactable = uiSaveAs.interactable = !busy && !fileBusy && client.PendingCount == 0;
                uiReconnect.gameObject.SetActive(!client.IsConnected); uiReconnect.interactable = !busy;
                uiReconnect.GetComponentInChildren<Text>().text = host != null && !host.IsRunning ? "Restart host and reconnect" : "Reconnect";
                uiLeave.interactable = !busy && !fileBusy;
                uiLeave.GetComponentInChildren<Text>().text = host != null ? "Stop hosting and continue offline..." : "Leave and continue offline...";
                uiLeaveWarning.text = (host != null ? "This disconnects everyone and stops the hosted room. Recovery data stays on this computer. " : "This ends your membership and its personal undo history. ") + "The last accepted document stays open offline; leaving does not save a local file." + (client.PendingCount > 0 ? " Pending edit outcomes are unresolved. Reconnect first to learn whether they were accepted." : "");
                // Never abandon an ambiguous submitted edit through the UI.
                uiLeaveConfirmation.GetComponentsInChildren<Button>().First().interactable = !busy && client.PendingCount == 0;
            }
            string signature = string.Join("|", recoveredRooms.Select(r => r.directory));
            if (signature != recoverySignature) {
                recoverySignature = signature;
                foreach (Transform child in uiRecoveryRows) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
                foreach (var saved in recoveredRooms) {
                    var captured = saved;
                    string date = DateTime.TryParse(saved.createdUtc, out var created) ? created.ToLocalTime().ToString("g") : saved.createdUtc;
                    UIButton(uiRecoveryRows, "Resume " + saved.room, "Resume " + saved.room + "  |  " + date, () => RequestRecovery(captured));
                }
            }
            uiRecoveryNotice.text = (recoveredRooms.Length == 0 ? "No local recovered rooms yet." : recoveredRooms.Length + " local room(s). Use the build that originally hosted each room.") + (unreadableRecovery ? "\nSome records could not be read; their files were retained." : "");
            uiDiagnostics.gameObject.SetActive(uiDiagnosticsVisible);
            if (uiDiagnosticsVisible) uiDiagnostics.text = "Protocol: " + CollaborationProtocol.Version + "\nCatalog: " + CollaborationScene.Catalog.hash + "\nTransport: " + (client?.ActiveTransport ?? transport.ToString()) + "\n" + (LocalPreferences.ProfileDirectory == null ? "Default local profile" : "Isolated local test profile") + "\nDetails: " + status;
            // Tab/arrow navigation stays within the modal, including the first and
            // last fields. A dropdown owns its own option navigation while expanded.
            if (uiTransport.transform.Find("Dropdown List") == null) {
                var controls = uiModal.GetComponentsInChildren<Selectable>().Where(s => s.IsInteractable() && !(s is Scrollbar)).ToArray();
                for (int i = 0; i < controls.Length; i++) {
                    var previous = controls[(i + controls.Length - 1) % controls.Length]; var next = controls[(i + 1) % controls.Length];
                    controls[i].navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = previous, selectOnLeft = previous, selectOnDown = next, selectOnRight = next };
                }
            }
        }

        private static string FriendlyStatus(string value) => value switch {
            "wrong-password" => "The room password was not accepted. Check it with the host and try again.",
            "catalog-or-room-mismatch" => "Room ID or catalog differs. Check the room ID and use the same build as the host.",
            "server-version-mismatch" => "The server uses a different collaboration version. Use matching builds and helpers.",
            "https-required-use-explicit-lan-http" => "Use an HTTPS address, or enable LAN HTTP for a trusted local-network test.",
            "room-full" => "This room has reached its participant limit.",
            "saved-room-does-not-match" => "This address now refers to a different room. Check the host, or forget the saved membership before joining anew.",
            "invalid-session" => "The saved membership is no longer valid. Forget it to join as a new member.",
            "recovery-catalog-mismatch-use-the-matching-build" => "This recovered room needs the original matching build and catalog.",
            _ => value
        };
        private void DisposeUI() { if (uiRoot != null) Destroy(uiRoot); }

        private static RectTransform UIRect(string name, Transform parent) {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false); return rect;
        }
        private static void UIStretch(RectTransform rect) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
        private static void UIPlace(RectTransform rect, float x, float y, float width, float height, bool stretch = false) {
            rect.anchorMin = Vector2.up; rect.anchorMax = stretch ? Vector2.one : Vector2.up; rect.pivot = Vector2.up;
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        }
        private static Image UIImage(Transform parent, Color color) { var image = parent.gameObject.AddComponent<Image>(); image.color = color; return image; }
        private static VerticalLayoutGroup UILayout(Transform parent, int spacing) {
            var layout = parent.gameObject.AddComponent<VerticalLayoutGroup>(); layout.spacing = spacing;
            layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false; return layout;
        }
        private static RectTransform UIColumn(Transform parent, string name) { var rect = UIRect(name, parent); UILayout(rect, 9); return rect; }
        private static RectTransform UIRow(Transform parent, string name) {
            var rect = UIRect(name, parent); var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>(); layout.spacing = 10;
            layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false; return rect;
        }
        private static Text UIText(Transform parent, string name, string text, int size = 14) {
            var rect = UIRect(name, parent); var label = rect.gameObject.AddComponent<Text>(); label.font = TransformFeedback.FeedbackFont;
            label.text = text; label.fontSize = size; label.color = new Color(.87f, .9f, .94f); label.raycastTarget = false; label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Overflow; return label;
        }
        private Button UIButton(Transform parent, string name, string text, UnityEngine.Events.UnityAction action, bool accent = false) {
            var rect = UIRect(name, parent); var image = UIImage(rect, accent ? uiAccent : new Color(.15f, .19f, .24f));
            var element = rect.gameObject.AddComponent<LayoutElement>(); element.minHeight = 36; element.flexibleWidth = 1;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; button.onClick.AddListener(action);
            var label = UIText(rect, "Label", text); UIStretch(label.rectTransform); label.rectTransform.offsetMin = new Vector2(10, 4); label.rectTransform.offsetMax = new Vector2(-10, -4); label.alignment = TextAnchor.MiddleCenter;
            return button;
        }
        private static InputField UIField(Transform parent, string label, string value, int limit, UnityEngine.Events.UnityAction<string> changed, bool secret = false) {
            var column = UIColumn(parent, label + " Field"); UIText(column, "Label", label, 13);
            var rect = UIRect(label, column); UIImage(rect, new Color(.11f, .14f, .18f)); rect.gameObject.AddComponent<LayoutElement>().minHeight = 36;
            var field = rect.gameObject.AddComponent<InputField>(); var text = UIText(rect, "Value", "", 15); UIStretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(10, 5); text.rectTransform.offsetMax = new Vector2(-10, -5); text.alignment = TextAnchor.MiddleLeft; text.verticalOverflow = VerticalWrapMode.Truncate;
            field.textComponent = text; field.targetGraphic = rect.GetComponent<Image>(); field.characterLimit = limit; field.contentType = secret ? InputField.ContentType.Password : InputField.ContentType.Standard;
            field.SetTextWithoutNotify(value); field.onValueChanged.AddListener(changed); field.selectionColor = new Color(.2f, .6f, 1, .6f);
            return field;
        }
        private static Toggle UIToggle(Transform parent, string name, string label, UnityEngine.Events.UnityAction<bool> changed) {
            var rect = UIRect(name, parent); rect.gameObject.AddComponent<LayoutElement>().minHeight = 30;
            var box = UIRect("Box", rect); UIPlace(box, 0, 4, 22, 22); var background = UIImage(box, new Color(.15f, .19f, .24f));
            var check = UIRect("Checked", box); UIStretch(check); check.offsetMin = new Vector2(5, 5); check.offsetMax = new Vector2(-5, -5); var mark = UIImage(check, new Color(.3f, .75f, 1));
            var text = UIText(rect, "Label", label, 14); UIStretch(text.rectTransform); text.rectTransform.offsetMin = new Vector2(32, 0); text.alignment = TextAnchor.MiddleLeft;
            var toggle = rect.gameObject.AddComponent<Toggle>(); toggle.targetGraphic = background; toggle.graphic = mark; toggle.onValueChanged.AddListener(changed); return toggle;
        }
        private static Dropdown UIDropdown(Transform parent, string label, string[] options, int selected, UnityEngine.Events.UnityAction<int> changed) {
            var column = UIColumn(parent, label + " Field"); UIText(column, "Label", label, 13);
            var rect = UIRect(label, column); var background = UIImage(rect, new Color(.15f, .19f, .24f)); rect.gameObject.AddComponent<LayoutElement>().minHeight = 36;
            var caption = UIText(rect, "Value", ""); UIStretch(caption.rectTransform); caption.rectTransform.offsetMin = new Vector2(10, 0); caption.alignment = TextAnchor.MiddleLeft;
            var template = UIRect("Template", rect); template.anchorMin = Vector2.zero; template.anchorMax = Vector2.right; template.pivot = Vector2.up; template.sizeDelta = new Vector2(0, 114);
            UIImage(template, new Color(.08f, .1f, .13f)); var scroll = template.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false;
            var viewport = UIRect("Viewport", template); UIStretch(viewport); viewport.gameObject.AddComponent<RectMask2D>();
            var content = UIRect("Content", viewport); content.anchorMin = Vector2.up; content.anchorMax = Vector2.one; content.pivot = Vector2.up; content.sizeDelta = new Vector2(0, 38);
            var item = UIRect("Item", content); UIStretch(item); var itemImage = UIImage(item, new Color(.19f, .3f, .4f));
            var itemText = UIText(item, "Option", ""); UIStretch(itemText.rectTransform); itemText.rectTransform.offsetMin = new Vector2(10, 0); itemText.alignment = TextAnchor.MiddleLeft;
            var toggle = item.gameObject.AddComponent<Toggle>(); toggle.targetGraphic = toggle.graphic = itemImage;
            scroll.viewport = viewport; scroll.content = content; template.gameObject.SetActive(false);
            var dropdown = rect.gameObject.AddComponent<Dropdown>(); dropdown.targetGraphic = background; dropdown.template = template; dropdown.captionText = caption; dropdown.itemText = itemText;
            dropdown.AddOptions(options.ToList()); dropdown.SetValueWithoutNotify(selected); dropdown.onValueChanged.AddListener(changed); return dropdown;
        }
    }
}
