using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

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
    }
}
