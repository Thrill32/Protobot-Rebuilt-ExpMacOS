using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Protobot.Builds;
using UnityEngine.PlayerLoop;


namespace Protobot.UI {
    public class BuildsListUI : MonoBehaviour {
        private string PrefsKey => "recentBuilds";
        private int sizeLimit = 5;

        [SerializeField] private BuildsManager buildsManager;
        
        [SerializeField] private GameObject buildDisplayPrefab;
        [SerializeField] private List<BuildDisplayUI> loadedDisplays;
        [SerializeField] private RectTransform listContentRect;

        public static Action OnLoadError;

        private float displayHeight;
        private void Start() {
            buildsManager.OnLoadBuild.AddListener(b => AddPath(buildsManager.buildPath));
            buildsManager.OnSaveBuild.AddListener(b => AddPath(buildsManager.buildPath));
            
            displayHeight = buildDisplayPrefab.GetComponent<RectTransform>().sizeDelta.y + 5;
            
            UpdateUI();

            OnLoadError += UpdateUI;
        }

        public void AddPath(string path) {
            if (string.IsNullOrWhiteSpace(path)) return;
            var paths = ReadPaths();
            
            if (paths.Contains(path)) {
                paths.Remove(path);
            }
             
            paths.Insert(0, path);
            
            if (paths.Count > sizeLimit)
                paths.RemoveAt(paths.Count - 1);
            
            SavePaths(paths);

            UpdateUI();
        }

        public void SavePaths(List<String> paths) {
            PlayerPrefs.SetString(PrefsKey, String.Join("\n", paths));
        }

        public List<string> ReadPaths() => PlayerPrefs.GetString(PrefsKey).Split('\n')
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).Take(sizeLimit).ToList();

        public List<string> GetPaths() {
            // Never probe saved network/cloud paths during startup or UI refresh.
            // File.Exists can block the first frame while a volume/provider stalls.
            // OpenPathAsync checks availability on a worker when a file is chosen;
            // keep unavailable entries so they can be retried when reconnected.
            return ReadPaths();
        }

        public void UpdateUI() {
            DestroyLoadedDisplays();
            var buildPaths = GetPaths();

            for (int i = 0; i < buildPaths.Count; i++) {
                BuildDisplayUI newDisplayUI = Instantiate(buildDisplayPrefab).GetComponent<BuildDisplayUI>();
                RectTransform rect = newDisplayUI.GetComponent<RectTransform>();

                rect.SetParent(listContentRect);
                rect.anchoredPosition = new Vector2(0, i * -displayHeight);
                rect.localScale = Vector3.one;

                newDisplayUI.UpdateUI(buildPaths[i], buildsManager);

                loadedDisplays.Add(newDisplayUI);
            }

            listContentRect.sizeDelta = new Vector2(listContentRect.sizeDelta.x, buildPaths.Count * displayHeight);
        }

        public void DestroyLoadedDisplays() {
            foreach (BuildDisplayUI display in loadedDisplays) {
                Destroy(display.gameObject);
            }

            loadedDisplays = new List<BuildDisplayUI>();
        }
    }
}
