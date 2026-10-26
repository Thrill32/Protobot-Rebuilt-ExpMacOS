using System;
using System.Collections;
using System.Collections.Generic;
using Protobot.Outlining;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    [Serializable]
    public class Parameter {
        public string name;
        public string value;
        
        [Header("Custom")]
        public bool custom;
        public string customUnit;
        public string customDefault;
        public Vector2 customLimits;
    }
    
    public abstract class PartGenerator : MonoBehaviour {
        public Parameter param1;
        public Parameter param2;
        public bool UsesParams => param1.name.Length > 0;
        public bool UsesTwoParams => param2.name.Length > 0;
        
        public void InitParamValues() {
            if (!UsesParams) return;

            param1.value = param1.custom ? param1.customDefault : GetParam1Options()[0];

            if (UsesTwoParams)
                param2.value = param2.custom ? param2.customDefault : GetParam2Options()[0];
        }
        
        public abstract List<string> GetParam1Options();
        public abstract List<string> GetParam2Options();

        public string GetId() {
            var partType = GetComponent<PartType>();
            var id = partType.id;
            
            if (UsesParams) {
                id += "-" + param1.value;

                if (UsesTwoParams)
                    id += "-" + param2.value;
            }

            return id;
        }

        public void SetId(GameObject obj) {
            var savedObj = obj.GetComponent<SavedObject>();

            if (savedObj == null)
                savedObj = obj.AddComponent<SavedObject>();
            
            savedObj.id = GetId();
        }
        public abstract Mesh GetMesh();
        public abstract GameObject Generate(Vector3 position, Quaternion rotation);

        public virtual bool TryGetPartData(out PartData partData) {
            partData = null;
            return false;
        }

        // Describe an attachment in generated-part coordinates without spawning a part.
        public virtual bool TryGetPlacementHole(out Pose pose, out float depth) {
            pose = Pose.identity;
            depth = 0;
            if (!TryGetPartData(out var data) || data == null) return false;
            var hole = data.primaryHole;
            if (hole == null) {
                foreach (var candidate in data.GetComponentsInChildren<HoleCollider>(true)) {
                    if (hole == null || data.transform.InverseTransformPoint(candidate.transform.position).sqrMagnitude <
                        data.transform.InverseTransformPoint(hole.transform.position).sqrMagnitude)
                        hole = candidate;
                }
            }
            if (hole == null) return false;
            pose = new Pose(data.transform.InverseTransformPoint(hole.transform.position),
                Quaternion.Inverse(data.transform.rotation) * hole.transform.rotation);
            depth = hole.transform.TransformVector(Vector3.forward).magnitude;
            return true;
        }
        
        public void RemoveDataScripts(GameObject obj) {
            if (obj.TryGetComponent(out PartData partData)) 
                Destroy(partData);
        }
    }
}
