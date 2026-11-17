using System.Reflection;

namespace Chat.App;

/// <summary>应用级常量：版本号与目录名。</summary>
public static class AppInfo
{
    public const string Name = "LLMChat";

    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return "0.1.0";
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
