using System.Net;

namespace Chat.Gateway;

public static class GatewayOptions
{
    public const string DefaultUrls = "http://127.0.0.1:5080";
    public const string DeepSeekBaseUrl = "https://api.deepseek.com";

    public static string ResolveUrls(string[] args)
    {
        var urls = Option(args, "--urls") ?? DefaultUrls;
        if (string.IsNullOrWhiteSpace(urls)) return DefaultUrls;
        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                throw new ArgumentException($"--urls 不是合法的绝对地址：{raw}");
            if (!IsLoopbackHost(uri.Host))
                throw new ArgumentException($"--urls 只允许 loopback 主机（127.0.0.1 / localhost / [::1]），收到：{uri.Host}");
        }
        return urls;
    }

    public static string ResolveDataDirectory(string[] args)
    {
        var value = Option(args, "--data-dir");
        return string.IsNullOrWhiteSpace(value)
            ? Chat.Storage.StoragePaths.DefaultDataDirectory
            : Path.GetFullPath(value);
    }

    public static string ResolveDemoRoot(string? webRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(webRoot) && File.Exists(Path.Combine(webRoot, "index.html")))
            return Path.GetFullPath(webRoot);
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "web", "chat-demo", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return Path.GetFullPath(candidate);
            current = current.Parent;
        }
        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "web", "chat-demo", "dist"));
        return fallback;
    }

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.Ordinal) ||
        host.Equals("::1", StringComparison.Ordinal) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal)) return args[index + 1];
        for (var index = 0; index < args.Length; index++)
        {
            var prefix = name + "=";
            if (args[index].StartsWith(prefix, StringComparison.Ordinal)) return args[index][prefix.Length..];
        }
        return null;
    }
}
