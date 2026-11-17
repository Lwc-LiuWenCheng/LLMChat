using System.Reflection;
using System.Runtime.Loader;

namespace Chat.Kernel;

/// <summary>
/// 每个插件一个 ALC：依赖解析到插件自己的目录，契约包（Chat.Abstractions）走默认上下文，
/// 保证宿主与插件看到同一份类型。注意：ALC 只隔离依赖，不隔离崩溃——SO/OOM 仍会带走进程，
/// 因此上线前还必须过签名准入与子进程 selftest。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginName, string assemblyPath)
        : base($"plugin:{pluginName}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        _pluginDirectory = Path.GetDirectoryName(assemblyPath) ?? ".";
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, typeof(Chat.Abstractions.IChatProvider).Assembly.GetName().Name, StringComparison.Ordinal))
            return null; // 交由 Default 上下文

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var local = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(local)) path = local;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
