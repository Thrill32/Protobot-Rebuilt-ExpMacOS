using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Protobot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CustomModelAppearanceValidation {
        public static void InspectFixture() {
            string[] args = Environment.GetCommandLineArgs();
            int source = Array.IndexOf(args, "-customModelFixture");
            int capture = Array.IndexOf(args, "-customModelCapture");
            if (source < 0 || source + 1 >= args.Length) throw new ArgumentException("Pass -customModelFixture with an OBJ or GLB path.");
            string path = args[source + 1];
            string destination = capture >= 0 && capture + 1 < args.Length ? args[capture + 1] : null;
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
            object[] read = { path, 1f, null };
            var data = (CustomModelData)typeof(CustomModelImporter).GetMethod("ReadPackage", hidden).Invoke(null, read);
            typeof(CustomModel).GetMethod("DecodeSourceImages", hidden).Invoke(null,
                new[] { (object)data.appearance, (List<byte[]>)read[2] });
            var model = CustomModel.Create(data, new Vector3(0, 95, 0), Quaternion.identity);
            model.layer = 30;
            try {
                var renderer = model.GetComponent<MeshRenderer>();
                var mesh = model.GetComponent<MeshFilter>().sharedMesh;
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < mesh.subMeshCount; i++) {
                    int textureIndex = data.appearance?.materials[i].texture ?? -1;
                    if (textureIndex < 0) {
                        Debug.Log($"Fixture slot {i} faces={mesh.GetIndexCount(i) / 3} diffuse={materials[i].color} shader={materials[i].shader.name}");
                        continue;
                    }
                    var imported = data.appearance.textures[textureIndex];
                    var pixels = imported.GetPixels();
                    var sample = (imported.height / 2 * imported.width + imported.width / 2) * 3;
                    Debug.Log($"Fixture slot {i} faces={mesh.GetIndexCount(i) / 3} diffuse={materials[i].color} " +
                        $"map={materials[i].mainTexture.width}x{materials[i].mainTexture.height} shader={materials[i].shader.name} " +
                        $"central RGB={pixels[sample]},{pixels[sample + 1]},{pixels[sample + 2]}");
                }
                if (destination != null && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    Capture(renderer.bounds, destination);
            }
            finally { Object.DestroyImmediate(model); }
        }

        private static void Capture(Bounds bounds, string destination) {
            Directory.CreateDirectory(destination);
            var cameraObject = new GameObject("Custom appearance capture");
            var camera = cameraObject.AddComponent<Camera>();
            var rt = new RenderTexture(720, 600, 24);
            var image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try {
                camera.enabled = false;
                camera.cullingMask = 1 << 30;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.2f, .2f, .2f);
                camera.orthographic = true;
                camera.orthographicSize = bounds.extents.magnitude * .8f;
                camera.targetTexture = rt;
                var directions = new[] { new Vector3(0, 1, .001f), new Vector3(0, .55f, .85f), new Vector3(.7f, .65f, .3f),
                    new Vector3(0, 1, .001f) };
                for (int i = 0; i < directions.Length; i++) {
                    var direction = directions[i].normalized;
                    camera.transform.SetPositionAndRotation(bounds.center + direction * bounds.size.magnitude * 2,
                        Quaternion.LookRotation(-direction, i == 3 ? Vector3.back : Vector3.forward));
                    camera.Render();
                    RenderTexture.active = rt;
                    image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    image.Apply();
                    File.WriteAllBytes(Path.Combine(destination, $"view-{i}.png"), image.EncodeToPNG());
                }
            }
            finally {
                RenderTexture.active = previous;
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(rt);
            }
        }
    }
}
