using System.Net;
using System.Text;
using Chat.Abstractions;

namespace Chat.Providers.DeepSeek.Tests;

/// <summary>捕获到的出站请求（含 DeepSeek 插件使用的头）。</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    string? ApiKeyHeader,
    string? AnthropicVersion,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string Accept,
    string? Body);

internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        _responder = responder;

    public List<CapturedRequest> Requests { get; } = [];

    public CapturedRequest LastRequest => Requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.TryGetValues("x-api-key", out var apiKeyValues);
        request.Headers.TryGetValues("anthropic-version", out var versionValues);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            apiKeyValues is null ? null : string.Join(",", apiKeyValues),
            versionValues is null ? null : string.Join(",", versionValues),
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.Accept.ToString(),
            body));
        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class StubProviderContext : IProviderContext
{
    public StubProviderContext(HttpMessageInvoker http, string? apiKey)
    {
        Http = http;
        if (apiKey is not null)
        {
            Secrets[DeepSeekProvider.ApiKeySecret] = apiKey;
        }
    }

    public Dictionary<string, string> SettingsMap { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public List<string> Logs { get; } = [];

    public string SessionId { get; init; } = "test-session";

    public string AppVersion => "0.1.0";

    public HttpMessageInvoker Http { get; }

    public string TempDirectory => Path.GetTempPath();

    public IReadOnlyDictionary<string, string> Settings => SettingsMap;

    public ValueTask<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);

    public void Log(ProviderLogLevel level, string message) => Logs.Add($"{level}: {message}");
}

internal static class Sse
{
    public static HttpResponseMessage Response(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "text/event-stream"),
    };

    public static HttpResponseMessage Json(string payload, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    public static string Frame(string json) => "data: " + json + "\n\n";
}

internal static class TestHarness
{
    public static ChatRequest Request(string model = "deepseek-flash") => new()
    {
        Model = model,
        Messages = [new ChatMessage(ChatRole.User, "你好")],
    };

    public static StubProviderContext Context(
        StubHttpHandler handler,
        string? apiKey = "test-key",
        params (string Key, string Value)[] settings)
    {
        var context = new StubProviderContext(new HttpMessageInvoker(handler, disposeHandler: false), apiKey);
        foreach (var (key, value) in settings)
        {
            context.SettingsMap[key] = value;
        }

        return context;
    }

    public static async Task<List<ChatEvent>> CollectAsync(
        StubHttpHandler handler,
        ChatRequest request,
        StubProviderContext? context = null)
    {
        context ??= Context(handler);
        var events = new List<ChatEvent>();
        await foreach (var evt in new DeepSeekProvider().StreamAsync(request, context))
        {
            events.Add(evt);
        }

        return events;
    }
}
