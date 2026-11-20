namespace Chat.Gateway;

/// <summary>保留独立的存储配置入口，便于将来把数据目录与模型配置移入配置文件。</summary>
public static class StorageOptions
{
    public static string ResolveDataDirectory(string[] args) => GatewayOptions.ResolveDataDirectory(args);
}
