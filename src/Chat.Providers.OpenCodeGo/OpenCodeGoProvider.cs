using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chat.Abstractions;

namespace Chat.Providers.OpenCodeGo;

/// <summary>
/// OpenCode Go 网关 provider（OpenAI Chat Completions 兼容族）。
/// 网关约束：
/// 1) 每个请求带 <c>x-opencode-session</c>（= 宿主会话 id）与显式 <c>User-Agent</c>；默认
///    <c>LLMChat/&lt;AppVersion&gt;</c>，可用设置键 <see cref="UserAgentSetting"/> 覆盖（避免 .NET 默认 UA）；
/// 2) 响应中的 <c>usage.opencode</c> 计费块（list_cost / billing_source / tokens.*）原样以
///    <see cref="ChatEvent.RawEvent"/> 保留，并尽量折算为 <see cref="TokenUsage"/>；
/// 3) 5 小时 / 周 / 月 / 频率等限额错误映射为可读中文提示。
/// </summary>
public sealed class OpenCodeGoProvider : IChatProvider
{
    /// <summary>API Key 密钥键。</summary>
    public const string ApiKeySecret = "opencode.apiKey";

    /// <summary>覆盖网关基址。</summary>
    public const string BaseUrlSetting = "opencode.baseUrl";

    /// <summary>覆盖默认模型。</summary>
    public const string ModelSetting = "opencode.model";

    /// <summary>token 上限字段名：<c>max_tokens</c>（默认）/ <c>max_completion_tokens</c>。</summary>
    public const string MaxTokensFieldSetting = "opencode.maxTokensField";

    /// <summary>是否请求末帧用量（<c>stream_options.include_usage</c>），默认开启。</summary>
    public const string StreamUsageSetting = "opencode.streamUsage";

    /// <summary>覆盖默认 User-Agent（默认 <c>LLMChat/&lt;AppVersion&gt;</c>）。</summary>
    public const string UserAgentSetting = "opencode.userAgent";

    /// <summary>网关要求的会话头。</summary>
    public const string SessionHeader = "x-opencode-session";

    public const string DefaultBaseUrl = "https://opencode.ai/inference/go/openai/v1";
    public const string DefaultModel = "deepseek-v4-pro";

    private const string DefaultMaxTokensField = "max_tokens";
    private const string MaxCompletionTokensField = "max_completion_tokens";
    private const int DefaultMaxTokens = 8192;
    private const int ErrorBodyLimit = 800;

    /// <summary>网关不可达 / 无凭据时回退的常用模型。</summary>
    public static IReadOnlyList<string> BuiltInModels { get; } =
    [
        "deepseek-v4-pro",
        "glm-5.3",
        "kimi-k2",
        "qwen3-coder",
    ];

    public ProviderDescriptor Descriptor { get; } = new(
        Id: "chat.provider.opencodego",
        DisplayName: "OpenCode Go",
        Family: ProtocolFamily.OpenAiCompatible,
        Version: "1.0.0",
        Capabilities: ProviderCapabilities.Thinking
            | ProviderCapabilities.ToolCall
            | ProviderCapabilities.Usage
            | ProviderCapabilities.PromptCache,
        DefaultModel: DefaultModel,
        Description: "OpenCode Go 网关（OpenAI Chat Completions 兼容，需 x-opencode-session 头）。");

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        IProviderContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = await context.GetSecretAsync(ApiKeySecret, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return new ChatEvent.Failed("missing_api_key", "未配置 OpenCode Go API Key（opencode.apiKey）。");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            yield return new ChatEvent.Failed(
                "missing_session_id",
                "宿主未提供 SessionId，OpenCode Go 网关要求每个请求携带 x-opencode-session。");
            yield break;
        }

