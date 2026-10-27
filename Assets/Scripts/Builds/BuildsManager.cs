using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using UnityEngine;

using Protobot.UI;
using Protobot.InputEvents;

namespace Protobot.Builds {
    public class BuildsManager : MonoBehaviour {
        [SerializeField] private InputEvent saveInput;

        [SerializeField] private UnsavedChangesUI unsavedChangesMenu;
        
        /// <summary>
        /// Stores the path where the current build is located
        /// </summary>
        public string buildPath = "";
        
        /// <summary>
        /// Stores the currently saved data for the loaded build
        /// </summary>
        private BuildData savedBuildData;

        private string attemptPath = "";
        private BuildData attemptData;
        
        public BuildDataUnityEvent OnLoadBuild;
        public BuildDataUnityEvent OnSaveBuild;

        private bool avoidingQuit = false;
        private bool forceQuit = false;

        private bool IsNotSaved => buildPath == "";
        public bool IsFileOperationInProgress { get; private set; }
        private const string LastDirectoryKey = "buildFileDirectory";

        private string DialogDirectory => string.IsNullOrEmpty(buildPath)
            ? PlayerPrefs.GetString(LastDirectoryKey, "") : Path.GetDirectoryName(buildPath);

        private void Awake() {
            saveInput.performed += Save;
        }

        public void Start() {
            buildPath = "";
            
            SceneBuild.OnGenerateBuild += (data) => {
                OnLoadBuild.Invoke(data);
            };
            

            Application.wantsToQuit += () => {
                if (IsFileOperationInProgress || NativeFileDialogs.IsOpen) return false;
                avoidingQuit = HasUnsavedChanges() && !forceQuit;
                if (avoidingQuit) {
                    unsavedChangesMenu.Enable(IsNotSaved);
                }

                return !avoidingQuit;
            };

            unsavedChangesMenu.OnPressDiscard += () => {
                if (avoidingQuit) 
                    Quit();
                else
                    LoadAttempt();
            };

            unsavedChangesMenu.OnPressSave += () => {
                if (avoidingQuit)
                    SaveAndQuit();
                else
                    SaveAndLoadAttempt();
            };
            
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 1; i < arguments.Length; i++) {
                if (!IsBuildPath(arguments[i])) continue;
                _ = OpenPathAsync(arguments[i]);
                break;
            }
        }

        public string GetFileName() => PathToFileName(buildPath);
        
