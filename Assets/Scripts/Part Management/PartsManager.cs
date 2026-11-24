using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace Protobot {
    public static class PartsManager {
        public static PartType[] partTypes;

        [RuntimeInitializeOnLoadMethod]
        public static void LoadPartTypes() {
            partTypes = Resources.LoadAll("Part Prefabs").Select(p => (p as GameObject)?.GetComponent<PartType>()).ToArray();
            foreach (var p in partTypes)
                p.GetComponent<PartGenerator>().InitParamValues();
        }

        public static PartType GetPartType(string id) {
            var idSplit = id.Split('-');
            var typeId = idSplit[0];
            return partTypes.FirstOrDefault(partType => partType.id == typeId);
        }
        
        public static GameObject GeneratePart(string id, Vector3 pos, Quaternion rot) {
            var partType = GetPartType(id);
            if (partType == null) return null;
            var gen = partType.GetComponent<PartGenerator>();
            var (first, second) = gen.ReadIdParameters(id.Split('-'));

            // Older builds omitted some of these options or stored the wheel size first.
            if (id == "NUT") first = "Lock";
            if ((gen.name == "Omni Wheel" || gen.name == "Traction Wheel") && second == "")
                (first, second) = ("V1", first);
            if (gen.name == "Motor" && first == "") first = "11W";
            if (gen.name == "Block Bearing" && first == "") first = "Normal";
            if (gen.name == "Cylinder" && second == "") second = "Normal";
            if (gen.name == "Ring" && first == "") first = "Red";

            string previousParam1 = gen.param1.value;
            string previousParam2 = gen.param2.value;
            try {
                gen.param1.value = first;
                gen.param2.value = second;
                return gen.Generate(pos, rot);
            }
            finally {
                gen.param1.value = previousParam1;
                gen.param2.value = previousParam2;
            }
        }

        /// <Summary> Returns a list of all loaded parts in the current scene </Summary>
        public static List<GameObject> FindLoadedObjects() {
            return GameObject.FindObjectsOfType<SavedObject>().Select(x => x.gameObject).ToList();
        }

        internal static IEnumerable<CustomModel> FindDeletedAppearance() =>
            GameObject.FindObjectsByType<CustomModel>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(model => model.IsDeletedAppearance);

        /// <Summary> Destroys all loaded parts in the current scene </Summary>
        public static void DestroyLoadedObjects() {
            foreach (var model in FindDeletedAppearance()) model.DiscardDeletedAppearance();
            foreach (var obj in FindLoadedObjects()) {
                obj.SetActive(false);
                if (obj.TryGetComponent<CustomModel>(out var custom)) custom.ReleaseGeometry();
                GameObject.Destroy(obj);
            }
        }
    }
}
