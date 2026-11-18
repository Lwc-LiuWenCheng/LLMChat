using System.Net.Http.Headers;
using Chat.Abstractions;
using Chat.Core;
using Chat.Kernel;
using Chat.Storage;

namespace Chat.App.Services;

/// <summary>
/// 应用组合根：数据目录、SQLite、插件发现与加载。UI 只依赖这里暴露的仓储与 provider 列表。
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly FileStore _files;
    private readonly IUrlLauncher _urlLauncher;

    private AppServices(string dataDirectory, ChatDatabase database, ProviderHost host,
        IReadOnlyList<LoadedPlugin> plugins, IReadOnlyList<PluginLoadFailure> failures,
        IUrlLauncher? urlLauncher = null, HttpMessageInvoker? http = null)
    {
        DataDirectory = dataDirectory;
        Database = database;
        Sessions = new SessionRepository(database);
        Messages = new MessageRepository(database);
        Settings = new SettingsRepository(database);
        Snapshot = new SnapshotService(database, StoragePaths.SnapshotsDirectory(dataDirectory));
        Host = host;
        Plugins = plugins;
        Failures = failures;

        _http = http as HttpClient ?? new HttpClient(new HttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        if (!ReferenceEquals(_http, http))
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
        _files = new FileStore(dataDirectory);
        _urlLauncher = urlLauncher ?? new SystemUrlLauncher();
    }

    public string DataDirectory { get; }

    public ChatDatabase Database { get; }

    public SessionRepository Sessions { get; }

    public MessageRepository Messages { get; }

    public SettingsRepository Settings { get; }

    public SnapshotService Snapshot { get; }

    public ProviderHost Host { get; }

    public IReadOnlyList<LoadedPlugin> Plugins { get; }

    public IReadOnlyList<PluginLoadFailure> Failures { get; }

    public HttpMessageInvoker Http => _http;

    /// <summary>
    /// 为一次 provider 请求创建附件字节读取器：相对路径统一经 <see cref="FileStore.ResolvePath"/> 校验，
    /// 同一请求内相同路径只读一次。读不到（文件缺失、越界、IO 错误）返回 null，由 PromptAssembler 降级。
    /// </summary>
    public AttachmentReader CreateAttachmentReader()
    {
        var cache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        return relativePath =>
        {
            if (cache.TryGetValue(relativePath, out var cached)) return cached;
            byte[]? bytes;
            try
            {
                var fullPath = _files.ResolvePath(relativePath);
                bytes = File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                bytes = null;
            }
            cache[relativePath] = bytes;
            return bytes;
        };
    }

    /// <summary>DeepSeek 账户向导（宿主层服务，URL 打开与 HTTP 都可注入假实现）。</summary>
    public IDeepSeekAccountService DeepSeekAccount { get; private set; } = null!;

    public static AppServices Create(
        string? dataDirectory = null,
        IUrlLauncher? urlLauncher = null,
        HttpMessageInvoker? http = null)
    {
        var data = dataDirectory ?? StoragePaths.DefaultDataDirectory;
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(StoragePaths.SnapshotsDirectory(data));
        Directory.CreateDirectory(StoragePaths.PluginsDirectory(data));

        var database = new ChatDatabase(StoragePaths.DatabaseFile(data));
        database.Initialize();

        var builtinRoot = Path.Combine(AppContext.BaseDirectory, "plugins");
        var catalog = new PluginCatalog(builtinRoot, StoragePaths.PluginsDirectory(data));
        var host = new ProviderHost(catalog);
        var result = host.LoadAll(AppInfo.Version);

        var services = new AppServices(data, database, host, result.Loaded, result.Failures, urlLauncher, http);
        services.DeepSeekAccount = new DeepSeekAccountService(services._http, services.Settings, services._urlLauncher);
        return services;
    }

    public IChatProvider? FindProvider(string? providerId)
    {
        var all = Plugins.Select(p => p.Provider).ToList();
        if (providerId is { Length: > 0 })
        {
            var exact = all.FirstOrDefault(p => string.Equals(p.Descriptor.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        return all.FirstOrDefault();
    }

    public ProviderContext CreateContext(string sessionId) =>
        new(sessionId, Settings, _http, Path.Combine(DataDirectory, "tmp", sessionId));

    public void Dispose() => _http.Dispose();
}
