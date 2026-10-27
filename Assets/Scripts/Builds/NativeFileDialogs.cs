using System;
using System.Threading;
using System.Threading.Tasks;
using SFB;

namespace Protobot.Builds {
    /// <summary>Keep native panel callbacks outside Unity scene/UI mutations.</summary>
    public static class NativeFileDialogs {
        public static bool IsOpen { get; private set; }

        public static Task<string[]> OpenFileAsync(string title, string directory, string extension) =>
            Show<string[]>(complete => StandaloneFileBrowser.OpenFilePanelAsync(title, directory, extension, false, complete));

        public static Task<string> SaveFileAsync(string title, string directory, string name, string extension) =>
            Show<string>(complete => StandaloneFileBrowser.SaveFilePanelAsync(title, directory, name, extension, complete));

        private static async Task<T> Show<T>(Action<Action<T>> show) {
            if (IsOpen) throw new InvalidOperationException("A file dialog is already open.");
            var context = SynchronizationContext.Current ??
                throw new InvalidOperationException("File dialogs must be opened on the Unity thread.");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            IsOpen = true;
            try {
                // Cocoa can invoke this inside its modal event loop. Always return
                // to Unity's next update before loading a scene or opening more UI.
                show(result => context.Post(_ => completion.TrySetResult(result), null));
                return await completion.Task;
            }
            finally {
                IsOpen = false;
            }
        }
    }
}
