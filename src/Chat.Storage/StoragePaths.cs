namespace Chat.Storage;

/// <summary>数据落盘位置。测试里直接传临时目录，避免碰用户数据。</summary>
public static class StoragePaths
{
    public const string AppFolderName = "LLMChat";

    public static string DefaultDataDirectory
    {
        get
        {
            var os = Environment.OSVersion.Platform;
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", AppFolderName);
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", AppFolderName)
                : Path.Combine(xdg, AppFolderName);
        }
    }

    public static string DatabaseFile(string dataDirectory) => Path.Combine(dataDirectory, "chat.db");

    public static string SnapshotsDirectory(string dataDirectory) => Path.Combine(dataDirectory, "snapshots");

    /// <summary>附件内容寻址目录：{data}/files/{sha256}{ext}。</summary>
    public static string FilesDirectory(string dataDirectory) => Path.Combine(dataDirectory, FileStore.FolderName);

    public static string PluginsDirectory(string dataDirectory) => Path.Combine(dataDirectory, "plugins");
}
