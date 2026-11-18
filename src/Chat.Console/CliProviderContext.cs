using System.Net.Http.Headers;
using Chat.Abstractions;

namespace Chat.Console;

/// <summary>CLI 的最小 IProviderContext：密钥来自 --api-key / --set / 环境变量，日志走 stderr。</summary>
internal sealed class CliProviderContext : IProviderContext, IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly bool _verbose;

    public CliProviderContext(
        string sessionId,
        string appVersion,
        string? apiKey,
        IReadOnlyDictionary<string, string> settings,
        string tempDirectory,
        bool verbose)
    {
        SessionId = sessionId;
        AppVersion = appVersion;
        Settings = settings;
        TempDirectory = tempDirectory;
        _apiKey = apiKey;
        _verbose = verbose;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("llmchat-cli", appVersion));
    }

    public string SessionId { get; }

    public string AppVersion { get; }

    public HttpMessageInvoker Http => _http;

    public string TempDirectory { get; }

    public IReadOnlyDictionary<string, string> Settings { get; }

    public ValueTask<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!string.IsNullOrWhiteSpace(_apiKey))
            return ValueTask.FromResult<string?>(_apiKey);

        if (Settings.TryGetValue(key, out var fromSettings) && !string.IsNullOrWhiteSpace(fromSettings))
            return ValueTask.FromResult<string?>(fromSettings);

        var envName = key switch
        {
            "deepseek.apiKey" => "DEEPSEEK_API_KEY",
            "openai.apiKey" => "OPENAI_API_KEY",
            "opencode.apiKey" => "OPENCODE_API_KEY",
            _ => null,
        };
        if (envName is not null)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value))
                return ValueTask.FromResult<string?>(value);
        }

        return ValueTask.FromResult<string?>(null);
    }

    public void Log(ProviderLogLevel level, string message)
    {
        if (level == ProviderLogLevel.Debug && !_verbose) return;
        System.Console.Error.WriteLine($"[{level.ToString().ToLowerInvariant()}] {message}");
    }

    public void Dispose() => _http.Dispose();
}
