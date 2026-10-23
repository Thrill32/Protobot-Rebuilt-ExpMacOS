using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Protobot {
    /// <summary>Parameter rules shared by placement, pick-part and existing-part edits.</summary>
    public static class PartParameterEditing {
        public static bool Load(PartGenerator generator, string id) {
            if (generator == null) return false;
            var values = id.Split('-');
            generator.InitParamValues();
            if (generator.UsesParams && values.Length > 1) generator.param1.value = values[1];
            if (generator.UsesTwoParams && values.Length > 2) generator.param2.value = values[2];
            Normalize(generator);
            return true;
        }

        public static void Normalize(PartGenerator generator) {
            if (!generator.UsesParams) return;
            Normalize(generator.param1, generator.GetParam1Options());
            // Shaft type controls the legal length even before GetMesh is called.
            if (generator is ShaftPartGenerator)
                generator.param2.customLimits.y = generator.param1.value == "Normal" ? 12f : 24f;
            if (generator.UsesTwoParams) Normalize(generator.param2, generator.GetParam2Options());
        }

        private static void Normalize(Parameter parameter, List<string> options) {
            if (parameter.custom) {
                if (!float.TryParse(parameter.value, out float value))
                    float.TryParse(parameter.customDefault, out value);
                value = Mathf.Clamp(value, parameter.customLimits.x, parameter.customLimits.y);
                if (parameter.customUnit == "Holes") value = Mathf.Round(value);
                parameter.value = value.ToString(CultureInfo.CurrentCulture);
            }
            else if (options != null && options.Count > 0 && !options.Contains(parameter.value))
                parameter.value = options[0];
        }

        public static bool Step(PartGenerator generator, int index, int direction) {
            if (!generator.UsesParams || (index == 1 && !generator.UsesTwoParams)) return false;
            var parameter = index == 0 ? generator.param1 : generator.param2;
            string before = generator.GetId();
            if (parameter.custom) {
                if (!float.TryParse(parameter.value, out float value)) return false;
                float increment = parameter.customUnit == "Holes" ? 1f : .5f;
                parameter.value = (value + direction * increment).ToString(CultureInfo.CurrentCulture);
            }
            else {
                var options = index == 0 ? generator.GetParam1Options() : generator.GetParam2Options();
                if (options == null || options.Count == 0) return false;
                parameter.value = options[Mathf.Clamp(options.IndexOf(parameter.value) + direction, 0, options.Count - 1)];
            }
            Normalize(generator);
            return before != generator.GetId();
        }

        // Recreate the part through its normal generator, so holes, colliders,
        // detectors, part-list metadata and its saved ID all match its new shape.
        public static GameObject Replace(GameObject original, PartGenerator generator,
            Vector3? holeAnchor = null, Vector3? localHole = null) {
            var replacement = generator.Generate(original.transform.position, original.transform.rotation);
            if (replacement == null) return null;
            if (holeAnchor.HasValue && localHole.HasValue) {
                var hole = replacement.GetComponentsInChildren<HoleCollider>()
                    .OrderBy(candidate => (replacement.transform.InverseTransformPoint(candidate.transform.position) - localHole.Value).sqrMagnitude)
                    .FirstOrDefault();
                if (hole != null) replacement.transform.position += holeAnchor.Value - hole.transform.position;
            }
            replacement.GetComponent<SavedObject>().state = original.GetComponent<SavedObject>().state;
            if (original.TryGetComponent<Renderer>(out var oldRenderer) &&
                replacement.TryGetComponent<Renderer>(out var newRenderer))
                newRenderer.sharedMaterial = new Material(newRenderer.sharedMaterial) { color = oldRenderer.sharedMaterial.color };
            return replacement;
        }
    }
}
