using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationConnectionValidation {
        private const string Key = "Protobot.ConnectionSmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog(); EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>(); if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }
        [InitializeOnLoadMethod] private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try { await Check(); Debug.Log("Collaboration connection validation passed: visible real-byte geometry progress, pointer Cancel and Escape, unchanged accepted scenes/local files, same-membership retry, stale-progress isolation, canceled host/resume cleanup and successful recovered-room retry."); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        internal static async Task Check(bool proxyMode = false) {
            string root = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "connection-check"); RecoveryFiles.CreateDirectory(root);
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild); PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            var local = SceneBuild.ToBuildData(); var catalog = CollaborationScene.Catalog;
            string model = Path.Combine(root, "private-source.obj"); CollaborationValidation.WriteLargeModel(model);
            var custom = CustomModel.Create(CustomModelImporter.Read(model, 1), Vector3.right * 20, Quaternion.identity); File.Delete(model);
            string modelId = custom.GetComponent<SavedObject>().ConnectionId;
            var document = CollaborationScene.CaptureDocument(catalog); document.parts.First(p => p.catalogId != "CUSTOM").position[0] = 41;
            SceneBuild.GenerateBuild(local);
            CollaborationSession.EnableDevelopment(); var session = Object.FindFirstObjectByType<CollaborationSession>(); var manager = Object.FindFirstObjectByType<BuildsManager>();
            string originalSave = Path.Combine(root, "original.pbb"); manager.buildPath = originalSave;
            Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Initial local save failed."); byte[] originalBytes = File.ReadAllBytes(originalSave);
            var seed = new Bootstrap { room = "connection-room", password = "private-ui-password", ownerCredential = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), catalog = catalog, document = document };
            var info = CollaborationRecovery.Create(seed, 0);
            using var authority = new CollaborationHost(); await authority.StartAsync(seed, 0, false, info.directory); info.port = authority.BoundPort; CollaborationRecovery.Save(info);
            try {
                using var relay = new SlowRelay(authority.Url);
                using var outbound = proxyMode ? new OutboundProxyFixture(relay.Url, "proxy-user", "private-proxy-password") : null;
                using var editor = new CollaborationClient(authority.Url, seed.room, catalog, CollaborationTransport.LongPolling); await editor.JoinAsync("Remote owner", ownerCredential: seed.ownerCredential);
                Set(session, "endpoint", relay.Url); Set(session, "room", seed.room); Set(session, "transport", CollaborationArguments.Get("--collab-transport", "long-polling") == "short-polling" ? CollaborationTransport.ShortPolling : CollaborationTransport.LongPolling); Set(session, "password", seed.password);
                Invoke(session, "OpenPanel", 1);
                if (proxyMode) {
                    Get<Dropdown>(session, "uiNetworkRoute").value = 2; Get<Dropdown>(session, "uiProxyAuthentication").value = 1;
                    Get<Dropdown>(session, "uiTransport").value = (int)CollaborationTransport.ShortPolling; Invoke(session, "RefreshUI");
                    var proxyAddress = Get<InputField>(session, "uiProxyAddress");
                    await CollaborationUIValidation.Click(proxyAddress.gameObject); proxyAddress.text = outbound.Url;
                    Get<InputField>(session, "uiProxyUsername").text = "proxy-user"; Get<InputField>(session, "uiProxyPassword").text = "private-proxy-password";
                    Require(Get<InputField>(session, "uiProxyPassword").contentType == InputField.ContentType.Password && Get<InputField>(session, "uiProxyUsername").contentType == InputField.ContentType.Password, "Proxy input is not private.");
                    outbound.RejectAll = true; outbound.ReflectedError = WireJson.Text(new WireError { error = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("proxy-user:private-proxy-password")) });
                    await Click(session, "Join Room"); await Until(() => !Get<bool>(session, "busy"));
                    string report = WireJson.Text(Get<DiagnosticReport>(session, "lastDiagnostic"));
                    Require(!CollaborationSession.IsShared && PartsManager.FindLoadedObjects().Count == 1 && manager.buildPath == originalSave && File.ReadAllBytes(originalSave).SequenceEqual(originalBytes) && !report.Contains("private-proxy") && !report.Contains(outbound.Url) && !report.Contains(outbound.ReflectedError), "407 reflected private data or changed the scene/file.");
                    outbound.RejectAll = false; Get<InputField>(session, "uiPassword").text = seed.password;
                    await Click(session, "Test Connection"); await Until(() => !Get<bool>(session, "busy"));
                    Require(Get<Text>(session, "uiTestReport").text.Contains("passed") && outbound.Authenticated > 0, "Actual Unity connection-test control did not follow proxy policy.");
                    Get<InputField>(session, "uiPassword").text = seed.password;
                }
                relay.Hold();
                await Click(session, "Join Room"); await Until(() => relay.Partial.Task.IsCompleted);
                Invoke(session, "RefreshUI");
                Require(Get<Text>(session, "uiStatus").text.Contains("Downloading model") && Get<Text>(session, "uiStatus").text.Contains("MiB"), "UI did not report model count and actual transferred bytes.");
                var oldAttempt = Get<object>(session, "connectionAttempt"); string identity = Get<CollaborationClient>(oldAttempt, "Candidate").Participant.id;
                string status = Get<Text>(session, "uiStatus").text;
                Require(!status.Contains(seed.password) && !status.Contains(seed.ownerCredential) && !status.Contains("private-source"), "Progress exposed private metadata.");
                var added = WireJson.Clone(editor.Snapshot.parts.First(p => p.catalogId != "CUSTOM")); added.id = Guid.NewGuid().ToString("N"); added.position[0] = 71;
                Require((await editor.SubmitAsync(editor.CreateTransaction("AddParts", new[] { added }))).accepted, "Remote edit during download failed.");
                await Click(session, "Cancel Connection"); await Until(() => !Get<bool>(session, "busy"));
                Require(!CollaborationSession.IsShared && PartsManager.FindLoadedObjects().Count == 1 && manager.buildPath == originalSave && File.ReadAllBytes(originalSave).SequenceEqual(originalBytes), "Canceled join replaced the local build/file.");
                Require(authority.IsRunning && File.Exists(CollaborationRecovery.SessionFile(relay.Url, seed.room, catalog.hash)), "Canceled join stopped an independent helper or discarded the saved identity.");
                relay.Release(); await Click(session, "Join Room"); await Until(() => CollaborationSession.IsShared && !Get<bool>(session, "busy"));
                var client = Get<CollaborationClient>(session, "client");
                Require(client.Participant.id == identity && PartsManager.FindLoadedObjects().Count == 3 && manager.buildPath == "" && Body(modelId).GetComponentInChildren<MeshFilter>().sharedMesh.vertexCount == 90000,
                    "Retry lost membership, snapshot catch-up, mesh materialization or Save As behavior.");
                ((IProgress<ConnectionProgress>)oldAttempt).Report(new ConnectionProgress(ConnectionStage.DownloadingGeometry, 998, 999, 999)); Invoke(session, "RefreshUI");
                Require(!Get<Text>(session, "uiStatus").text.Contains("999"), "An ended attempt changed a newer connection's progress.");
                string joinedSave = Path.Combine(root, "joined.pbb"); manager.buildPath = joinedSave;
                Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Joined accepted save failed."); byte[] joinedBytes = File.ReadAllBytes(joinedSave);
                await client.PrepareReconnectAsync(); string accepted = CollaborationSession.CommittedHash;
                var small = new GeometryAsset { name = "Later geometry", vertices = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, triangles = new[] { 0, 1, 2 } }; small.hash = DocumentModel.GeometryHash(small);
                var later = new Part { id = Guid.NewGuid().ToString("N"), catalogId = "CUSTOM", assetHash = small.hash, position = new double[] { 80, 0, 0 } };
                Require((await editor.SubmitAsync(editor.CreateTransaction("AddParts", new[] { later }, assetWrites: new[] { small }))).accepted, "Later remote geometry failed.");
                relay.Hold(); Invoke(session, "RefreshUI"); await Click(session, "Reconnect"); await Until(() => relay.Partial.Task.IsCompleted);
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.Escape)); await Task.Delay(80);
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); await Until(() => !Get<bool>(session, "busy"));
                Require(!client.IsConnected && !Get<bool>(session, "panel") && !Get<GameObject>(session, "uiModal").activeSelf && CollaborationSession.CommittedHash == accepted && PartsManager.FindLoadedObjects().Count == 3,
                    "Escape did not cancel reconnect or retained a partial shared scene/modal.");
                Require(manager.buildPath == joinedSave && File.ReadAllBytes(joinedSave).SequenceEqual(joinedBytes), "Canceled reconnect changed a local file.");
                relay.Release(); Invoke(session, "OpenPanel", -1); await Click(session, "Reconnect"); await Until(() => client.IsConnected && !Get<bool>(session, "busy"));
                Require(client.Participant.id == identity && PartsManager.FindLoadedObjects().Count == 4 && !manager.CurrentBuildIsSaved && File.ReadAllBytes(originalSave).SequenceEqual(originalBytes), "Reconnect retry lost identity, latest content or independent saved bytes.");
                if (proxyMode) {
                    await Click(session, "Pause Network"); await Until(() => !Get<bool>(session, "busy") && !client.IsConnected);
                    string routeHash = CollaborationSession.CommittedHash; byte[] cache = File.ReadAllBytes(CollaborationRecovery.SessionFile(relay.Url, seed.room, catalog.hash));
                    outbound.RejectAll = true; Get<Dropdown>(session, "uiNetworkRoute").value = 1;
                    await Click(session, "Reconnect"); await Until(() => !Get<bool>(session, "busy"));
                    Require(client.IsConnected && client.NetworkRoute == "direct" && client.Participant.id == identity && CollaborationSession.CommittedHash == routeHash, "Direct-route UI reconnect lost membership/content or used the failed explicit proxy.");
                    await Click(session, "Pause Network"); await Until(() => !Get<bool>(session, "busy") && !client.IsConnected);
                    Get<Dropdown>(session, "uiNetworkRoute").value = 2;
                    await Click(session, "Reconnect"); await Until(() => !Get<bool>(session, "busy"));
                    Require(!client.IsConnected && client.Participant.id == identity && CollaborationSession.CommittedHash == routeHash && File.ReadAllBytes(CollaborationRecovery.SessionFile(relay.Url, seed.room, catalog.hash)).SequenceEqual(cache) && manager.buildPath == joinedSave && File.ReadAllBytes(joinedSave).SequenceEqual(joinedBytes), "Failed route change altered scene/file/membership.");
                    outbound.RejectAll = false; await Click(session, "Reconnect"); await Until(() => !Get<bool>(session, "busy"));
                    Require(client.IsConnected && client.NetworkRoute == "http-proxy" && client.Participant.id == identity, "Proxy-route retry lost identity.");
                    // Owned helper control ignores this failed participant proxy.
                    outbound.RejectAll = true;
                }
                Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown");
                int count = PartsManager.FindLoadedObjects().Count;
                var hosting = (Task)Invoke(session, "HostAsync"); var canceledHost = Get<CollaborationHost>(session, "host"); Invoke(session, "CancelConnection"); await hosting;
                Require(!CollaborationSession.IsShared && !Get<bool>(session, "busy") && (canceledHost == null || !canceledHost.IsRunning) && authority.IsRunning && PartsManager.FindLoadedObjects().Count == count,
                    "Canceled Host retained an owned process or changed the local scene.");
                await editor.LeaveAsync(); relay.Release();
                await authority.StopAsync();
                var recovering = (Task)Invoke(session, "RecoverHostAsync", info); Invoke(session, "CancelConnection"); await recovering;
                Require(!CollaborationSession.IsShared && !Get<bool>(session, "busy") && PartsManager.FindLoadedObjects().Count == count && File.Exists(Path.Combine(info.directory, "room.sqlite")), "Canceled Resume changed the scene or discarded durable room data.");
                await (Task)Invoke(session, "RecoverHostAsync", info);
                Require(CollaborationSession.IsShared && Get<CollaborationClient>(session, "client").Participant.role == "owner" && PartsManager.FindLoadedObjects().Count == 4 && manager.buildPath == "", "Resume retry failed to restore the accepted room with Save As.");
                if (proxyMode) Require(Get<CollaborationClient>(session, "client").NetworkRoute == "direct", "Owned-helper loopback control inherited an outbound proxy.");
                Invoke(session, "Detach"); await Get<Task>(session, "hostShutdown");
            }
            finally { await authority.StopAsync(); }
        }

        // Local-only relay gives deterministic real HTTP body boundaries without
        // adding failure controls or delays to the production helper.
        private sealed class SlowRelay : IDisposable {
            private readonly HttpListener listener = new HttpListener();
            private readonly HttpClient http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
            private readonly CancellationTokenSource stop = new CancellationTokenSource();
            private TaskCompletionSource<bool> release;
            public TaskCompletionSource<bool> Partial;
            public string Url { get; }
            public SlowRelay(string upstream) {
                var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
                Url = "http://127.0.0.1:" + number; listener.Prefixes.Add(Url + "/"); listener.Start(); _ = Run(upstream);
            }
            public void Hold() { release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); Partial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
            public void Release() { release?.TrySetResult(true); release = null; }
            private async Task Run(string upstream) {
                while (!stop.IsCancellationRequested) {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch (Exception) when (stop.IsCancellationRequested) { return; }
                    _ = Forward(context, upstream);
                }
            }
            private async Task Forward(HttpListenerContext context, string upstream) {
                try {
                    using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), upstream + context.Request.RawUrl);
                    string token = context.Request.Headers["Authorization"]; if (token != null) request.Headers.TryAddWithoutValidation("Authorization", token);
                    if (context.Request.HasEntityBody) request.Content = new StreamContent(context.Request.InputStream);
                    using var response = await http.SendAsync(request, stop.Token); var bytes = await response.Content.ReadAsByteArrayAsync();
                    context.Response.StatusCode = (int)response.StatusCode; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                    var held = release;
                    if (held != null && response.IsSuccessStatusCode && context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath.Contains("/assets/")) {
                        int prefix = Math.Min(65536, Math.Max(1, bytes.Length / 2));
                        await context.Response.OutputStream.WriteAsync(bytes, 0, prefix, stop.Token); await context.Response.OutputStream.FlushAsync(stop.Token); Partial.TrySetResult(true);
                        using (stop.Token.Register(() => held.TrySetCanceled())) await held.Task;
                        await context.Response.OutputStream.WriteAsync(bytes, prefix, bytes.Length - prefix, stop.Token);
                    }
                    else await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, stop.Token);
                }
                catch (Exception error) when (error is HttpRequestException || error is IOException || error is OperationCanceledException || error is ObjectDisposedException || error is HttpListenerException) { }
                finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
            }
            public void Dispose() { stop.Cancel(); Release(); listener.Close(); http.Dispose(); stop.Dispose(); }
        }
        private static GameObject Body(string id) => PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == id);
        private static async Task Click(CollaborationSession session, string name) { Invoke(session, "RefreshUI"); await CollaborationUIValidation.Click(Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>(true).Single(b => b.name == name).gameObject); }
        private static async Task Until(Func<bool> condition) { for (int i = 0; i < 1200; i++) { if (condition()) return; await Task.Delay(25); } throw new TimeoutException("Connection UI condition timed out."); }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
