using System.Net.Http.Headers;
using Chat.Abstractions;
using Chat.Kernel;
using Chat.Storage;

namespace Chat.App.Services;

/// <summary>
/// 应用组合根：数据目录、SQLite、插件发现与加载。UI 只依赖这里暴露的仓储与 provider 列表。
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;

    private AppServices(string dataDirectory, ChatDatabase database, ProviderHost host,
        IReadOnlyList<LoadedPlugin> plugins, IReadOnlyList<PluginLoadFailure> failures)
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

        _http = new HttpClient(new HttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
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

    public static AppServices Create(string? dataDirectory = null)
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

        return new AppServices(data, database, host, result.Loaded, result.Failures);
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
