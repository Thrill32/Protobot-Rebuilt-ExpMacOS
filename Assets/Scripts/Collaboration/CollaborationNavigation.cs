using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        [Serializable] private sealed class RememberedServers { public string[] origins = Array.Empty<string>(); }
        private const string ServersPreference = "Collaboration.Servers", LastServerPreference = "Collaboration.LastServer";
        private GameObject uiNavigation, uiNavigationJoin, uiNavigationBack, uiNavigationList, uiDirectoryLookupCancel;
        private RectTransform uiNavigationCard, uiNavigationDetail, uiNavigationBody, uiAdvancedViewport;
        private Transform uiDirectoryRows;
        private ScrollRect uiDirectoryScroll, uiNavigationScroll;
        private InputField uiServerHost, uiNavigationRoom, uiNavigationPassword, uiNavigationName;
        private Text uiDirectoryNotice, uiNavigationStatus, uiSavedAccess;
        private Dropdown uiSavedServers;
        private Toggle uiNavigationLan, uiNavigationViewer;
        private Button uiNavigationJoinButton, uiForgetAccess, uiDirectoryCancel;
        private CanvasGroup uiNavigationForms;
        private string serverDraft, directoryOrigin, directorySignature, savedAccessSignature;
        private GameObject navigationFocus;
        private RoomDirectoryEntry[] directoryRooms = Array.Empty<RoomDirectoryEntry>();
        private CancellationTokenSource directoryRequest;
        private bool advancedNavigation = true, navigationDetail, directoryLoaded;

        private void InitializeNavigation() {
            uiAdvancedViewport = (RectTransform)uiContent.parent;
            uiNavigation = UIRect("Collaboration Navigation", uiRoot.transform).gameObject; UIStretch((RectTransform)uiNavigation.transform);
            var outside = uiNavigation.AddComponent<Image>(); outside.color = new Color(0, 0, 0, .12f);
            var dismiss = uiNavigation.AddComponent<Button>(); dismiss.targetGraphic = outside; dismiss.navigation = new Navigation { mode = Navigation.Mode.None };
            dismiss.onClick.AddListener(ClosePanel);
            uiNavigationCard = UIRect("Server Popover", uiNavigation.transform); UIImage(uiNavigationCard, new Color(.055f, .065f, .08f, 1));
            var column = UIColumn(uiNavigationCard, "Server Browser"); column.GetComponent<VerticalLayoutGroup>().spacing = 6; UIStretch(column); column.offsetMin = new Vector2(14, 14); column.offsetMax = new Vector2(-14, -14);
            var heading = UIRow(column, "Navigation Heading"); UIText(heading, "Title", "Collaboration", 20);
            UIButton(heading, "Navigation Close", "Close", ClosePanel);
            uiNavigationForms = column.gameObject.AddComponent<CanvasGroup>();
            uiServerHost = UIField(column, "Server hostname", endpoint, 256, value => serverDraft = value);
            serverDraft = endpoint;
            uiSavedServers = UIDropdown(column, "Remembered servers", Array.Empty<string>(), 0, value => {
                var servers = ReadServers(); if (value < servers.Length) { uiServerHost.SetTextWithoutNotify(servers[value]); serverDraft = servers[value]; _ = BrowseServerAsync(); }
            });
            var serversRow = UIRow(column, "Server Actions");
            UIButton(serversRow, "Browse Server", "Connect / refresh", () => _ = BrowseServerAsync(), true);
            UIButton(serversRow, "Add Server", "Add server", () => {
                if (busy || IsShared) return; CancelDirectory(); directoryRooms = Array.Empty<RoomDirectoryEntry>(); directoryOrigin = null; directoryLoaded = false;
                navigationDetail = false; uiServerHost.text = ""; serverDraft = ""; directorySignature = null;
                uiDirectoryNotice.text = "Enter another server hostname, then Connect."; uiServerHost.Select(); uiServerHost.ActivateInputField();
            });
            uiDirectoryNotice = UIText(column, "Directory Notice", "Enter a server to browse its rooms, or paste an invite.", 12);
            var cancelDirectory = UIButton(column, "Cancel Directory", "Cancel lookup", CancelDirectory); uiDirectoryLookupCancel = cancelDirectory.gameObject;
            var list = UIRect("Room Directory Scroll", column); list.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1;
            list.GetComponent<LayoutElement>().minHeight = 90; UIImage(list, new Color(.08f, .1f, .13f)); list.gameObject.AddComponent<RectMask2D>();
            uiDirectoryRows = UIColumn(list, "Directory Rooms"); var rows = (RectTransform)uiDirectoryRows;
            rows.anchorMin = Vector2.up; rows.anchorMax = Vector2.one; rows.pivot = Vector2.up; rows.sizeDelta = Vector2.zero;
            rows.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            uiDirectoryScroll = list.gameObject.AddComponent<ScrollRect>(); uiDirectoryScroll.viewport = list; uiDirectoryScroll.content = rows;
            uiDirectoryScroll.horizontal = false; uiDirectoryScroll.movementType = ScrollRect.MovementType.Clamped; uiDirectoryScroll.scrollSensitivity = 30;
            uiNavigationList = list.gameObject;
            var joins = UIRow(column, "Direct Joining");
            UIButton(joins, "Join By ID", "Join by room ID", () => { SelectNavigationRoom(""); });
            UIButton(joins, "Navigation Paste Invite", "Paste invite", () => {
                if (busy || IsShared) return;
                PasteInvite(); serverDraft = endpoint; uiServerHost.SetTextWithoutNotify(endpoint); navigationDetail = true; SelectUITab(1); ClearPassword();
            });
            var create = UIRow(column, "Room Actions");
            UIButton(create, "Navigation Create Room", "Create room", () => { if (PrepareNavigationServer()) OpenNavigationTab(3); });
            UIButton(create, "Navigation Host Locally", "Host locally", () => OpenNavigationTab(0));
            var secondary = UIRow(column, "Secondary Actions");
            UIButton(secondary, "Navigation Owned Rooms", "Owner / recovery", () => OpenNavigationTab(5));
            UIButton(secondary, "Navigation Server Admin", "Admin", () => { if (PrepareNavigationServer()) OpenNavigationTab(4); });
            UIButton(column, "Advanced Collaboration", "Advanced / debugging...", () => { CancelDirectory(); advancedNavigation = true; OpenPanel(IsShared ? -1 : 1); });

            uiNavigationDetail = UIRect("Adjacent Room Panel", uiNavigation.transform); UIImage(uiNavigationDetail, new Color(.065f, .08f, .10f, 1));
            var back = UIButton(uiNavigationDetail, "Navigation Back", "Back to rooms", () => { ClearPassword(); ClearRoomKeys(); ClearReplacementConfirmation(); navigationDetail = false; ClearUIFocus(); });
            uiNavigationBack = back.gameObject; UIPlace((RectTransform)back.transform, 14, 10, 180, 34);
            var detailClose = UIButton(uiNavigationDetail, "Detail Close", "Close", ClosePanel);
            var closeRect = (RectTransform)detailClose.transform; closeRect.anchorMin = closeRect.anchorMax = closeRect.pivot = Vector2.one;
            closeRect.anchoredPosition = new Vector2(-14, -10); closeRect.sizeDelta = new Vector2(82, 34);
            var viewport = UIRect("Adjacent Scroll", uiNavigationDetail); UIStretch(viewport); viewport.offsetMin = new Vector2(14, 112); viewport.offsetMax = new Vector2(-14, -56);
            UIImage(viewport, new Color(0, 0, 0, .01f)); viewport.gameObject.AddComponent<RectMask2D>();
            uiNavigationScroll = viewport.gameObject.AddComponent<ScrollRect>(); uiNavigationScroll.viewport = viewport;
            uiNavigationScroll.horizontal = false; uiNavigationScroll.movementType = ScrollRect.MovementType.Clamped; uiNavigationScroll.scrollSensitivity = 30;
            uiNavigationBody = UIColumn(viewport, "Join Form"); uiNavigationBody.anchorMin = Vector2.up; uiNavigationBody.anchorMax = Vector2.one; uiNavigationBody.pivot = Vector2.up;
            uiNavigationBody.sizeDelta = Vector2.zero; uiNavigationBody.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            uiNavigationJoin = uiNavigationBody.gameObject;
            uiNavigationRoom = UIField(uiNavigationBody, "Room ID", room, 80, value => { if (room != value) { ClearPassword(); savedAccessSignature = null; } room = value; });
            uiNavigationName = UIField(uiNavigationBody, "Your name", displayName, 48, value => displayName = value);
            uiSavedAccess = UIText(uiNavigationBody, "Saved Access", "", 13);
            uiNavigationPassword = UIField(uiNavigationBody, "Room password", "", 128, value => password = value, true);
            uiNavigationViewer = UIToggle(uiNavigationBody, "Navigation Viewer", "Join as viewer (read-only)", value => { joinAsViewer = value; uiViewer.isOn = value; });
            uiNavigationLan = UIToggle(uiNavigationBody, "Navigation LAN HTTP", "Allow unencrypted LAN HTTP", value => { lan = value; uiLan.isOn = value; });
            uiNavigationJoinButton = UIButton(uiNavigationBody, "Navigation Join", "Join room", () => { if (PrepareNavigationServer()) RequestJoin(); }, true);
            uiForgetAccess = UIButton(uiNavigationBody, "Forget Saved Access", "Forget saved access", ForgetNavigationAccess);
            UIText(uiNavigationBody, "Join Safety", "Joining uses the normal Save / Discard prompt. Saved access keeps your role and history. Forget removes this profile’s participant credential; private ownership stays available under Owner / recovery.", 12);
            var footer = UIRect("Navigation Footer", uiNavigationDetail); footer.anchorMin = Vector2.zero; footer.anchorMax = Vector2.right;
            footer.pivot = Vector2.zero; footer.anchoredPosition = new Vector2(14, 10); footer.sizeDelta = new Vector2(-28, 94);
            uiNavigationStatus = UIText(footer, "Navigation Status", "", 12); UIStretch(uiNavigationStatus.rectTransform); uiNavigationStatus.rectTransform.offsetMax = new Vector2(-88, 0);
            uiDirectoryCancel = UIButton(footer, "Navigation Cancel", "Cancel", () => { CancelDirectory(); CancelConnection(); CancelRoomOperation(); });
            var cancel = (RectTransform)uiDirectoryCancel.transform; cancel.anchorMin = cancel.anchorMax = cancel.pivot = new Vector2(1, .5f); cancel.sizeDelta = new Vector2(82, 34);
            uiNavigation.SetActive(false);
            RefreshRememberedServers();
        }
        private string[] ReadServers() {
            try { return WireJson.Decode<RememberedServers>(System.Text.Encoding.UTF8.GetBytes(LocalPreferences.GetString(ServersPreference, "{}"))).origins ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }
        private void RememberServer(string origin) {
            origin = CollaborationInvite.ValidateOrigin(origin);
            var servers = new RememberedServers { origins = new[] { origin }.Concat(ReadServers()).Distinct(StringComparer.Ordinal).Take(8).ToArray() };
            LocalPreferences.SetString(ServersPreference, WireJson.Text(servers)); LocalPreferences.SetString(LastServerPreference, origin); LocalPreferences.Save();
            RefreshRememberedServers();
        }
        private void RefreshRememberedServers() {
            if (uiSavedServers == null) return;
            uiSavedServers.ClearOptions(); var servers = ReadServers(); uiSavedServers.AddOptions((servers.Length == 0 ? new[] { "No remembered servers" } : servers).ToList());
            uiSavedServers.SetValueWithoutNotify(Mathf.Max(0, Array.IndexOf(servers, endpoint)));
        }
        private bool PrepareNavigationServer() {
            try {
                var origin = CollaborationInvite.ValidateOrigin(serverDraft);
                if (endpoint != origin) { ClearPassword(); directoryRooms = Array.Empty<RoomDirectoryEntry>(); directoryLoaded = false; directorySignature = null; }
                endpoint = origin; uiServerHost.SetTextWithoutNotify(origin); serverDraft = origin; RememberServer(origin); return true;
            } catch (Exception error) { Report(error); uiDirectoryNotice.text = error.Message; return false; }
        }
        private void OpenNavigation() {
            advancedNavigation = false; navigationDetail = IsShared; serverDraft = endpoint; uiServerHost.SetTextWithoutNotify(endpoint);
            SelectUITab(1); OpenPanel();
            if (!IsShared && !string.IsNullOrEmpty(endpoint) && !directoryLoaded) _ = BrowseServerAsync();
            if (!IsShared && string.IsNullOrEmpty(endpoint)) { uiServerHost.Select(); uiServerHost.ActivateInputField(); }
        }
        private void CancelDirectory() { directoryRequest?.Cancel(); }
        private void OpenNavigationTab(int tab) { ClearPassword(); navigationDetail = true; SelectUITab(tab); }
        private async Task BrowseServerAsync() {
            if (busy || IsShared || !PrepareNavigationServer()) return;
            CancelDirectory();
            var active = new CancellationTokenSource(); directoryRequest = active; var origin = endpoint;
            directoryRooms = Array.Empty<RoomDirectoryEntry>(); directoryOrigin = origin; directoryLoaded = false; directorySignature = null;
            uiDirectoryNotice.text = "Checking room directory…";
            try {
                using var remote = new RemoteRoomDirectory(origin, lan, await SelectedNetworkAsync(active.Token));
                var result = await remote.ReadAsync(active.Token); active.Token.ThrowIfCancellationRequested();
                if (closing || directoryRequest != active || endpoint != origin) return;
                directoryRooms = result.rooms; directoryLoaded = true;
                uiDirectoryNotice.text = result.rooms.Length == 0 ? "No available rooms listed. Join by ID or create a room." : "Select a room. Passwords and room permissions still apply.";
            } catch (OperationCanceledException) { if (!closing && directoryRequest == active) uiDirectoryNotice.text = "Directory lookup canceled. Join by ID or refresh when ready."; }
            catch (Exception error) {
                if (closing || directoryRequest != active || endpoint != origin) return;
                var code = error is CollaborationException known ? known.Code : WireFailure.Code(error);
                uiDirectoryNotice.text = code == "room-directory-disabled" ? "This server keeps its rooms private. Use an invite or room ID." :
                    code == "room-directory-unavailable" ? "This server has no public directory. Use an invite or room ID." : FriendlyStatus(code);
            } finally { if (directoryRequest == active) { directoryRequest = null; directorySignature = null; } active.Dispose(); nextUIRefresh = 0; }
        }
        private void SelectNavigationRoom(string id) {
            if (busy || IsShared || !PrepareNavigationServer()) return;
            ClearPassword(); room = id; savedAccessSignature = null; navigationDetail = true; SelectUITab(1);
            uiNavigationRoom.SetTextWithoutNotify(id); uiNavigationRoom.Select();
        }
        private void ForgetNavigationAccess() {
            try {
                endpoint = CollaborationInvite.ValidateOrigin(endpoint);
                DocumentModel.Require(DocumentModel.ValidId(room), "invalid-room");
                CollaborationClient.ForgetRecovery(CollaborationRecovery.SessionFile(endpoint, room, CollaborationScene.Catalog.hash));
                ClearPassword(); savedAccessSignature = directorySignature = null;
                status = "Saved participant access forgotten for this server and room. Private owner recovery is retained.";
            } catch (Exception error) { Report(error); }
        }
        private void RefreshNavigation() {
            if (uiNavigation == null) return;
            uiNavigation.SetActive((panel || busy) && !advancedNavigation);
            bool ordinary = !advancedNavigation;
            var target = ordinary && (navigationDetail || IsShared) && (uiTab != 1 || IsShared) ? uiNavigationScroll.viewport : uiAdvancedViewport;
            if (uiContent.parent != target) {
                uiContent.SetParent(target, false); uiContent.anchorMin = Vector2.up; uiContent.anchorMax = Vector2.one; uiContent.pivot = Vector2.up; uiContent.anchoredPosition = Vector2.zero; uiContent.sizeDelta = Vector2.zero;
                uiScroll.content = target == uiAdvancedViewport ? uiContent : null; uiNavigationScroll.verticalNormalizedPosition = 1;
            }
            uiUtilityGroup.gameObject.SetActive(advancedNavigation);
            if (ordinary) { uiNetwork.SetActive(false); uiPauseNetwork.gameObject.SetActive(false); }
            if (!uiNavigation.activeSelf) return;
            bool detail = navigationDetail || IsShared;
            uiNavigationDetail.gameObject.SetActive(detail);
            var root = (RectTransform)uiRoot.transform;
            float available = root.rect.width - 24, height = Mathf.Min(690, root.rect.height - 52);
            bool adjacent = Screen.width >= 1100 && available >= 880;
            float leftWidth = Mathf.Min(354, available), rightWidth = adjacent ? Mathf.Min(530, available - leftWidth - 8) : Mathf.Min(580, available);
            uiBadge.GetWorldCorners(uiEntryCorners);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(root, uiEntryCorners[0], null, out var badgeBottom);
            float total = detail && adjacent ? leftWidth + 8 + rightWidth : detail ? rightWidth : leftWidth;
            float x = Mathf.Clamp(badgeBottom.x, root.rect.xMin + 12, root.rect.xMax - total - 12);
            float y = root.rect.yMax - 36;
            void Place(RectTransform rect, float left, float width) {
                rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f); rect.pivot = Vector2.up; rect.anchoredPosition = new Vector2(left, y); rect.sizeDelta = new Vector2(width, height);
            }
            Place(uiNavigationCard, x, leftWidth); Place(uiNavigationDetail, adjacent ? x + leftWidth + 8 : x, rightWidth);
            uiNavigationCard.gameObject.SetActive(!detail || adjacent);
            uiNavigationBack.SetActive(!IsShared);
            uiNavigationForms.interactable = !busy; uiNavigationForms.blocksRaycasts = !busy;
            foreach (var control in uiNavigationCard.GetComponentsInChildren<Selectable>()) control.interactable = !busy && (!IsShared || control.name == "Advanced Collaboration" || control.name == "Navigation Close");
            uiNavigationJoin.SetActive(!IsShared && uiTab == 1);
            uiNavigationScroll.content = uiNavigationJoin.activeSelf ? uiNavigationBody : uiContent;
            uiNavigationJoinButton.interactable = !busy && AdmissionCooldownSeconds == 0;
            foreach (var control in uiNavigationJoin.GetComponentsInChildren<Selectable>()) control.interactable = !busy;
            uiNavigationJoinButton.interactable = !busy && AdmissionCooldownSeconds == 0;
            uiNavigationRoom.SetTextWithoutNotify(room); uiNavigationName.SetTextWithoutNotify(displayName);
            uiNavigationPassword.SetTextWithoutNotify(password); uiNavigationLan.SetIsOnWithoutNotify(lan); uiNavigationViewer.SetIsOnWithoutNotify(joinAsViewer);
            uiNavigationStatus.text = ConnectionStatus(); uiDirectoryCancel.gameObject.SetActive(connectionAttempt != null || roomOperation != null || directoryRequest != null);
            uiDirectoryCancel.interactable = true;
            uiDirectoryLookupCancel.SetActive(directoryRequest != null);
            string scope = endpoint + "\n" + room;
            if (scope != savedAccessSignature && !busy) {
                savedAccessSignature = scope;
                try {
                    bool saved = DocumentModel.ValidId(room) && !string.IsNullOrEmpty(endpoint) && CollaborationClient.HasSavedAccess(CollaborationRecovery.SessionFile(endpoint, room, CollaborationScene.Catalog.hash), endpoint, room, CollaborationScene.Catalog.hash);
                    uiSavedAccess.text = saved ? "Saved access available. Rejoin with your existing role and history." : "Enter a password if this room requires one.";
                    uiNavigationPassword.transform.parent.gameObject.SetActive(!saved); uiForgetAccess.gameObject.SetActive(saved);
                } catch { uiSavedAccess.text = "Saved access could not be read. Its private file is retained."; uiForgetAccess.gameObject.SetActive(true); uiNavigationPassword.transform.parent.gameObject.SetActive(false); }
            }
            string signature = endpoint + "|" + directoryOrigin + "|" + string.Join(",", directoryRooms.Select(r => r.room));
            if (directorySignature != signature && !busy) {
                directorySignature = signature;
                foreach (Transform child in uiDirectoryRows) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
                var saved = string.IsNullOrEmpty(endpoint) ? Array.Empty<string>() : CollaborationClient.SavedRooms(System.IO.Path.Combine(CollaborationRecovery.Root, "sessions"), endpoint, CollaborationScene.Catalog.hash);
                var listed = directoryOrigin == endpoint ? directoryRooms : Array.Empty<RoomDirectoryEntry>();
                foreach (var id in listed.Select(r => r.room).Concat(saved).Distinct().Take(256)) {
                    var entry = listed.FirstOrDefault(r => r.room == id);
                    UIButton(uiDirectoryRows, "Directory Room " + id, id + (saved.Contains(id) ? "  · saved access" : entry?.passwordRequired == true ? "  · password" : ""), () => SelectNavigationRoom(id));
                }
                uiDirectoryScroll.verticalNormalizedPosition = 1;
            }
        }
        private void NavigateCollaborationKeyboard() {
            KeepNavigationFocusVisible();
            if (!panel || Keyboard.current == null || !Keyboard.current.tabKey.wasPressedThisFrame || uiRoot.GetComponentsInChildren<Dropdown>().Any(d => d.transform.Find("Dropdown List") != null)) return;
            var controls = (advancedNavigation ? uiModal : uiNavigation).GetComponentsInChildren<Selectable>().Where(s => s.IsInteractable() && s.gameObject != uiNavigation && !(s is Scrollbar)).ToArray();
            if (controls.Length == 0) return;
            int index = Array.FindIndex(controls, s => s.gameObject == EventSystem.current.currentSelectedGameObject);
            int step = Keyboard.current.shiftKey.isPressed ? -1 : 1;
            var next = controls[(index + step + controls.Length) % controls.Length]; next.Select();
            if (next is InputField field) field.ActivateInputField();
            KeepNavigationFocusVisible();
        }
        private void KeepNavigationFocusVisible() {
            // Pointer presses must not move their target between down and up.
            var keyboard = Keyboard.current;
            if (!panel || keyboard == null || !(keyboard.tabKey.isPressed || keyboard.upArrowKey.isPressed || keyboard.downArrowKey.isPressed ||
                keyboard.leftArrowKey.isPressed || keyboard.rightArrowKey.isPressed)) return;
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (!selected || selected == navigationFocus || !selected.transform.IsChildOf(uiRoot.transform)) return;
            navigationFocus = selected;
            var scroll = selected.GetComponentInParent<ScrollRect>();
            if (!scroll || !scroll.content || scroll.content.rect.height <= scroll.viewport.rect.height) return;
            Canvas.ForceUpdateCanvases();
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, selected.transform);
            float adjustment = bounds.max.y > scroll.viewport.rect.yMax ? scroll.viewport.rect.yMax - bounds.max.y :
                bounds.min.y < scroll.viewport.rect.yMin ? scroll.viewport.rect.yMin - bounds.min.y : 0;
            scroll.content.anchoredPosition += Vector2.up * adjustment;
        }
    }
}
