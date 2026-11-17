using System.Collections.Concurrent;
using System.Diagnostics;
using Chat.Abstractions;
using Chat.Storage;

namespace Chat.App.Services;

/// <summary>
/// 插件运行上下文：会话 id、共享 HTTP 管道、密钥与设置读取、日志。
/// 每会话一个实例（SessionId 用于 OpenCode Go 之类的路由亲和标识）。
/// </summary>
public sealed class ProviderContext : IProviderContext
{
    private static readonly ConcurrentDictionary<string, byte> LoggedKeys = new();

    private readonly SettingsRepository _settings;
    private readonly string _tempDirectory;

    public ProviderContext(string sessionId, SettingsRepository settings, HttpMessageInvoker http, string tempDirectory)
    {
        SessionId = sessionId;
        _settings = settings;
        Http = http;
        _tempDirectory = tempDirectory;
        Settings = settings.GetAll();
    }

    public string SessionId { get; }

    public string AppVersion => AppInfo.Version;

    public HttpMessageInvoker Http { get; }

    public string TempDirectory
    {
        get
        {
            Directory.CreateDirectory(_tempDirectory);
            return _tempDirectory;
        }
    }

    public IReadOnlyDictionary<string, string> Settings { get; }

    public ValueTask<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = _settings.Get(key);
        if (!string.IsNullOrWhiteSpace(value)) return ValueTask.FromResult<string?>(value);

        // 便于开发与 CI：环境变量兜底（deepseek.apiKey → DEEPSEEK_API_KEY）。
        var envName = key.Replace('.', '_').ToUpperInvariant();
        var fromEnv = Environment.GetEnvironmentVariable(envName);
        return ValueTask.FromResult<string?>(string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv);
    }

    public void Log(ProviderLogLevel level, string message)
    {
        Debug.WriteLine($"[{SessionId}][{level}] {message}");
        if (!LoggedKeys.TryAdd($"{SessionId}:{level}:{message}", 0)) return;

        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_tempDirectory)!, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "provider.log"),
                $"{DateTimeOffset.Now:O} [{SessionId}] [{level}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // 日志失败不影响主流程
        }
    }
}
