#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        [Serializable] private sealed class UIProbeResult { public bool passed; public string role, transport; public int participants; }
        // Explicit development-player diagnostics. Uses the same launcher and
        // prefilled Join form as the local-window button, then kills only its child.
        internal async Task RunUIPlayerProbeAsync(string output, bool child) {
            Button FindButton(string name) => uiRoot.GetComponentsInChildren<Button>(true).Single(b => b.name == name);
            async Task Wait(Func<bool> condition) {
                var end = DateTime.UtcNow.AddSeconds(40);
                while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException("Built UI probe timed out."); await Task.Delay(30); }
            }
            async Task Capture(string suffix) {
                await Task.Delay(250); ScreenCapture.CaptureScreenshot(output + suffix + ".png");
                await Wait(() => File.Exists(output + suffix + ".png"));
            }
            if (child) {
                if (LocalPreferences.ProfileDirectory == null || !panel || uiTab != 1) throw new InvalidOperationException("Local test window did not open an isolated prefilled Join page.");
                uiName.text = "UI test viewer"; uiViewer.isOn = true;
                await Capture(".join"); FindButton("Join Room").onClick.Invoke();
                await Wait(() => client?.IsConnected == true && !busy);
                if (client.Participant.role != "viewer" || CanEdit) throw new InvalidOperationException("Viewer UI did not enforce read-only membership.");
                RefreshUI(); await Capture(".connected");
                File.WriteAllBytes(output, WireJson.Encode(new UIProbeResult { passed = true, role = client.Participant.role, transport = client.ActiveTransport }));
                return;
            }
            OpenPanel(0); uiPort.text = "0"; await Capture(".host");
            FindButton("Start Hosting").onClick.Invoke(); await Wait(() => client?.IsConnected == true && !busy);
            string childProfile = Path.Combine(LocalPreferences.ProfileDirectory, "UI child profile"); string childOutput = output + ".child.json";
            var start = CollaborationTestWindow.StartInfo(childProfile, host.Url, room);
            start.Arguments += " --collab-probe ui-child --collab-test-results " + CollaborationHost.Quote(childOutput) + " -screen-width 1000 -screen-height 760 -screen-fullscreen 0";
            using (var owned = System.Diagnostics.Process.Start(start)) {
                try {
                    await Wait(() => File.Exists(childOutput));
                    var result = WireJson.Decode<UIProbeResult>(File.ReadAllBytes(childOutput));
                    if (!result.passed || result.role != "viewer") throw new InvalidOperationException("Local test window did not join through its UI.");
                    await Wait(() => participants.Length == 2); RefreshUI(); await Capture(".connected");
                }
                finally { if (!owned.HasExited) { owned.Kill(); await Task.Run(() => owned.WaitForExit()); } }
            }
            var final = new UIProbeResult { passed = true, role = client.Participant.role, transport = client.ActiveTransport, participants = participants.Length };
            Detach(); await hostShutdown;
            File.WriteAllBytes(output, WireJson.Encode(final));
        }
    }
}
#endif
