using System;
using UnityEditor;
using UnityEngine;

namespace ProtobotEditorTools {
    public static class WindowsPlayerConfigurationValidation {
        [MenuItem("Tools/Protobot/Validate Windows Player Configuration")]
        public static void Validate() {
            var mode = PlayerSettings.fullScreenMode;
            int width = PlayerSettings.defaultScreenWidth, height = PlayerSettings.defaultScreenHeight;
            bool resizable = PlayerSettings.resizableWindow;
            try {
                Require(!WindowsPlayerConfiguration.Begin(BuildTarget.StandaloneOSX), "macOS unexpectedly received the Windows window-mode override.");
                Require(PlayerSettings.fullScreenMode == mode, "Checking macOS changed its configured window mode.");
                Require(WindowsPlayerConfiguration.Begin(BuildTarget.StandaloneWindows64), "Win64 did not receive its player override.");
                Require(PlayerSettings.fullScreenMode == FullScreenMode.FullScreenWindow,
                    "Win64 is not configured to start in borderless windowed mode.");
                Require(PlayerSettings.defaultScreenWidth == width && PlayerSettings.defaultScreenHeight == height &&
                    PlayerSettings.resizableWindow == resizable,
                    "The Windows window-mode override changed resolution or resizing settings.");
            }
            finally { WindowsPlayerConfiguration.Restore(); }
            Require(PlayerSettings.fullScreenMode == mode, "The shared/macOS player window mode was not restored after Windows configuration.");
            Debug.Log("Windows player configuration validation passed: borderless Win64 default, preserved resolution/resizing, and restored shared/macOS mode.");
        }

        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
