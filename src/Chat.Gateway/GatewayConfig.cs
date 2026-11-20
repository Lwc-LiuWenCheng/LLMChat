namespace Chat.Gateway;

/// <summary>网关启动配置。测试可显式注入临时数据目录与假的上游 handler。</summary>
public sealed record GatewayConfig(
    string DataDirectory,
    string Urls,
    string DemoRoot,
    HttpMessageHandler? UpstreamHandler = null)
{
    public bool HasDemo => File.Exists(Path.Combine(DemoRoot, "index.html"));

    public static GatewayConfig FromArgs(string[] args) => new(
        GatewayOptions.ResolveDataDirectory(args),
        GatewayOptions.ResolveUrls(args),
        GatewayOptions.ResolveDemoRoot());
}
