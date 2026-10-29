using System;
using System.Linq;
using Protobot;
using Protobot.Simulation;
using UnityEditor;
using UnityEngine;

namespace ProtobotEditorTools {
    public static class MechanismSetup {
        public static void ConfigureParts() {
            foreach (var path in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Resources/Part Prefabs/4Motion" }).Select(AssetDatabase.GUIDToAssetPath)) {
                if (path.EndsWith("/Gear.prefab", StringComparison.Ordinal)) continue;
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var type = source.GetComponent<PartType>();
                if (type == null || !(type.id == "SPKT" || type.id == "LKBR" || type.id == "CLMP" || type.id == "MCMS" ||
                    type.id == "OMNI" || type.id == "TWHL" || type.id == "FWHL" || type.id == "MECN")) continue;
                var root = PrefabUtility.LoadPrefabContents(path);
                try {
                    foreach (var data in root.GetComponentsInChildren<PartData>(true)) {
                        DriveKind? kind = type.id == "SPKT" ? DriveKind.Sprocket : type.id != "MCMS" ? DriveKind.Attachment : data.param1Value switch {
                            "Bevel Gear" => DriveKind.Bevel,
                            "Worm and Wheel" => data.param2Value == "Worm" ? DriveKind.Worm : DriveKind.WormWheel,
                            "Rack" => DriveKind.Rack,
                            "Hand Crank" or "Drop Off Cam" => DriveKind.Attachment,
                            _ => null
                        };
                        if (!kind.HasValue) continue;
                        var part = data.GetComponent<SimulationPart>() ?? data.gameObject.AddComponent<SimulationPart>();
                        part.Configure(data, kind.Value);
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }
        public static void Report() {
            foreach (var name in new[] { "Mechanisms", "Lock Bar", "Shaft Collar", "Gears and Sprockets/Sprocket" }) {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Part Prefabs/4Motion/" + name + ".prefab");
                foreach (var data in root.GetComponentsInChildren<PartData>(true)) {
                    var filter = data.GetComponent<MeshFilter>();
                    var center = data.primaryHole != null ? data.transform.InverseTransformPoint(data.primaryHole.transform.position) : filter.sharedMesh.bounds.center;
                    var axis = data.primaryHole != null ? data.transform.InverseTransformDirection(data.primaryHole.transform.forward) : Vector3.forward;
                    var vertices = filter.sharedMesh.vertices;
                    var offsets = vertices.Select(v => v - center).ToArray();
                    var radial = offsets.Select(v => Vector3.ProjectOnPlane(v, axis).magnitude).ToArray();
                    float min = offsets.Min(v => Vector3.Dot(v, axis)), max = offsets.Max(v => Vector3.Dot(v, axis));
                    var bins = Enumerable.Range(0, 8).Select(i => {
                        var ids = Enumerable.Range(0, offsets.Length).Where(j => Vector3.Dot(offsets[j], axis) >= Mathf.Lerp(min, max, i / 8f) - .001f &&
                            Vector3.Dot(offsets[j], axis) <= Mathf.Lerp(min, max, (i + 1) / 8f) + .001f).ToArray();
                        return ids.Length == 0 ? "-" : ids.Max(j => radial[j]).ToString("0.###");
                    });
                    Debug.Log($"MECHANICAL {name} {data.param1Value}/{data.param2Value}: center={center:F4}, axis={axis:F3}, bounds={filter.sharedMesh.bounds}, axial={min:0.####}..{max:0.####}, radius={radial.Max():0.####}, bins={string.Join(",", bins)}");
                }
            }
        }
    }
}
