using UnityEditor;
using UnityEditor.Events;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using System.Linq;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Tools;

namespace ProtobotEditorTools
{
    /// <summary>
    /// One-shot entry point used to finish the Built-in Render Pipeline to URP
    /// material migration. Keeping this callable also makes future imports of
    /// legacy material assets easy to repair from the command line.
    /// </summary>
    public static class UrpMaterialMigration
    {
        public static void ConvertProjectMaterials()
        {
            var upgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(
                typeof(UniversalRenderPipelineAsset));

            MaterialUpgrader.UpgradeProjectFolder(
                upgraders,
                "Convert Built-in materials to URP",
                MaterialUpgrader.UpgradeFlags.None);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Protobot URP material migration completed.");
        }

        public static void ConfigureCameraStack()
        {
            const string scenePath = "Assets/Scenes/Main.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            Camera mainCamera = null;
            Camera uiCamera = null;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                {
                    if (camera.name == "Main Camera")
                        mainCamera = camera;
                    else if (camera.name == "UICamera")
                        uiCamera = camera;
                }
            }

            if (mainCamera == null || uiCamera == null)
                throw new System.InvalidOperationException(
                    "Main.unity must contain both 'Main Camera' and 'UICamera'.");

            var mainData = mainCamera.GetUniversalAdditionalCameraData();
            var uiData = uiCamera.GetUniversalAdditionalCameraData();

            mainData.renderType = CameraRenderType.Base;
            uiData.renderType = CameraRenderType.Overlay;

            mainData.cameraStack.RemoveAll(camera => camera == null || camera == uiCamera);
            mainData.cameraStack.Add(uiCamera);

            EditorUtility.SetDirty(mainCamera);
            EditorUtility.SetDirty(uiCamera);
            EditorUtility.SetDirty(mainData);
            EditorUtility.SetDirty(uiData);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log("Protobot URP camera stack configured: Main Camera -> UICamera.");
        }

        public static void ConfigureOutlineRenderer()
        {
            const string rendererPath = "Assets/Universal RP_Renderer.asset";
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (rendererData == null)
                throw new System.InvalidOperationException(
                    $"Unable to load the active URP renderer at '{rendererPath}'.");

            if (rendererData.TryGetRendererFeature<URPOutlineFeature>(out var existingFeature))
            {
                existingFeature.SetActive(true);
                EditorUtility.SetDirty(existingFeature);
                rendererData.SetDirty();
                AssetDatabase.SaveAssets();
                Debug.Log("Protobot URP outline renderer feature was already configured.");
                return;
            }

            var feature = ScriptableObject.CreateInstance<URPOutlineFeature>();
            feature.name = "Easy Performant Outline";
            feature.hideFlags = HideFlags.HideInHierarchy;
            feature.SetActive(true);
            feature.Create();
            AssetDatabase.AddObjectToAsset(feature, rendererData);

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
                throw new System.InvalidOperationException(
                    "Unable to determine the outline renderer feature's local asset ID.");

            var serializedRenderer = new SerializedObject(rendererData);
            serializedRenderer.Update();

            var features = serializedRenderer.FindProperty("m_RendererFeatures");
            var featureMap = serializedRenderer.FindProperty("m_RendererFeatureMap");
            var index = features.arraySize;

            features.InsertArrayElementAtIndex(index);
            features.GetArrayElementAtIndex(index).objectReferenceValue = feature;
            featureMap.InsertArrayElementAtIndex(index);
            featureMap.GetArrayElementAtIndex(index).longValue = localId;

            serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
            rendererData.SetDirty();
            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(rendererPath, ImportAssetOptions.ForceUpdate);

            Debug.Log("Protobot URP outline renderer feature configured.");
        }