        var baseUrl = BaseUrl(context);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ChatEvent.Failed("invalid_base_url", $"opencode.baseUrl 必须是 http/https 绝对 URL：{baseUrl}");
            yield break;
        }

        var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        var model = ModelFor(context, request.Model);
        context.Log(ProviderLogLevel.Debug, $"POST {endpoint} model={model}");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddGatewayHeaders(httpRequest, context);
        httpRequest.Content = new StringContent(
            BuildBody(request, context.Settings, model).ToJsonString(),
            Encoding.UTF8,
            "application/json");

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
        catch (OperationCanceledException ex)
        {
            networkError = "请求超时：" + ex.Message;
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
                string? errorBody = null;
                Exception? bodyError = null;
                try
                {
                    errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    bodyError = ex;
                }

                if (bodyError is not null)
                {
                    yield return new ChatEvent.Failed("network_error", "读取错误响应失败：" + bodyError.Message, Retryable: true);
                    yield break;
                }

                yield return HttpFailure(response.StatusCode, errorBody ?? string.Empty);
                yield break;
            }

            // 部分网关以 HTTP 200 + application/json 返回错误体；只有 JSON 内容类型整包识别，
            // text/plain 等仍按 SSE 逐行解析，避免误伤用纯文本携带 data: 帧的网关。
            if (IsJsonResponse(response))
            {
                string? body = null;
                Exception? bodyError = null;
                try
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    bodyError = ex;
                }

                if (bodyError is not null)
                {
                    yield return new ChatEvent.Failed("network_error", "读取响应体失败：" + bodyError.Message, Retryable: true);
                    yield break;
                }

                if (TryReadErrorText(body!, out var errorCode, out var errorMessage))
                {
                    yield return FailureFor(errorCode, errorMessage, retryable: false);
                }
                else
                {
                    yield return new ChatEvent.Failed(
                        "invalid_response",
                        $"服务端返回非 SSE 响应（Content-Type: {response.Content.Headers.ContentType?.MediaType ?? "unknown"}）：{Truncate(body ?? string.Empty, 500)}");
                }

                yield break;
            }

            Stream? responseStream = null;
            Exception? streamError = null;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
            {
                streamError = ex;
            }

            if (streamError is not null)
            {
                yield return new ChatEvent.Failed("network_error", "读取响应流失败：" + streamError.Message, Retryable: true);
                yield break;
            }

            await using var stream = responseStream!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var pending = new Dictionary<int, ToolCallBuffer>();
            var stopReason = StopReason.EndTurn;
            var sawDone = false;
            var sawFinishReason = false;
            var warnedInvalidFrame = false;
            Exception? readError = null;

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    readError = ex;
                    break;
                }

                if (line is null)
                {
                    break;
                }

                // SSE 注释行（": keep-alive"）与空行直接跳过。
                if (line.Length == 0 || line[0] == ':')
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload.Length == 0)
                {
                    continue;
                }

                if (payload == "[DONE]")
                {
                    sawDone = true;
                    break;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(payload);
                }
                catch (JsonException)
                {
                    // 坏帧保持跳过（不影响整条流），但只提示一次，避免刷屏。
                    if (!warnedInvalidFrame)
                    {
                        warnedInvalidFrame = true;
                        context.Log(ProviderLogLevel.Warning, "响应流中出现了无法解析的 SSE 数据帧，已跳过（后续同类帧不再提示）。");
                    }

                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (TryReadError(root, out var frameCode, out var frameMessage))
                    {
                        yield return FailureFor(frameCode, frameMessage, retryable: false);
                        yield break;
                    }

                    var (events, reason) = ReadChoiceEvents(root, pending);
                    foreach (var evt in events)
                    {
                        yield return evt;
                    }

                    if (reason is { } mapped)
                    {
                        stopReason = mapped;
                        sawFinishReason = true;
                    }

                    foreach (var usageEvent in ReadUsageEvents(root))
                    {
                        yield return usageEvent;
                    }
                }
            }

            if (readError is not null)
            {
                yield return new ChatEvent.Failed("network_error", "读取响应流失败：" + readError.Message, Retryable: true);
                yield break;
            }

            // EOF 时既没有 finish_reason 也没有 [DONE] → 流被截断，不能发 Done。
            if (!sawDone && !sawFinishReason)
            {
                yield return new ChatEvent.Failed(
                    "stream_truncated",
                    "响应流在完成前中断（未收到 finish_reason 或 [DONE]）。",
                    Retryable: true);
                yield break;
            }

            var trailing = DrainToolCalls(pending);
            if (trailing.Count > 0 && stopReason == StopReason.EndTurn)
            {
                stopReason = StopReason.ToolUse;
            }

            foreach (var evt in trailing)
            {
                yield return evt;
            }

            yield return new ChatEvent.Done(stopReason);
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(
        IProviderContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            context.Log(ProviderLogLevel.Warning, "宿主未提供 SessionId，OpenCode Go 要求 x-opencode-session，回退内置模型列表。");
            return FallbackModels();
        }

        if (Setting(context.Settings, ModelSetting) is { Length: > 0 } configured)
        {
            return [new ModelInfo(configured, configured)];
        }

        var baseUrl = BaseUrl(context);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var modelsUri)
            || (modelsUri.Scheme != Uri.UriSchemeHttp && modelsUri.Scheme != Uri.UriSchemeHttps))
        {
            context.Log(ProviderLogLevel.Warning, $"opencode.baseUrl 必须是 http/https 绝对 URL，回退内置模型列表：{baseUrl}");
            return FallbackModels();
        }

        var key = await context.GetSecretAsync(ApiKeySecret, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            return FallbackModels();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            AddGatewayHeaders(request, context);
            using var response = await context.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                context.Log(ProviderLogLevel.Warning, $"OpenCode Go 模型清单请求失败：HTTP {(int)response.StatusCode}。");
                return FallbackModels();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var models = new List<ModelInfo>();
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                AppendModels(data, models);
            }

            if (models.Count == 0
                && document.RootElement.TryGetProperty("models", out var nested)
                && nested.ValueKind == JsonValueKind.Array)
            {
                AppendModels(nested, models);
            }

            return models.Count > 0 ? models : FallbackModels();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FallbackModels();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            context.Log(ProviderLogLevel.Warning, $"OpenCode Go 模型清单不可用，回退内置列表：{ex.Message}");
            return FallbackModels();
        }
    }

    private static void AppendModels(JsonElement array, List<ModelInfo> models)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString() is { Length: > 0 } id)
                {
                    models.Add(new ModelInfo(id, id));
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (FirstString(item, "id") is not { Length: > 0 } modelId)
            {
                continue;
            }

            var display = FirstString(item, "display_name") ?? FirstString(item, "name") ?? modelId;
            models.Add(new ModelInfo(modelId, display));
        }
    }

    private static string BaseUrl(IProviderContext context) =>
        Setting(context.Settings, BaseUrlSetting) ?? DefaultBaseUrl;

    private static string ModelFor(IProviderContext context, string? requested)
    {
        if (Setting(context.Settings, ModelSetting) is { Length: > 0 } configured)
        {
            return configured;
        }

        return string.IsNullOrWhiteSpace(requested) ? DefaultModel : requested.Trim();
    }

    private static string? Setting(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static IReadOnlyList<ModelInfo> FallbackModels() =>
        BuiltInModels.Select(id => new ModelInfo(id, id)).ToArray();

    private static void AddGatewayHeaders(HttpRequestMessage request, IProviderContext context)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent(context));
        AddSessionHeader(request, context);
    }

    /// <summary>显式 User-Agent：opencode.userAgent 覆盖优先，否则 LLMChat/&lt;AppVersion&gt;。</summary>
    private static string UserAgent(IProviderContext context)
    {
        if (Setting(context.Settings, UserAgentSetting) is { } overridden)
        {
            return overridden;
        }

        var version = context.AppVersion;
        return "LLMChat/" + (string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim());
    }

    private static void AddSessionHeader(HttpRequestMessage request, IProviderContext context)
    {
        var sessionId = context.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            context.Log(ProviderLogLevel.Warning, "宿主未提供 SessionId，OpenCode Go 网关可能拒绝该请求。");
            return;
        }

        request.Headers.TryAddWithoutValidation(SessionHeader, sessionId);
    }

    private static JsonObject BuildBody(ChatRequest request, IReadOnlyDictionary<string, string> settings, string model)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        foreach (var message in request.Messages)
        {
            AppendMessage(messages, message);
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            [MaxTokensField(settings)] = request.MaxTokens ?? DefaultMaxTokens,
        };

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = BuildTools(tools);
        }

        if (ShouldRequestUsage(settings))
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return body;
    }

    private static string MaxTokensField(IReadOnlyDictionary<string, string> settings) =>
        string.Equals(Setting(settings, MaxTokensFieldSetting), MaxCompletionTokensField, StringComparison.OrdinalIgnoreCase)
            ? MaxCompletionTokensField
            : DefaultMaxTokensField;

    private static bool ShouldRequestUsage(IReadOnlyDictionary<string, string> settings) =>
        !string.Equals(Setting(settings, StreamUsageSetting), "false", StringComparison.OrdinalIgnoreCase);

    private static JsonArray BuildTools(IReadOnlyList<ToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ParseOrEmptyObject(tool.ParametersJson),
                },
            });
        }

        return array;
    }

    private static void AppendMessage(JsonArray messages, ChatMessage message)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        List<ChatPart.ToolCall>? calls = null;
        List<ChatPart.ToolResult>? results = null;

        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case ChatPart.Text value when value.Value.Length > 0:
                    text.Append(value.Value);
                    break;
                case ChatPart.Thinking thinking:
                    reasoning.Append(thinking.Value);
                    break;
                case ChatPart.ToolCall call:
                    (calls ??= []).Add(call);
                    break;
                case ChatPart.ToolResult result:
                    (results ??= []).Add(result);
                    break;
            }
        }

        var role = message.Role switch
        {
            ChatRole.System => "system",
            ChatRole.Assistant => "assistant",
            _ => "user",
        };

        if (role == "assistant")
        {
            if (text.Length > 0 || reasoning.Length > 0 || calls is { Count: > 0 })
            {
                var assistant = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = text.Length > 0 ? text.ToString() : null,
                };

                // 思考内容回放：GLM/DeepSeek 等兼容端点要求把上一轮思考放在 reasoning_content。
                if (reasoning.Length > 0)
                {
                    assistant["reasoning_content"] = reasoning.ToString();
                }

                if (calls is { Count: > 0 })
                {
                    assistant["tool_calls"] = BuildToolCalls(calls);
                }

                messages.Add(assistant);
            }

            AppendToolResults(messages, results);
        }
        else
        {
            AppendToolResults(messages, results);
            if (text.Length > 0)
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = text.ToString() });
            }
        }
    }

    private static JsonArray BuildToolCalls(List<ChatPart.ToolCall> calls)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            array.Add(new JsonObject
            {
                ["id"] = call.CallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                },
            });
        }

        return array;
    }

    private static void AppendToolResults(JsonArray messages, List<ChatPart.ToolResult>? results)
    {
        if (results is not { Count: > 0 })
        {
            return;
        }

        foreach (var result in results)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = result.CallId,
                ["content"] = result.Content,
            });
        }
    }

    private static (List<ChatEvent> Events, StopReason? StopReason) ReadChoiceEvents(
        JsonElement root,
        Dictionary<int, ToolCallBuffer> pending)
    {
        var events = new List<ChatEvent>();
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return (events, null);
        }

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object)
        {
            return (events, null);
        }

        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            var reasoning = FirstString(delta, "reasoning_content")
                ?? FirstString(delta, "reasoning")
                ?? FirstString(delta, "reasoning_text");
            if (!string.IsNullOrEmpty(reasoning))
            {
                events.Add(new ChatEvent.ThinkingDelta(reasoning));
            }

            AppendContent(delta, events);
            AppendToolCalls(delta, pending, events);
        }

        StopReason? stop = null;
        if (choice.TryGetProperty("finish_reason", out var finish)
            && finish.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(finish.GetString()))
        {
            stop = MapFinishReason(finish.GetString()!);
            events.AddRange(DrainToolCalls(pending));
        }

        return (events, stop);
    }

    private static void AppendContent(JsonElement delta, List<ChatEvent> events)
    {
        if (!delta.TryGetProperty("content", out var content))
        {
            return;
        }

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                if (content.GetString() is { Length: > 0 } text)
                {
                    events.Add(new ChatEvent.TextDelta(text));
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && FirstString(item, "text") is { Length: > 0 } part)
                    {
                        events.Add(new ChatEvent.TextDelta(part));
                    }
                }

                break;
        }
    }

    private static void AppendToolCalls(JsonElement delta, Dictionary<int, ToolCallBuffer> pending, List<ChatEvent> events)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var position = 0;
        foreach (var item in toolCalls.EnumerateArray())
        {
            var index = position;
            position++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex))
            {
                index = parsedIndex;
            }

            if (!pending.TryGetValue(index, out var buffer))
            {
                buffer = new ToolCallBuffer();
                pending[index] = buffer;
            }

            // 有的网关第一帧只给 function、id 稍后才到：Start 一旦发出（可能用了按 index
            // 生成的临时 id），后续事件必须继续用同一个 id，否则宿主无法把增量拼回同一调用。
            if (!buffer.Started && FirstString(item, "id") is { Length: > 0 } id)
            {
                buffer.Id = id;
            }

            string? chunk = null;
            if (item.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                if (FirstString(function, "name") is { Length: > 0 } name)
                {
                    buffer.Name = name;
                }

                if (function.TryGetProperty("arguments", out var argumentsElement))
                {
                    chunk = argumentsElement.ValueKind == JsonValueKind.String
                        ? argumentsElement.GetString()
                        : argumentsElement.GetRawText();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        buffer.Arguments.Append(chunk);
                    }
                }
            }

            if (!buffer.Started && buffer.Name.Length > 0)
            {
                buffer.Started = true;
                if (buffer.Id.Length == 0)
                {
                    buffer.Id = $"call_{index}";
                }

                events.Add(new ChatEvent.ToolCallStart(buffer.Id, buffer.Name));
                if (buffer.Arguments.Length > 0)
                {
                    events.Add(new ChatEvent.ToolCallArgumentsDelta(buffer.Id, buffer.Arguments.ToString()));
                }
            }
            else if (buffer.Started && !string.IsNullOrEmpty(chunk))
            {
                events.Add(new ChatEvent.ToolCallArgumentsDelta(buffer.Id, chunk));
            }
        }
    }

    private static List<ChatEvent> DrainToolCalls(Dictionary<int, ToolCallBuffer> pending)
    {
        var events = new List<ChatEvent>(pending.Count);
        foreach (var (index, buffer) in pending.OrderBy(pair => pair.Key))
        {
            if (!buffer.Started)
            {
                buffer.Started = true;
                if (buffer.Id.Length == 0)
                {
                    buffer.Id = $"call_{index}";
                }

                events.Add(new ChatEvent.ToolCallStart(buffer.Id, buffer.Name));
                if (buffer.Arguments.Length > 0)
                {
                    events.Add(new ChatEvent.ToolCallArgumentsDelta(buffer.Id, buffer.Arguments.ToString()));
                }
            }

            events.Add(new ChatEvent.ToolCallCompleted(
                buffer.Id,
                buffer.Arguments.Length > 0 ? buffer.Arguments.ToString() : "{}"));
        }

        pending.Clear();
        return events;
    }

    /// <summary>
    /// 标准 usage 与 <c>usage.opencode</c> 计费块：计费块先原样保留为 RawEvent，
    /// 再把 tokens 折算进 UsageEvent（标准字段优先，缺失由计费块补齐）。
    /// </summary>
    private static IEnumerable<ChatEvent> ReadUsageEvents(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        JsonElement? openCode = null;
        if (usage.TryGetProperty("opencode", out var oc) && oc.ValueKind == JsonValueKind.Object)
        {
            openCode = oc;
            yield return new ChatEvent.RawEvent(oc.GetRawText());
        }

        var standard = ReadUsage(usage);
        var billing = openCode is { } ocValue ? ReadOpenCodeUsage(ocValue) : null;
        var merged = MergeUsage(standard, billing);
        if (merged is not null)
        {
            yield return new ChatEvent.UsageEvent(merged);
        }
    }

    private static TokenUsage? ReadUsage(JsonElement usage)
    {
        var input = ReadLong(usage, "prompt_tokens") ?? ReadLong(usage, "input_tokens");
        var output = ReadLong(usage, "completion_tokens") ?? ReadLong(usage, "output_tokens");
        var cached = ReadLong(usage, "cached_tokens")
            ?? ReadNestedLong(usage, "prompt_tokens_details", "cached_tokens")
            ?? ReadLong(usage, "prompt_cache_hit_tokens");
        var reasoning = ReadNestedLong(usage, "completion_tokens_details", "reasoning_tokens");

        if (input is null && output is null && cached is null && reasoning is null)
        {
            return null;
        }

        return new TokenUsage(input, output, cached, reasoning);
    }

    private static TokenUsage? ReadOpenCodeUsage(JsonElement openCode)
    {
        if (!TryGetObject(openCode, "tokens", out var tokens))
        {
            return null;
        }

        var input = ReadLong(tokens, "input") ?? ReadLong(tokens, "prompt_tokens");
        var output = ReadLong(tokens, "output") ?? ReadLong(tokens, "completion_tokens");
        var cached = ReadLong(tokens, "cached") ?? ReadLong(tokens, "cache_read_input_tokens");
        var reasoning = ReadLong(tokens, "reasoning") ?? ReadLong(tokens, "reasoning_tokens");

        if (input is null && output is null && cached is null && reasoning is null)
        {
            return null;
        }

        return new TokenUsage(input, output, cached, reasoning);
    }

    private static TokenUsage? MergeUsage(TokenUsage? standard, TokenUsage? billing)
    {
        if (standard is null)
        {
            return billing;
        }

        if (billing is null)
        {
            return standard;
        }

        return new TokenUsage(
            standard.InputTokens ?? billing.InputTokens,
            standard.OutputTokens ?? billing.OutputTokens,
            standard.CachedInputTokens ?? billing.CachedInputTokens,
            standard.ReasoningTokens ?? billing.ReasoningTokens);
    }

    private static long? ReadNestedLong(JsonElement element, string parent, string property) =>
        TryGetObject(element, parent, out var nested) ? ReadLong(nested, property) : null;

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool TryGetObject(JsonElement element, string property, out JsonElement value) =>
        element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? FirstString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    private static StopReason MapFinishReason(string reason) => reason switch
    {
        "length" => StopReason.MaxTokens,
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "content_filter" => StopReason.ContentFilter,
        "stop_sequence" => StopReason.StopSequence,
        _ => StopReason.EndTurn,
    };

    private static bool IsJsonResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static ChatEvent.Failed HttpFailure(HttpStatusCode status, string body)
    {
        var statusCode = (int)status;
        var retryable = statusCode is >= 500 or 429;
        if (TryReadErrorText(body, out var code, out var message))
        {
            return FailureFor(code, message, retryable);
        }

        return FailureFor($"http_{statusCode}", $"HTTP {statusCode}: {Truncate(body, ErrorBodyLimit)}", retryable);
    }

    /// <summary>限额错误统一为可读中文提示（code=limit_exceeded，可重试）。</summary>
    private static ChatEvent.Failed FailureFor(string code, string message, bool retryable)
    {
        var limit = DescribeLimit(message);
        return limit is null
            ? new ChatEvent.Failed(code, message, retryable)
            : new ChatEvent.Failed("limit_exceeded", $"{limit}（OpenCode Go）：{message}", Retryable: true);
    }

    private static string? DescribeLimit(string message)
    {
        if (message.Length == 0)
        {
            return null;
        }

        if (ContainsAny(message, "5-hour", "5 hour", "five hour", "5h", "5 小时", "五小时"))
        {
            return "5 小时限额";
        }

        if (ContainsAny(message, "weekly", "week", "周"))
        {
            return "周限额";
        }

        if (ContainsAny(message, "monthly", "month", "月"))
        {
            return "月限额";
        }

        if (ContainsAny(message, "rate limit", "too many requests", "频率", "限流"))
        {
            return "请求频率限制";
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadErrorText(string body, out string code, out string message)
    {
        code = "provider_error";
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && TryReadError(document.RootElement, out code, out message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadError(JsonElement root, out string code, out string message)
    {
        code = "provider_error";
        message = string.Empty;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryGetObject(root, "error", out var error))
        {
            code = FirstString(error, "code") ?? FirstString(error, "type") ?? "provider_error";
            message = FirstString(error, "message") ?? "OpenCode Go 返回错误。";
            return true;
        }

        if (root.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            code = FirstString(root, "code") ?? "provider_error";
            message = FirstString(root, "message") ?? "OpenCode Go 返回错误。";
            return true;
        }

        if (TryGetObject(root, "base_resp", out var baseResp))
        {
            code = FirstString(baseResp, "status_code") ?? FirstString(baseResp, "status_msg") ?? "provider_error";
            message = FirstString(baseResp, "status_msg") ?? "OpenCode Go 返回错误。";
            return true;
        }

        return false;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed class ToolCallBuffer
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();

        public bool Started { get; set; }
    }
}
