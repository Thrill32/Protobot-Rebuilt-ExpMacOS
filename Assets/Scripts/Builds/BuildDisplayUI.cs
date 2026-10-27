using UnityEngine;
using UnityEngine.UI;
using Protobot.Builds;
using System;
using System.Collections;
using TMPro;

namespace Protobot.UI {
    public class BuildDisplayUI : MonoBehaviour {
        public BuildsManager buildsManager;
        public TMP_Text buildNameText;
        public string buildPath;

        public void UpdateUI(string path, BuildsManager _buildsManager) {
            buildPath = path;
            buildNameText.text = BuildsManager.PathToFileName(path);
            buildsManager = _buildsManager;
        }

        public async void LoadBuild() {
            if (buildsManager.IsFileOperationInProgress || NativeFileDialogs.IsOpen) return;
            if (!await buildsManager.OpenPathAsync(buildPath) && this != null)
                StartCoroutine(DisplayLoadError());
        }
        
        IEnumerator DisplayLoadError() {
            buildNameText.text = "Could not open file...";
            buildNameText.color = new Color(0.9f,0.3f,0.3f,0);
            yield return new WaitForSeconds(1);
            BuildsListUI.OnLoadError?.Invoke();
        }
    }
}
