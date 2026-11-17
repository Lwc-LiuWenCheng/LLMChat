using System.Runtime.InteropServices;

namespace Chat.Kernel;

/// <summary>插件来源：随应用内置，或用户目录（可覆盖内置版本）。</summary>
public enum PluginSource
{
    Builtin,
    User,
}

/// <summary>目录扫描结果。</summary>
public sealed record PluginCandidate(PluginManifest Manifest, PluginSource Source, string AssemblyPath);

/// <summary>
/// 扫描内置/用户两个插件根目录。版本选择规则：同 id 用户目录优先；同目录多版本取最高 SemVer；
/// 不满足 apiVersion/platforms/minAppVersion 的记为 Unsupported 但保留在列表里（UI 可显示原因）。
/// </summary>
public sealed class PluginCatalog
{
    private readonly string _builtinRoot;
    private readonly string _userRoot;

    public PluginCatalog(string builtinRoot, string userRoot)
    {
        _builtinRoot = builtinRoot;
        _userRoot = userRoot;
    }

    public IReadOnlyList<PluginCandidate> Scan(string appVersion)
    {
        var all = new List<PluginCandidate>();
        all.AddRange(ScanRoot(_builtinRoot, PluginSource.Builtin));
        all.AddRange(ScanRoot(_userRoot, PluginSource.User));

        // 同 id：用户覆盖内置，其余取最高版本
        return all
            .GroupBy(c => c.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Source == PluginSource.User)
                          .ThenByDescending(c => ParseVersion(c.Manifest.Version))
                          .First())
            .Where(c => IsCompatible(c.Manifest, appVersion, out _))
            .OrderBy(c => c.Manifest.DisplayName)
            .ToList();
    }

    private static IEnumerable<PluginCandidate> ScanRoot(string root, PluginSource source)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
        {
            var manifest = PluginManifest.TryRead(manifestPath);
            if (manifest?.Directory is null) continue;
            var assemblyPath = Path.Combine(manifest.Directory, manifest.EntryAssembly);
            if (!File.Exists(assemblyPath)) continue;
            yield return new PluginCandidate(manifest, source, assemblyPath);
        }
    }

    public static bool IsCompatible(PluginManifest manifest, string appVersion, out string? reason)
    {
        if (!SameMajor(manifest.ApiVersion, Chat.Abstractions.PluginApi.Version))
        {
            reason = $"契约版本不匹配：插件 {manifest.ApiVersion}，主程序 {Chat.Abstractions.PluginApi.Version}";
            return false;
        }

        if (manifest.Platforms.Count > 0 && !manifest.Platforms.Any(p => MatchesPlatform(p)))
        {
            reason = $"不支持当前平台：{CurrentPlatform()}";
            return false;
        }

        if (manifest.MinAppVersion is { Length: > 0 } min && TryParse(min, out var minVersion) &&
            TryParse(appVersion, out var app) && app < minVersion)
        {
            reason = $"需要主程序 ≥ {min}";
            return false;
        }

        reason = null;
        return true;
    }

    public static string CurrentPlatform()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : "linux";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"{os}-{arch}";
    }

    private static bool MatchesPlatform(string declared)
    {
        var value = declared.Trim().ToLowerInvariant();
        if (value is "any" or "*" or "") return true;
        var current = CurrentPlatform();
        var os = current.Split('-')[0];
        return value == current || value == os || (value == "windows" && os == "windows");
    }

    private static bool SameMajor(string left, string right) =>
        TryParse(left, out var a) && TryParse(right, out var b) && a.Major == b.Major;

    private static Version ParseVersion(string text) => TryParse(text, out var v) ? v : new Version(0, 0);

    private static bool TryParse(string text, out Version version)
    {
        var clean = text.Split('-', '+')[0];
        return Version.TryParse(clean.Length == 0 ? "0.0" : clean, out version!);
    }
}