        public static void ConfigureNoclipCamera()
        {
            const string keybindingsPath = "Assets/Prefabs/Keybindings.prefab";
            const string shortcutPath = "Assets/Prefabs/Shortcut Container.prefab";
            const string scenePath = "Assets/Scenes/Main.unity";

            var keybindingsRoot = PrefabUtility.LoadPrefabContents(keybindingsPath);
            try
            {
                Transform cameraGroup = null;
                foreach (var candidate in keybindingsRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (candidate.name == "Camera")
                    {
                        cameraGroup = candidate;
                        break;
                    }
                }
                if (cameraGroup == null)
                    throw new System.InvalidOperationException(
                        "The Keybindings prefab does not contain a Camera group.");

                var noclipTransform = cameraGroup.Find("Noclip Camera");
                if (noclipTransform == null)
                {
                    var noclipObject = new GameObject("Noclip Camera");
                    noclipObject.transform.SetParent(cameraGroup, false);

                    var inputEvent = noclipObject.AddComponent<InputEvent>();
                    inputEvent.defaultAction = new InputAction("Default", InputActionType.Button);
                    inputEvent.defaultAction.AddBinding("<Keyboard>/shift");

                    var serializedInput = new SerializedObject(inputEvent);
                    serializedInput.FindProperty("allowModifierOnly").boolValue = true;
                    serializedInput.ApplyModifiedPropertiesWithoutUndo();
                }

                PrefabUtility.SaveAsPrefabAsset(keybindingsRoot, keybindingsPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(keybindingsRoot);
            }

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var pivotObject = GameObject.Find("Main Camera Pivot");
            if (pivotObject == null)
                throw new System.InvalidOperationException(
                    "Main.unity does not contain 'Main Camera Pivot'.");

            InputEvent noclipInput = null;
            foreach (var input in Object.FindObjectsByType<InputEvent>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (input.name == "Noclip Camera")
                {
                    noclipInput = input;
                    break;
                }
            }

            if (noclipInput == null)
                throw new System.InvalidOperationException(
                    "The Noclip Camera input was not instantiated in Main.unity.");

            var controller = pivotObject.GetComponent<NoclipCameraInput>();
            if (controller == null)
                controller = pivotObject.AddComponent<NoclipCameraInput>();

            var serializedController = new SerializedObject(controller);
            serializedController.FindProperty("activationInput").objectReferenceValue = noclipInput;
            serializedController.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(controller);

            RebindUI noclipUi = null;
            RebindUI orbitUi = null;
            foreach (var ui in Object.FindObjectsByType<RebindUI>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var serializedUi = new SerializedObject(ui);
                var input = serializedUi.FindProperty("inputEvent").objectReferenceValue as InputEvent;
                if (input == null)
                    continue;

                if (input.name == "Noclip Camera")
                    noclipUi = ui;
                else if (input.name == "Orbit")
                    orbitUi = ui;
            }

            if (noclipUi == null)
            {
                if (orbitUi == null)
                    throw new System.InvalidOperationException(
                        "Unable to locate the Camera keybinding section in Preferences.");

                var shortcutPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(shortcutPath);
                var shortcut = PrefabUtility.InstantiatePrefab(
                    shortcutPrefab, orbitUi.transform.parent) as GameObject;
                shortcut.name = "Noclip Camera Shortcut";
                shortcut.transform.SetSiblingIndex(orbitUi.transform.GetSiblingIndex() + 1);

                noclipUi = shortcut.GetComponentInChildren<RebindUI>(true);
                var serializedUi = new SerializedObject(noclipUi);
                serializedUi.FindProperty("inputEvent").objectReferenceValue = noclipInput;
                serializedUi.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(noclipUi);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log("Protobot noclip camera configured with a rebindable Shift shortcut.");
        }

        public static void ConfigureSnapBypassAndConnectTool()
        {
            const string keybindingsPath = "Assets/Prefabs/Keybindings.prefab";
            const string shortcutPath = "Assets/Prefabs/Shortcut Container.prefab";
            const string scenePath = "Assets/Scenes/Main.unity";

            var keybindingsRoot = PrefabUtility.LoadPrefabContents(keybindingsPath);
            try
            {
                var generalGroup = keybindingsRoot.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(candidate => candidate.name == "General");
                if (generalGroup == null)
                    throw new System.InvalidOperationException(
                        "The Keybindings prefab does not contain a General group.");

                var snapTransform = generalGroup.Find("Snap Bypass");
                if (snapTransform == null)
                {
                    var snapObject = new GameObject("Snap Bypass");
                    snapObject.transform.SetParent(generalGroup, false);

                    var inputEvent = snapObject.AddComponent<InputEvent>();
                    inputEvent.defaultAction = new InputAction("Default", InputActionType.Button);
                    inputEvent.defaultAction.AddBinding("<Keyboard>/ctrl");
                    inputEvent.defaultAction.AddBinding("<Keyboard>/leftMeta");
                    inputEvent.defaultAction.AddBinding("<Keyboard>/rightMeta");

                    var serializedInput = new SerializedObject(inputEvent);
                    serializedInput.FindProperty("allowModifierOnly").boolValue = true;
                    serializedInput.ApplyModifiedPropertiesWithoutUndo();

                    snapObject.AddComponent<SnapBypassInput>();
                }
                else if (snapTransform.GetComponent<SnapBypassInput>() == null)
                {
                    snapTransform.gameObject.AddComponent<SnapBypassInput>();
                }

                var connectInputTransform = generalGroup.Find("Enable Connect Tool");
                if (connectInputTransform == null)
                {
                    var connectInputObject = new GameObject("Enable Connect Tool");
                    connectInputObject.transform.SetParent(generalGroup, false);

                    var inputEvent = connectInputObject.AddComponent<InputEvent>();
                    inputEvent.defaultAction = new InputAction("Default", InputActionType.Button);
                    inputEvent.defaultAction.AddBinding("<Keyboard>/4");
                }

                PrefabUtility.SaveAsPrefabAsset(keybindingsRoot, keybindingsPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(keybindingsRoot);
            }

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            InputEvent snapInput = null;
            InputEvent connectInput = null;
            RebindUI snapUi = null;
            RebindUI connectInputUi = null;
            RebindUI colorToolUi = null;
            RebindUI duplicateUi = null;
            foreach (var input in Object.FindObjectsByType<InputEvent>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (input.name == "Snap Bypass")
                    snapInput = input;
                else if (input.name == "Enable Connect Tool")
                    connectInput = input;
            }

            foreach (var ui in Object.FindObjectsByType<RebindUI>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var serializedUi = new SerializedObject(ui);
                var input = serializedUi.FindProperty("inputEvent").objectReferenceValue as InputEvent;
                if (input == null)
                    continue;

                if (input.name == "Snap Bypass")
                    snapUi = ui;
                else if (input.name == "Enable Connect Tool")
                    connectInputUi = ui;
                else if (input.name == "Enable Color Tool")
                    colorToolUi = ui;
                else if (input.name == "Duplicate")
                    duplicateUi = ui;
            }

            if (snapInput == null)
                throw new System.InvalidOperationException(
                    "The Snap Bypass input was not instantiated in Main.unity.");

            if (connectInput == null)
                throw new System.InvalidOperationException(
                    "The Enable Connect Tool input was not instantiated in Main.unity.");

            if (snapUi == null)
            {
                if (duplicateUi == null)
                    throw new System.InvalidOperationException(
                        "Unable to locate the Selection keybinding section in Preferences.");

                var shortcutPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(shortcutPath);
                var shortcut = PrefabUtility.InstantiatePrefab(
                    shortcutPrefab, duplicateUi.transform.parent) as GameObject;
                shortcut.name = "Snap Bypass Shortcut";
                shortcut.transform.SetSiblingIndex(duplicateUi.transform.GetSiblingIndex() + 1);

                snapUi = shortcut.GetComponentInChildren<RebindUI>(true);
                var serializedUi = new SerializedObject(snapUi);
                serializedUi.FindProperty("inputEvent").objectReferenceValue = snapInput;
                serializedUi.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(snapUi);
            }

            if (connectInputUi == null)
            {
                if (colorToolUi == null)
                    throw new System.InvalidOperationException(
                        "Unable to locate the tool keybinding section in Preferences.");

                var shortcutPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(shortcutPath);
                var shortcut = PrefabUtility.InstantiatePrefab(
                    shortcutPrefab, colorToolUi.transform.parent) as GameObject;
                shortcut.name = "Enable Connect Tool Shortcut";
                shortcut.transform.SetSiblingIndex(colorToolUi.transform.GetSiblingIndex() + 1);

                connectInputUi = shortcut.GetComponentInChildren<RebindUI>(true);
                var serializedUi = new SerializedObject(connectInputUi);
                serializedUi.FindProperty("inputEvent").objectReferenceValue = connectInput;
                serializedUi.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(connectInputUi);
            }

            var sidebar = GameObject.Find("Tools Sidebar")?.transform;
            var transformTool = GameObject.Find("Transform Tool");
            var primarySelection = GameObject.Find("Primary Selection Manager")?
                .GetComponent<SelectionManager>();
            var mouseCast = GameObject.Find("Main Camera")?.GetComponent<MouseCast>();

            if (sidebar == null || transformTool == null ||
                primarySelection == null || mouseCast == null)
            {
                throw new System.InvalidOperationException(
                    "Main.unity is missing a required object for the Connect tool.");
            }

            var connectObject = GameObject.Find("Connect Tool");
            if (connectObject == null)
            {
                connectObject = Object.Instantiate(transformTool, sidebar);
                connectObject.name = "Connect Tool";

                var rect = connectObject.GetComponent<RectTransform>();
                rect.anchoredPosition = new Vector2(20.000061f, -145.5f);

                var options = connectObject.transform.Find("Transform Tool Options");
                if (options != null)
                    Object.DestroyImmediate(options.gameObject);

                var mouseEvents = connectObject.GetComponent<MouseButtonEvents>();
                if (mouseEvents != null)
                    Object.DestroyImmediate(mouseEvents);

                var tooltip = connectObject.GetComponent<Protobot.UI.Tooltip>();
                if (tooltip != null)
                    tooltip.text = "Connect";

                var icon = connectObject.GetComponentsInChildren<Image>(true)
                    .FirstOrDefault(image => image.name == "Transform Icon");
                if (icon != null)
                {
                    icon.name = "Connect Icon";
                    icon.sprite = AssetDatabase.LoadAllAssetsAtPath(
                            "Assets/Sprites/Tools Icons/Connect Mode.png")
                        .OfType<Sprite>()
                        .FirstOrDefault();
                }
            }

            var toggle = connectObject.GetComponent<Toggle>();
            var connectTool = connectObject.GetComponent<ConnectTool>();
            if (connectTool == null)
                connectTool = connectObject.AddComponent<ConnectTool>();

            while (toggle.onValueChanged.GetPersistentEventCount() > 0)
                UnityEventTools.RemovePersistentListener(toggle.onValueChanged, 0);
            toggle.SetIsOnWithoutNotify(false);

            var serializedConnectTool = new SerializedObject(connectTool);
            serializedConnectTool.FindProperty("mouseCast").objectReferenceValue = mouseCast;
            serializedConnectTool.FindProperty("selectionManager").objectReferenceValue = primarySelection;
            serializedConnectTool.FindProperty("toolbarToggle").objectReferenceValue = toggle;
            serializedConnectTool.FindProperty("activateInput").objectReferenceValue = connectInput;
            serializedConnectTool.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(connectObject);
            EditorUtility.SetDirty(connectTool);
            EditorUtility.SetDirty(toggle);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log(
                "Configured Ctrl/Cmd snap bypass and the fourth Connect toolbar tool with key 4.");
        }
    }
}
