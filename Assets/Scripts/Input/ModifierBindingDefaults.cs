using System.Linq;
using UnityEngine.InputSystem;

namespace Protobot.InputEvents {
    public static class ModifierBindingDefaults {
        public const string WindowsGroupingPath = "<Keyboard>/z";
        private const string MigrationKey = "Input.GroupingBypassWindowsDefaultVersion";

        public static void ConfigureGroupingDefault(InputAction action, bool windows) {
            if (!windows || action == null) return;
            action.Disable();
            for (int i = action.bindings.Count - 1; i >= 0; i--)
                action.ChangeBinding(i).Erase();
            action.AddBinding(WindowsGroupingPath);
        }

        public static void MigrateWindowsGroupingRebind(RebindAction rebind, bool windows) {
            if (!windows || rebind == null || LocalPreferences.GetInt(MigrationKey, 0) >= 1) return;
            var paths = rebind.action.bindings.Select(binding => binding.effectivePath)
                .Where(path => !string.IsNullOrEmpty(path)).ToArray();
            if (paths.Length > 0 && paths.All(IsMetaPath)) {
                rebind.action.RemoveAllBindingOverrides();
                LocalPreferences.DeleteKey("Grouping Bypass");
            }
            LocalPreferences.SetInt(MigrationKey, 1);
            LocalPreferences.Save();
        }

        private static bool IsMetaPath(string path) => path.EndsWith("/leftMeta") || path.EndsWith("/rightMeta") || path.EndsWith("/meta");
    }
}
