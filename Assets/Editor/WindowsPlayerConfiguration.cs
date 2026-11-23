using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ProtobotEditorTools {
    /// <summary>Serializes a Windows-only borderless default into players without changing macOS defaults.</summary>
    public sealed class WindowsPlayerConfiguration : IPreprocessBuildWithReport, IPostprocessBuildWithReport {
        private const string RestorePending = "Protobot.WindowsPlayerConfiguration.RestorePending";
        private const string PreviousMode = "Protobot.WindowsPlayerConfiguration.PreviousMode";
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report) => Begin(report.summary.platform);
        public void OnPostprocessBuild(BuildReport report) => Restore();

        internal static bool Begin(BuildTarget target) {
            if (target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneWindows64) return false;
            if (!SessionState.GetBool(RestorePending, false)) {
                SessionState.SetInt(PreviousMode, (int)PlayerSettings.fullScreenMode);
                SessionState.SetBool(RestorePending, true);
            }
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            EditorApplication.update -= RestoreWhenIdle;
            EditorApplication.update += RestoreWhenIdle;
            return true;
        }

        internal static void Restore() {
            if (!SessionState.GetBool(RestorePending, false)) return;
            PlayerSettings.fullScreenMode = (FullScreenMode)SessionState.GetInt(PreviousMode, (int)FullScreenMode.Windowed);
            SessionState.EraseInt(PreviousMode);
            SessionState.EraseBool(RestorePending);
        }

        [InitializeOnLoadMethod]
        private static void RecoverInterruptedBuild() {
            EditorApplication.update -= RestoreWhenIdle;
            EditorApplication.update += RestoreWhenIdle;
        }

        private static void RestoreWhenIdle() {
            if (BuildPipeline.isBuildingPlayer) return;
            EditorApplication.update -= RestoreWhenIdle;
            Restore();
        }
    }
}
