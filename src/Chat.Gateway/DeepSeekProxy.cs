using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Chat.Storage;

namespace Chat.Gateway;

/// <summary>
/// 把前端发来的 Anthropic 兼容请求原样转发 DeepSeek，仅替换鉴权为网关保管的服务端 Key。
/// 响应体（SSE 或 JSON）逐字节复制，避免改写帧顺序或内容。
/// </summary>
public sealed class DeepSeekProxy : IAsyncDisposable
{
    private const string ApiKeySetting = "deepseek.apiKey";
    private const string BetaHeader = "files-api-2025-04-14";
    private readonly GatewayStore _store;
    private readonly ILogger _logger;
    private readonly HttpClient _client;
    private bool _ownsClient;

    public DeepSeekProxy(GatewayStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        }, disposeHandler: true);
        _ownsClient = true;
    }

    public DeepSeekProxy(GatewayStore store, ILogger logger, HttpMessageHandler handler)
    {
        _store = store;
        _logger = logger;
        _client = new HttpClient(handler, disposeHandler: false);
    }

    public async Task<IResult> ForwardMessagesAsync(HttpContext context)
    {
        try
        {
            var request = await BuildRequestAsync(context, "/anthropic/v1/messages", context.RequestAborted).ConfigureAwait(false);
            return await SendAsync(request, context, context.RequestAborted).ConfigureAwait(false);
        }
        catch (GatewayBadRequestException error)
        {
            return GatewayErrors.MissingKey(error.Message);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "转发 DeepSeek messages 失败");
            return GatewayErrors.Error("upstream_error", $"转发请求失败：{error.Message}", StatusCodes.Status502BadGateway);
        }
    }

    public async Task<IResult> ForwardModelsAsync(HttpContext context)
    {
        try
        {
            var path = context.Request.Path.StartsWithSegments("/anthropic")
                ? "/anthropic/v1/models"
                : "/v1/models";
            var request = await BuildRequestAsync(context, path, context.RequestAborted).ConfigureAwait(false);
            return await SendAsync(request, context, context.RequestAborted).ConfigureAwait(false);
        }
        catch (GatewayBadRequestException error)
        {
            return GatewayErrors.MissingKey(error.Message);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "转发 DeepSeek models 失败");
            return GatewayErrors.Error("upstream_error", $"转发请求失败：{error.Message}", StatusCodes.Status502BadGateway);
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        var key = ReadApiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new GatewayBadRequestException("请先在设置里保存 DeepSeek API Key。");

        var target = new Uri(new Uri(GatewayOptions.DeepSeekBaseUrl), path);
        var request = new HttpRequestMessage(context.Request.Method == "GET" ? HttpMethod.Get : HttpMethod.Post, target);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
        request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);

        foreach (var header in context.Request.Headers)
        {
            var name = header.Key;
            if (name.StartsWith("x-api-key", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(name, header.Value.ToArray());
        }

        if (!HttpMethod.Get.Equals(request.Method))
        {
            var bytes = await ReadBodyAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
            request.Content = new ByteArrayContent(bytes);
            if (context.Request.ContentType is { Length: > 0 } contentType)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        return request;
    }

    private async Task<IResult> SendAsync(HttpRequestMessage request, HttpContext context, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = contentType;
        if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
            context.Response.ContentType += "; charset=" + charset.Trim('"');
        if (response.Headers.CacheControl is not null)
            context.Response.Headers.CacheControl = response.Headers.CacheControl.ToString();
        await stream.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }

    private string? ReadApiKey()
    {
        var database = new ChatDatabase(StoragePaths.DatabaseFile(_store.DataDirectory));
        return new SettingsRepository(database).Get(ApiKeySetting);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<byte[]> ReadBodyAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}