using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chat.Kernel;

/// <summary>
/// 插件清单（plugins/&lt;id&gt;/manifest.json）。字段名与 design 文档一致，
/// 未知字段忽略——便于主程序超前于插件演进。
/// </summary>
public sealed record PluginManifest
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = "";

    public required string Version { get; init; }

    /// <summary>契约版本，只比较 Major。</summary>
    public required string ApiVersion { get; init; }

    public string? MinAppVersion { get; init; }

    /// <summary>支持的平台（<c>any</c>/<c>windows</c>/<c>linux</c>/<c>macos</c> + 架构）。缺省视为全平台。</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];

    public required string EntryAssembly { get; init; }

    /// <summary>可选：显式指定实现类型，缺省则扫描程序集。</summary>
    public IReadOnlyList<string> ProviderTypes { get; init; } = [];

    public bool ListedByDefault { get; init; } = true;

    [JsonIgnore]
    public string? Directory { get; init; }

    [JsonIgnore]
    public string? Source { get; init; }

    public static PluginManifest? TryRead(string manifestPath)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version))
                return null;
            return manifest with
            {
                Directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)),
                Source = manifestPath,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
