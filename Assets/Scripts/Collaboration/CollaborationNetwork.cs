using System;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        private int networkRoute, proxyAuthentication;
        private bool invalidNetworkArgument;
        private string proxyAddress = "", proxyUsername = "", proxyPassword = "";
        private GameObject uiNetwork, uiProxyFields;
        private CanvasGroup uiNetworkGroup;
        private Dropdown uiNetworkRoute, uiProxyAuthentication;
        private InputField uiProxyAddress, uiProxyUsername, uiProxyPassword;
        private Button uiPauseNetwork;
        private NetworkPolicy SelectedNetwork() {
            DocumentModel.Require(!invalidNetworkArgument, "proxy-route-unsupported");
            return NetworkPolicy.Create(new NetworkConfiguration {
            route = networkRoute == 0 ? "system" : networkRoute == 1 ? "direct" : "http-proxy",
            proxy = networkRoute == 2 ? proxyAddress : null,
            authentication = networkRoute == 2 && proxyAuthentication == 1 ? "basic" : "none",
            username = networkRoute == 2 && proxyAuthentication == 1 ? proxyUsername : null,
            password = networkRoute == 2 && proxyAuthentication == 1 ? proxyPassword : null
            });
        }
        private Task<NetworkPolicy> SelectedNetworkAsync(CancellationToken cancellation) {
            var selected = SelectedNetwork(); return Task.Run(() => { cancellation.ThrowIfCancellationRequested(); return selected; }, cancellation);
        }
        private void InitializeNetworkUI(Transform parent) {
            uiNetwork = UIColumn(parent, "Network Settings").gameObject; uiNetworkGroup = uiNetwork.AddComponent<CanvasGroup>();
            UIText(uiNetwork.transform, "Network Help", "Network route applies to Join, connection tests and reconnect. Local hosting always connects directly. Changes keep this room's saved membership and pending outcomes.", 12);
            uiNetworkRoute = UIDropdown(uiNetwork.transform, "Network route", new[] { "System proxy settings (default)", "Direct connection", "Explicit HTTP proxy" }, networkRoute, value => { networkRoute = value; invalidNetworkArgument = false; });
            uiProxyFields = UIColumn(uiNetwork.transform, "Private Proxy Input").gameObject;
            uiProxyAddress = UIField(uiProxyFields.transform, "Proxy address (http://host:port)", proxyAddress, 256, value => proxyAddress = value);
            uiProxyAuthentication = UIDropdown(uiProxyFields.transform, "Proxy authentication", new[] { "No proxy authentication", "Basic (private input)" }, 0, value => proxyAuthentication = value);
            uiProxyUsername = UIField(uiProxyFields.transform, "Proxy username", "", 256, value => proxyUsername = value, true);
            uiProxyPassword = UIField(uiProxyFields.transform, "Proxy password", "", 256, value => proxyPassword = value, true);
            UIText(uiProxyFields.transform, "Proxy Privacy", "Private input stays in this window's memory. It is separate from the room password and is never saved or copied to another test window. Basic requires a trusted network to the HTTP proxy.", 12);
            UIButton(uiProxyFields.transform, "Clear Proxy Input", "Clear proxy input", () => {
                proxyAddress = proxyUsername = proxyPassword = "";
                uiProxyAddress.SetTextWithoutNotify(""); uiProxyUsername.SetTextWithoutNotify(""); uiProxyPassword.SetTextWithoutNotify("");
            });
            uiPauseNetwork = UIButton(parent, "Pause Network", "Pause connection to change network route", () => _ = PauseForNetworkAsync());
        }
        private async Task PauseForNetworkAsync() {
            if (busy || client == null || host != null) return;
            busy = true;
            try { CancelTools(); CancelPose(); ClearGhosts(); await client.PrepareReconnectAsync(); status = "Connection paused. Choose a route and Reconnect. Accepted content, saved membership and pending outcomes are kept."; }
            catch (Exception error) { Report(error); }
            finally { busy = false; nextUIRefresh = 0; }
        }
        private void RefreshNetworkUI() {
            bool editable = host == null && (client == null || !client.IsConnected);
            uiNetwork.SetActive(editable); uiNetworkGroup.interactable = !busy; uiNetworkGroup.blocksRaycasts = !busy;
            uiPauseNetwork.gameObject.SetActive(host == null && client != null && client.IsConnected); uiPauseNetwork.interactable = !busy;
            uiProxyFields.SetActive(networkRoute == 2);
            uiProxyUsername.gameObject.SetActive(proxyAuthentication == 1); uiProxyPassword.gameObject.SetActive(proxyAuthentication == 1);
        }
    }
}
