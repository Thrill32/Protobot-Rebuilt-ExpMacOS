using UnityEngine;
using System.Collections;
using System;
using UnityEngine.Serialization;

namespace Protobot.Builds {
    [Serializable]
    public class ObjectData {
        public double xPos, yPos, zPos;
        public double xRot, yRot, zRot;
        public double rColor, gColor, bColor;
        public string states;
        [FormerlySerializedAs("meshId")] public string partId;
        [System.Runtime.Serialization.OptionalField] public string connectionId;
        [System.Runtime.Serialization.OptionalField] public CustomModelData customModel;
        [System.Runtime.Serialization.OptionalField] public bool hasPaint;
        public bool IsCustomModel => partId == CustomModel.PartId;
        public Vector3 GetPos() => new Vector3((float)xPos, (float)yPos, (float)zPos);
        public Quaternion GetRot() => Quaternion.Euler((float)xRot, (float)yRot, (float)zRot);
        public Color GetColor() => new Color((float)rColor, (float)gColor, (float)bColor, 1);

        public override bool Equals(object obj) {
            var data = obj as ObjectData;
            if (data == null) return false;
            
            if (data.GetPos() != GetPos()) return false;
            if (data.GetRot() != GetRot()) return false;
            if (data.partId != partId) return false;
            // Unity's inline JSON serializer can expand a null class into empty fields.
            if (IsCustomModel && (customModel == null || !customModel.SameAs(data.customModel))) return false;
            if (data.hasPaint != hasPaint) return false;
            // Pre-color builds omit ordinary RGB values. Preserve their legacy
            // comparison while tracking every tint created by the paint tool.
            if ((IsCustomModel || hasPaint) && data.GetColor() != GetColor()) return false;

            return true;
        }
    }
}
