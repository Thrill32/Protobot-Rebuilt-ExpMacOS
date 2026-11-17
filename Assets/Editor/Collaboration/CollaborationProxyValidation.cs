using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationProxyValidation {
        private const string Key = "Protobot.ProxySmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog(); EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod] private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>(); if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try { await Check(); await CollaborationConnectionValidation.Check(true); Debug.Log("Collaboration proxy validation passed: anonymous/Basic runtime transports, actual proxy/test/Join/Cancel/Escape/pause/route/reconnect controls, geometry materialization, membership and scene/file preservation, private error metadata and direct owned-helper recovery."); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var seed = new Bootstrap { room = "proxy-team", catalog = CollaborationScene.Catalog, ownerCredential = "proxy-validation-owner", document = CollaborationScene.CaptureDocument(CollaborationScene.Catalog) };
            using var host = new CollaborationHost(); await host.StartAsync(seed, 0, false);
            foreach (bool basic in new[] { false, true }) {
                using var proxy = new OutboundProxyFixture(host.Url, basic ? "proxy-user" : null, basic ? "private-proxy-password" : null);
                var policy = NetworkPolicy.Create(new NetworkConfiguration { route = "http-proxy", proxy = proxy.Url,
                    authentication = basic ? "basic" : "none", username = basic ? "proxy-user" : null, password = basic ? "private-proxy-password" : null });
                foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
                    using var client = new CollaborationClient(host.Url, seed.room, seed.catalog, transport, networkPolicy: policy);
                    int received = 0; client.Presence += _ => Interlocked.Increment(ref received);
                    await client.JoinAsync("Unity proxy proof");
                    for (int i = 0; i < 400 && received == 0 && client.IsConnected; i++) await Task.Delay(25);
                    if (!client.IsConnected || received == 0) throw new InvalidOperationException("Unity proxy failed " + transport + " basic=" + basic);
                    await client.LeaveAsync();
                }
                if (proxy.Forwarded == 0 || basic && proxy.Authenticated == 0) throw new InvalidOperationException("Unity bypassed explicit proxy.");
            }
            await host.StopAsync();
            using (var tls = new UntrustedTlsFixture())
            using (var proxy = new OutboundProxyFixture(tls.Url, "proxy-user", "private-proxy-password")) {
                var policy = NetworkPolicy.Create(new NetworkConfiguration { route = "http-proxy", proxy = proxy.Url, authentication = "basic", username = "proxy-user", password = "private-proxy-password" });
                var report = await ConnectionDoctor.RunAsync(tls.Url, networkPolicy: policy);
                if (report.passed || report.checks.Single(c => c.name == "capabilities").code != "tls-trust-or-name-failed" || proxy.Connects == 0 || proxy.Authenticated == 0 || proxy.OriginCredentialInConnect) throw new InvalidOperationException("Unity CONNECT did not preserve TLS trust/authentication boundaries.");
                proxy.RejectAll = true; proxy.ReflectedError = "private-proxy-password";
                report = await ConnectionDoctor.RunAsync(tls.Url, networkPolicy: policy);
                if (report.passed || report.checks.Single(c => c.name == "capabilities").code != "proxy-authentication-required") throw new InvalidOperationException("Unity CONNECT 407 did not produce its fixed authentication code.");
            }
        }
    }
}
