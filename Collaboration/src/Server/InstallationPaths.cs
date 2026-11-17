namespace Protobot.Collaboration.Server {
    // Shared private/no-replace publication primitives, also used by room restore.
    public static class InstallationPaths {
        public static string Stage(string target) => RoomBackup.Stage(target);
        public static void Publish(string stage, string target) => RoomBackup.PublishDirectory(stage, target);
    }
}
