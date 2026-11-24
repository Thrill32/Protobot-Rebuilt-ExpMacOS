using System.Linq;

namespace Protobot {
    public sealed class ScrewPartGenerator : ChildPartGenerator {
        public const string Standard = "Standard Screw";
        public const string SetScrew = "Set Screw";
        public const string BearingRivet = "Bearing Rivet";

        public override bool UsesTwoParams => param1.value == Standard;

        // The saved option is the standard screw's original size token, or the fixed type.
        private static string SavedOption(string type, string size) => type == Standard ? size : type;

        public override string GetId() => GetComponent<PartType>().id + "-" + SavedOption(param1.value, param2.value);

        public string[] GetSavedOptions() => GetComponentsInChildren<PartData>()
            .Select(data => SavedOption(data.param1Value, data.param2Value)).ToArray();

        public override (string first, string second) ReadIdParameters(string[] values) {
            if (values.Length == 2 && values[1] is SetScrew or BearingRivet) return (values[1], "");
            if (values.Length == 2) return (Standard, values[1]);
            if (values.Length == 3 && values[1] == "Star") {
                var id = string.Join("-", values);
                var legacy = GetComponentsInChildren<PartData>().FirstOrDefault(data =>
                    data.param1Value == Standard && data.GetComponent<SavedObject>()?.id == id);
                if (legacy != null) return (Standard, legacy.param2Value);
            }
            return (Standard, values.Length == 1 ? "1/4in" : "");
        }
    }
}