        public static string PathToFileName(string path) => string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path.Replace('\\', '/'));
        
        public async void Save() => await SaveAsync();

        public async void SaveAs() => await SaveAsync(true);

        public async void SaveAndQuit() {
            if (!await SaveAsync()) return;
            // Editing can continue while the disk write is pending.
            if (HasUnsavedChanges()) unsavedChangesMenu.Enable(IsNotSaved);
            else Quit();
        }

        private async Task<bool> SaveAsync(bool saveAs = false) {
            if (IsFileOperationInProgress || NativeFileDialogs.IsOpen) return false;
            IsFileOperationInProgress = true;
            try {
                var path = buildPath;
                if (saveAs || string.IsNullOrEmpty(path)) {
                    var name = string.IsNullOrEmpty(path) ? "Untitled" : Path.GetFileNameWithoutExtension(path);
                    path = await NativeFileDialogs.SaveFileAsync("Save Build File", DialogDirectory, name, "pbb");
                    if (string.IsNullOrEmpty(path) || this == null) return false;
                }
                var data = SceneBuild.ToBuildData();
                await Task.Run(() => {
                    using var file = File.Create(path);
                    new BinaryFormatter().Serialize(file, data);
                });
                if (this == null) return false;
                buildPath = path;
                savedBuildData = data;
                RememberDirectory(path);
                OnSaveBuild?.Invoke(data);
                return true;
            }
            catch (Exception exception) {
                Debug.LogError("Could not save build: " + exception.Message);
                return false;
            }
            finally {
                IsFileOperationInProgress = false;
            }
        }

        public void Quit() {
            forceQuit = true;
            Application.Quit();
        }

        public void AttemptQuit() {
            Application.Quit();
        }

        private bool HasUnsavedChanges() {
            var sceneBuild = SceneBuild.ToBuildData();
            
            if (IsNotSaved) {
                return sceneBuild.parts.Length > 0;
            }

            return !sceneBuild.CompareData(savedBuildData);
        }

        public async void OpenBuild() {
            if (IsFileOperationInProgress || NativeFileDialogs.IsOpen) return;
            IsFileOperationInProgress = true;
            try {
                var paths = await NativeFileDialogs.OpenFileAsync("Open Build File", DialogDirectory, "pbb");
                if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]) || this == null) return;
                await ReadAndAttemptLoadAsync(paths[0]);
            }
            catch (Exception exception) {
                Debug.LogError("Could not open build: " + exception.Message);
            }
            finally {
                IsFileOperationInProgress = false;
            }
        }

        public async Task<bool> OpenPathAsync(string path) {
            if (IsFileOperationInProgress || NativeFileDialogs.IsOpen) return false;
            IsFileOperationInProgress = true;
            try {
                return await ReadAndAttemptLoadAsync(path);
            }
            catch (Exception exception) {
                Debug.LogError("Could not open build: " + exception.Message);
                return false;
            }
            finally {
                IsFileOperationInProgress = false;
            }
        }

        private async Task<bool> ReadAndAttemptLoadAsync(string path) {
            var build = await Task.Run(() => ParsePath(path));
            if (build == null || this == null) return false;
            RememberDirectory(path);
            AttemptLoad(build, path);
            return true;
        }

        private static bool IsBuildPath(string path) => !string.IsNullOrEmpty(path) &&
            string.Equals(Path.GetExtension(path), ".pbb", StringComparison.OrdinalIgnoreCase);

        private static void RememberDirectory(string path) => PlayerPrefs.SetString(LastDirectoryKey, Path.GetDirectoryName(path) ?? "");
        
        /// <summary>
        /// Converts a given file path to BuildData
        /// </summary>
        public static BuildData ParsePath(string filePath) {
            if (!IsBuildPath(filePath) || !File.Exists(filePath)) return null;
            using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var build = new BinaryFormatter().Deserialize(file) as BuildData;
            if (build?.camera == null)
                throw new InvalidDataException("This file does not contain a valid build.");
            build.parts ??= Array.Empty<ObjectData>();
            return build;
        }
        
        /// <summary>
        /// Sets the attempt variables to be ready once a load call is given
        /// </summary>
        /// <param name="newData"></param>
        /// <param name="newPath"></param>
        public void AttemptLoad(BuildData newData, string newPath) {
            if (newData == null) return;
            attemptData = newData;
            attemptPath = newPath;

            if (HasUnsavedChanges()) {
                unsavedChangesMenu.Enable(IsNotSaved);
            }
            else {
                LoadAttempt();
            }
        }

        public async void SaveAndLoadAttempt() {
            if (!await SaveAsync()) return;
            if (HasUnsavedChanges()) unsavedChangesMenu.Enable(IsNotSaved);
            else LoadAttempt();
        }

        public void LoadAttempt() {
            if (attemptData == null) return;
            savedBuildData = attemptData;
            buildPath = attemptPath;
            SceneBuild.GenerateBuild(attemptData);

            // Call OutputPartsList to update the weight display after placing a part
                PartListOutput partListOutput = FindObjectOfType<PartListOutput>();
                if (partListOutput != null) {
                    partListOutput.CalculatePartsList(); // Recalculate and notify weight update
                }
        }

        /// <summary>
        /// Loads an empty build with an untitled.pbb
        /// </summary>
        public void CreateNewBuild() {
            if (IsFileOperationInProgress || NativeFileDialogs.IsOpen) return;
            AttemptLoad(SceneBuild.DefaultBuild, "");
        }
    }
}
