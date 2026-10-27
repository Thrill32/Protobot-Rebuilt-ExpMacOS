using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Protobot {
    /// <summary>Local player-log milestones for diagnosing launch hangs on other Macs.</summary>
    public sealed class StartupDiagnostics : MonoBehaviour {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void BeforeSplash() {
            Debug.Log($"[Startup] Protobot {Application.version}; Unity {Application.unityVersion}; {SystemInfo.operatingSystem}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BeforeScene() {
            Debug.Log($"[Startup] Graphics initialized: {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}; log: {Application.consoleLogPath}");
            var probe = new GameObject("Startup diagnostics", typeof(StartupDiagnostics)) { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(probe);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AfterScene() => Debug.Log($"[Startup] Scene loaded: {SceneManager.GetActiveScene().name}");

        private IEnumerator Start() {
            yield return null;
            Debug.Log("[Startup] First frame completed; application update loop is running.");
            Destroy(gameObject);
        }
    }
}
