using System.Reflection;
using Chat.Abstractions;

namespace Chat.Kernel;

/// <summary>加载成功的插件实例。</summary>
public sealed record LoadedPlugin(PluginCandidate Candidate, IChatProvider Provider, Assembly Assembly);

/// <summary>加载失败原因（可展示给用户或写日志）。</summary>
public sealed record PluginLoadFailure(PluginCandidate Candidate, string Reason);

public sealed record PluginLoadResult(IReadOnlyList<LoadedPlugin> Loaded, IReadOnlyList<PluginLoadFailure> Failures);

/// <summary>
/// 插件加载入口。所有插件都在独立 ALC 中加载，宿主只通过 <see cref="IChatProvider"/> 使用它们。
/// </summary>
public sealed class ProviderHost
{
    private readonly PluginCatalog _catalog;

    public ProviderHost(PluginCatalog catalog) => _catalog = catalog;

    public PluginLoadResult LoadAll(string appVersion)
    {
        var loaded = new List<LoadedPlugin>();
        var failures = new List<PluginLoadFailure>();

        foreach (var candidate in _catalog.Scan(appVersion))
        {
            try
            {
                var plugin = LoadOne(candidate);
                loaded.AddRange(plugin.Select(p => new LoadedPlugin(candidate, p, p.GetType().Assembly)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(new PluginLoadFailure(candidate, Describe(ex)));
            }
        }

        return new PluginLoadResult(loaded, failures);
    }

    public IReadOnlyList<IChatProvider> LoadProviders(PluginCandidate candidate) => LoadOne(candidate);

    private static IReadOnlyList<IChatProvider> LoadOne(PluginCandidate candidate)
    {
        var context = new PluginLoadContext(candidate.Manifest.Id, candidate.AssemblyPath);
        var assembly = context.LoadFromAssemblyPath(candidate.AssemblyPath);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        var wanted = candidate.Manifest.ProviderTypes;
        var providerTypes = types
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IChatProvider).IsAssignableFrom(t))
            .Where(t => wanted.Count == 0 || wanted.Contains(t.FullName ?? t.Name, StringComparer.Ordinal))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();

        if (providerTypes.Count == 0)
            throw new InvalidOperationException($"程序集 {Path.GetFileName(candidate.AssemblyPath)} 中没有可实例化的 IChatProvider 实现。");

        var providers = new List<IChatProvider>(providerTypes.Count);
        foreach (var type in providerTypes)
            providers.Add((IChatProvider)Activator.CreateInstance(type)!);
        return providers;
    }

    private static string Describe(Exception ex) => ex switch
    {
        FileLoadException => $"无法加载程序集：{ex.Message}",
        BadImageFormatException => "程序集格式不正确（位数或目标框架不匹配）。",
        ReflectionTypeLoadException rtle => "程序集类型加载失败：" + string.Join("; ", rtle.LoaderExceptions.Select(e => e?.Message)),
        _ => ex.GetType().Name + "：" + ex.Message,
    };
}
