using System.Net;
using System.Text;
using Chat.Gateway;
using Chat.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Sdk;

namespace Chat.Gateway.Tests;

public sealed class GatewayFixture : IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "llmchat-gateway-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingHandler _handler = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public string DataDirectory => _temp;
    public RecordingHandler Handler => _handler;
    public HttpClient Client => _client ?? throw new InvalidOperationException("fixture 尚未启动");
    public void SetApiKey(string key)
    {
        var database = new ChatDatabase(StoragePaths.DatabaseFile(_temp));
        database.Initialize();
        new SettingsRepository(database).Set("deepseek.apiKey", key);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        var config = new GatewayConfig(_temp, "http://127.0.0.1:0", Path.Combine(_temp, "missing-dist"), _handler);
        _app = await GatewayHost.StartAsync(config);
        _client = new HttpClient { BaseAddress = TestAddress(_app) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await GatewayHost.StopAsync(_app);
        TryDelete(_temp);
    }

    internal static Uri TestAddress(WebApplication app)
    {
        var address = app.Urls.FirstOrDefault() ?? throw new InvalidOperationException("网关未监听任何地址");
        return new Uri(address);
    }

    internal static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class RecordingHandler : HttpMessageHandler
{
    public sealed record Call(string Method, Uri Uri, Dictionary<string, string[]> Headers, byte[] Body);
    private readonly List<Call> _calls = [];
    public IReadOnlyList<Call> Calls => _calls;
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
            foreach (var header in request.Content.Headers)
                headers[header.Key] = header.Value.ToArray();
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_calls) _calls.Add(new Call(request.Method.Method, request.RequestUri!, headers, body));
        if (Responder is not null) return Responder(request);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
    }

    public string Header(Call call, string name) => call.Headers.TryGetValue(name, out var values) ? string.Join(",", values) : "";
}
