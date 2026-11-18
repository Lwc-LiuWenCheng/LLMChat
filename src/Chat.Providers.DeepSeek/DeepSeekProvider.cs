using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chat.Abstractions;

namespace Chat.Providers.DeepSeek;

/// <summary>
/// DeepSeek 官方 Anthropic Messages 兼容端点（<c>/anthropic/v1/messages</c>）。
/// 之所以走 Anthropic 族而不是 OpenAI 族：官方客户端用的就是它，思考片段与
/// 加密签名（thinking signature）只在原厂通道能原样回放。
/// </summary>
public sealed class DeepSeekProvider : IChatProvider
{
    public const string ApiKeySecret = "deepseek.apiKey";
    public const string BaseUrlSetting = "deepseek.baseUrl";
    private const string DefaultBaseUrl = "https://api.deepseek.com/anthropic/v1";

    public ProviderDescriptor Descriptor { get; } = new(
        Id: "chat.provider.deepseek",
        DisplayName: "DeepSeek",
        Family: ProtocolFamily.AnthropicMessages,
        Version: "1.0.0",
        Capabilities: ProviderCapabilities.Thinking | ProviderCapabilities.Usage | ProviderCapabilities.PromptCache,
        DefaultModel: "deepseek-flash",
        Description: "DeepSeek 官方 API（Anthropic Messages 兼容）。");

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(IProviderContext context, CancellationToken cancellationToken = default)
    {
        var baseUrl = BaseUrl(context);
        var key = await context.GetSecretAsync(ApiKeySecret, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
            return [new ModelInfo("deepseek-flash", "DeepSeek Flash", 128_000)];

        // 官方 /anthropic/v1 没有 models 列表；走 OpenAI 兼容的 /models（同域）。
        var origin = new Uri(baseUrl);
        var modelsUrl = new Uri(origin, "/models");
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await context.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [new ModelInfo("deepseek-flash", "DeepSeek Flash", 128_000)];

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var list = new List<ModelInfo>();
        if (json?["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                var id = item?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) list.Add(new ModelInfo(id, id));
            }
        }

        return list.Count > 0 ? list : [new ModelInfo("deepseek-flash", "DeepSeek Flash", 128_000)];
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        IProviderContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = await context.GetSecretAsync(ApiKeySecret, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
        {
            yield return new ChatEvent.Failed("missing_api_key", "未配置 DeepSeek API Key。");
            yield break;
        }

        // 必须用字符串拼接而不是 new Uri(base, "/messages")：相对 URI 以前导 '/' 开头会
        // 重置到 host 根，默认 baseUrl（.../anthropic/v1）会变成 https://api.deepseek.com/messages。
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl(context) + "/messages");
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", key);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        string? networkError = null;
        try
        {
            response = await context.Http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            networkError = ex.Message;
        }

        if (response is null)
        {
            yield return new ChatEvent.Failed("network_error", networkError ?? "网络请求失败。", Retryable: true);
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                yield return new ChatEvent.Failed(
                    "http_" + (int)response.StatusCode,
                    $"HTTP {(int)response.StatusCode}: {Truncate(body, 800)}",
                    Retryable: (int)response.StatusCode is >= 500 or 429);
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var stopReason = StopReason.EndTurn;
            var finished = false;

            while (!finished)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    if (!finished) yield return new ChatEvent.Done(stopReason);
                    yield break;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]") continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(payload);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    switch (type)
                    {
                        case "content_block_delta":
                        {
                            if (!root.TryGetProperty("delta", out var delta)) break;
                            var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                            if (deltaType == "text_delta" && delta.TryGetProperty("text", out var text))
                                yield return new ChatEvent.TextDelta(text.GetString() ?? "");
                            else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var think))
                                yield return new ChatEvent.ThinkingDelta(think.GetString() ?? "");
                            break;
                        }

                        case "message_delta":
                        {
                            if (root.TryGetProperty("delta", out var delta) &&
                                delta.TryGetProperty("stop_reason", out var sr) &&
                                sr.ValueKind == JsonValueKind.String)
                            {
                                stopReason = MapStopReason(sr.GetString());
                            }

                            if (root.TryGetProperty("usage", out var usage))
                                yield return new ChatEvent.UsageEvent(ReadUsage(usage));
                            break;
                        }

                        case "message_stop":
                            finished = true;
                            yield return new ChatEvent.Done(stopReason);
                            break;

                        case "error":
                            yield return new ChatEvent.Failed(
                                "provider_error",
                                root.TryGetProperty("error", out var err) ? Truncate(err.ToString(), 500) : payload);
                            yield break;
                    }
                }
            }
        }
    }

    private static string BaseUrl(IProviderContext context) =>
        context.Settings.TryGetValue(BaseUrlSetting, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.TrimEnd('/')
            : DefaultBaseUrl;

    private static string BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var role = message.Role switch
            {
                ChatRole.Assistant => "assistant",
                _ => "user",
            };
            var content = new JsonArray();
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case ChatPart.Text text:
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Value });
                        break;
                    case ChatPart.ToolCall call:
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.CallId,
                            ["name"] = call.Name,
                            ["input"] = ParseOrEmptyObject(call.ArgumentsJson),
                        });
                        break;
                    case ChatPart.ToolResult result:
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.CallId,
                            ["content"] = result.Content,
                            ["is_error"] = result.IsError,
                        });
                        break;
                }
            }

            if (content.Count == 0) continue;
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? 8192,
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (request.System is { Length: > 0 } system) body["system"] = system;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        if (request.Extra is { } extra && extra.TryGetValue("thinking", out var thinking) && thinking == "enabled")
            body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = 4096 };

        return body.ToJsonString();
    }

    private static JsonNode ParseOrEmptyObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static TokenUsage ReadUsage(JsonElement usage) => new(
        InputTokens: usage.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : null,
        OutputTokens: usage.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : null,
        CachedInputTokens: usage.TryGetProperty("cache_read_input_tokens", out var c) ? c.GetInt64() : null);

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" => StopReason.EndTurn,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "tool_use" => StopReason.ToolUse,
        "refusal" => StopReason.ContentFilter,
        _ => StopReason.EndTurn,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
