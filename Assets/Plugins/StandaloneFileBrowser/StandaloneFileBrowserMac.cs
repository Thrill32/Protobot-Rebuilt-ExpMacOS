#if UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)

using System;
using System.Runtime.InteropServices;

namespace SFB {
    public class StandaloneFileBrowserMac : IStandaloneFileBrowser {
        private static Action<string[]> _openFileCb;
        private static Action<string[]> _openFolderCb;
        private static Action<string> _saveFileCb;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void AsyncCallback(string path);

        // Native panels outlive the P/Invoke call. Root their delegates until
        // completion rather than relying on temporary method-group delegates.
        private static readonly AsyncCallback OpenFileCallback = openFileCb;
        private static readonly AsyncCallback OpenFolderCallback = openFolderCb;
        private static readonly AsyncCallback SaveFileCallback = saveFileCb;

        [AOT.MonoPInvokeCallback(typeof(AsyncCallback))]
        private static void openFileCb(string result) {
            var callback = _openFileCb;
            _openFileCb = null;
            callback?.Invoke(string.IsNullOrEmpty(result) ? Array.Empty<string>() : result.Split((char)28));
        }

        [AOT.MonoPInvokeCallback(typeof(AsyncCallback))]
        private static void openFolderCb(string result) {
            var callback = _openFolderCb;
            _openFolderCb = null;
            callback?.Invoke(string.IsNullOrEmpty(result) ? Array.Empty<string>() : result.Split((char)28));
        }

        [AOT.MonoPInvokeCallback(typeof(AsyncCallback))]
        private static void saveFileCb(string result) {
            var callback = _saveFileCb;
            _saveFileCb = null;
            callback?.Invoke(result ?? "");
        }

        [DllImport("StandaloneFileBrowser")]
        private static extern void DialogWarmup();
        [DllImport("StandaloneFileBrowser")]
        private static extern void DialogReset();
        [DllImport("StandaloneFileBrowser")]
        private static extern IntPtr DialogOpenFilePanel(string title, string directory, string extension, bool multiselect);
        [DllImport("StandaloneFileBrowser")]
        private static extern void DialogOpenFilePanelAsync(string title, string directory, string extension, bool multiselect, AsyncCallback callback);
        [DllImport("StandaloneFileBrowser")]
        private static extern IntPtr DialogOpenFolderPanel(string title, string directory, bool multiselect);
        [DllImport("StandaloneFileBrowser")]
        private static extern void DialogOpenFolderPanelAsync(string title, string directory, bool multiselect, AsyncCallback callback);
        [DllImport("StandaloneFileBrowser")]
        private static extern IntPtr DialogSaveFilePanel(string title, string directory, string defaultName, string extension);
        [DllImport("StandaloneFileBrowser")]
        private static extern void DialogSaveFilePanelAsync(string title, string directory, string defaultName, string extension, AsyncCallback callback);

        public void PrepareDialogs() => DialogWarmup();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallQuitCleanup() {
            UnityEngine.Application.quitting -= ResetDialogs;
            UnityEngine.Application.quitting += ResetDialogs;
        }

#if UNITY_EDITOR_OSX
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallEditorCleanup() {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ResetDialogs;
            UnityEditor.EditorApplication.playModeStateChanged += state => {
                if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) ResetDialogs();
            };
        }
#endif

        private static void ResetDialogs() {
            DialogReset();
            // Settle pending tasks as cancelled when domain reload is disabled.
            var open = _openFileCb; var folder = _openFolderCb; var save = _saveFileCb;
            _openFileCb = _openFolderCb = null; _saveFileCb = null;
            open?.Invoke(Array.Empty<string>()); folder?.Invoke(Array.Empty<string>()); save?.Invoke("");
        }

        public string[] OpenFilePanel(string title, string directory, ExtensionFilter[] extensions, bool multiselect) {
            var paths = Marshal.PtrToStringAnsi(DialogOpenFilePanel(
                title,
                directory,
                GetFilterFromFileExtensionList(extensions),
                multiselect));
            return paths.Split((char)28);
        }

        public void OpenFilePanelAsync(string title, string directory, ExtensionFilter[] extensions, bool multiselect, Action<string[]> cb) {
            _openFileCb = cb;
            DialogOpenFilePanelAsync(
                title,
                directory,
                GetFilterFromFileExtensionList(extensions),
                multiselect,
                OpenFileCallback);
        }

        public string[] OpenFolderPanel(string title, string directory, bool multiselect) {
            var paths = Marshal.PtrToStringAnsi(DialogOpenFolderPanel(
                title,
                directory,
                multiselect));
            return paths.Split((char)28);
        }

        public void OpenFolderPanelAsync(string title, string directory, bool multiselect, Action<string[]> cb) {
            _openFolderCb = cb;
            DialogOpenFolderPanelAsync(
                title,
                directory,
                multiselect,
                OpenFolderCallback);
        }

        public string SaveFilePanel(string title, string directory, string defaultName, ExtensionFilter[] extensions) {
            return Marshal.PtrToStringAnsi(DialogSaveFilePanel(
                title,
                directory,
                defaultName,
                GetFilterFromFileExtensionList(extensions)));
        }

        public void SaveFilePanelAsync(string title, string directory, string defaultName, ExtensionFilter[] extensions, Action<string> cb) {
            _saveFileCb = cb;
            DialogSaveFilePanelAsync(
                title,
                directory,
                defaultName,
                GetFilterFromFileExtensionList(extensions),
                SaveFileCallback);
        }

        private static string GetFilterFromFileExtensionList(ExtensionFilter[] extensions) {
            if (extensions == null) {
                return "";
            }

            var filterString = "";
            foreach (var filter in extensions) {
                filterString += filter.Name + ";";

                foreach (var ext in filter.Extensions) {
                    filterString += ext + ",";
                }

                filterString = filterString.Remove(filterString.Length - 1);
                filterString += "|";
            }
            filterString = filterString.Remove(filterString.Length - 1);
            return filterString;
        }
    }
}

#endif
