#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        [Serializable] private sealed class UIProbeResult { public bool passed; public string role, transport; public int participants; }
        // Explicit development-player diagnostics. Uses the same launcher and
        // prefilled Join form as the local-window button, then kills only its child.
        internal async Task RunUIPlayerProbeAsync(string output, bool child) {
            Button FindButton(string name) => uiRoot.GetComponentsInChildren<Button>().Single(b => b.name == name);
            async Task Wait(Func<bool> condition) {
                var end = DateTime.UtcNow.AddSeconds(40);
                while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException("Built UI probe timed out."); await Task.Delay(30); }
            }
            async Task Capture(string suffix) {
                await Task.Delay(250); ScreenCapture.CaptureScreenshot(output + suffix + ".png");
                await Wait(() => File.Exists(output + suffix + ".png"));
            }
            async Task Click(GameObject target) {
                RefreshUI(); Canvas.ForceUpdateCanvases(); PositionCollaborationButton();
                await Task.Delay(80); Canvas.ForceUpdateCanvases();
                var scroll = target.GetComponentInParent<ScrollRect>();
                if (scroll?.content) {
                    var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, target.transform);
                    scroll.content.anchoredPosition += Vector2.up * (scroll.viewport.rect.yMax - bounds.max.y);
                    await Task.Delay(80); Canvas.ForceUpdateCanvases();
                }
                var rect = (RectTransform)target.transform; var center = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
                var hits = new System.Collections.Generic.List<RaycastResult>(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = center }, hits);
                if (hits.Count == 0 || hits[0].gameObject != target && !hits[0].gameObject.transform.IsChildOf(target.transform))
                    throw new InvalidOperationException("Built UI pointer target " + target.name + " at " + center + " is covered by " + (hits.Count == 0 ? "nothing" : hits[0].gameObject.name));
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center }); await Task.Delay(80);
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center }.WithButton(MouseButton.Left)); await Task.Delay(80);
                InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = center }); await Task.Delay(100);
            }
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            // Start runs before the first top-bar/layout update in a standalone player.
            await Task.Delay(300);
            if (child) {
                if (LocalPreferences.ProfileDirectory == null || !panel || uiTab != 1) throw new InvalidOperationException("Local test window did not open an isolated prefilled Join page.");
                ClosePanel(); await Click(FindButton("Collaboration").gameObject);
                await Click(FindButton("Join By ID").gameObject); uiNavigationRoom.text = room = CollaborationArguments.Get("--collab-room", "team");
                uiNavigationName.text = "UI test viewer";
                await Click(uiNavigationJoin.GetComponentsInChildren<Toggle>().Single(t => t.name == "Navigation Viewer").targetGraphic.gameObject);
                await Capture(".join"); await Click(FindButton("Navigation Join").gameObject);
                await Wait(() => client?.IsConnected == true && !busy);
                if (client.Participant.role != "viewer" || CanEdit) throw new InvalidOperationException("Viewer UI did not enforce read-only membership.");
                RefreshUI(); await Capture(".connected");
                File.WriteAllBytes(output, WireJson.Encode(new UIProbeResult { passed = true, role = client.Participant.role, transport = client.ActiveTransport }));
                return;
            }
            if (panel) ClosePanel(); await Click(FindButton("Collaboration").gameObject); await Click(FindButton("Navigation Host Locally").gameObject);
            uiPort.text = "0"; await Capture(".host");
            await Click(FindButton("Start Hosting").gameObject); await Wait(() => client?.IsConnected == true && !busy);
            string childProfile = Path.Combine(LocalPreferences.ProfileDirectory, "UI child profile"); string childOutput = output + ".child.json";
            var start = CollaborationTestWindow.StartInfo(childProfile, host.Url, room);
            int observedParticipants = 0;
            start.Arguments += " --collab-probe ui-child --collab-test-results " + CollaborationHost.Quote(childOutput) + " -screen-width 1000 -screen-height 760 -screen-fullscreen 0";
            using (var owned = System.Diagnostics.Process.Start(start)) {
                try {
                    await Wait(() => File.Exists(childOutput));
                    var result = WireJson.Decode<UIProbeResult>(File.ReadAllBytes(childOutput));
                    if (!result.passed || result.role != "viewer") throw new InvalidOperationException("Local test window did not join through its UI.");
                    await Wait(() => participants.Length == 2); observedParticipants = participants.Length; RefreshUI(); await Capture(".connected");
                }
                finally { if (!owned.HasExited) { owned.Kill(); await Task.Run(() => owned.WaitForExit()); } }
            }
            var final = new UIProbeResult { passed = true, role = client.Participant.role, transport = client.ActiveTransport, participants = observedParticipants };
            Detach(); await hostShutdown;
            File.WriteAllBytes(output, WireJson.Encode(final));
        }
    }
}
#endif
