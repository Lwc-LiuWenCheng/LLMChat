using System.Net;
using System.Text;
using Chat.Abstractions;

namespace Chat.Providers.OpenAiCompatible.Tests;

/// <summary>捕获到的出站请求（含请求体文本）。</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
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
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.Accept.ToString(),
            body));
        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class StubProviderContext : IProviderContext
{
    public StubProviderContext(HttpMessageInvoker http, string? apiKey = "test-key")
    {
        Http = http;
        if (apiKey is not null) Secrets[OpenAiCompatibleProvider.ApiKeySecret] = apiKey;
    }

    public Dictionary<string, string> SettingsMap { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public List<string> Logs { get; } = [];

    public string SessionId => "test-session";

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

    public static string Frame(string json) => "data: " + json + "\n\n";
}

internal static class TestHarness
{
    public static ChatRequest Request(string model = "glm-4.6") => new()
    {
        Model = model,
        Messages = [new ChatMessage(ChatRole.User, "你好")],
    };

    public static StubProviderContext Context(StubHttpHandler handler, params (string Key, string Value)[] settings)
    {
        var context = new StubProviderContext(new HttpMessageInvoker(handler, disposeHandler: false));
        foreach (var (key, value) in settings) context.SettingsMap[key] = value;
        return context;
    }

    public static async Task<List<ChatEvent>> CollectAsync(
        StubHttpHandler handler,
        ChatRequest request,
        StubProviderContext? context = null)
    {
        context ??= Context(handler);
        var events = new List<ChatEvent>();
        await foreach (var evt in new OpenAiCompatibleProvider().StreamAsync(request, context))
            events.Add(evt);
        return events;
    }
}

/// <summary>先吐出固定 SSE 前缀，随后一直阻塞直到取消——用于验证取消能穿透解析循环。</summary>
internal sealed class BlockingSseStream : Stream
{
    private readonly byte[] _prefix;
    private int _offset;

    public BlockingSseStream(string prefix) => _prefix = Encoding.UTF8.GetBytes(prefix);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>先吐出固定 SSE 前缀，随后抛出 <see cref="IOException"/>——模拟传输中断。</summary>
internal sealed class FailingSseStream : Stream
{
    private readonly byte[] _prefix;
    private int _offset;

    public FailingSseStream(string prefix) => _prefix = Encoding.UTF8.GetBytes(prefix);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }

        return ValueTask.FromException<int>(new IOException("连接被重置（模拟流中断）"));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
