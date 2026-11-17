using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chat.Abstractions;

namespace Chat.Providers.OpenAiCompatible;

/// <summary>内置兼容端点预设（默认基址 + 默认模型）。</summary>
/// <param name="Id">设置 <c>openai.preset</c> 的取值。</param>
/// <param name="DisplayName">界面显示名。</param>
/// <param name="BaseUrl">默认基址（不含 <c>/chat/completions</c>）。</param>
/// <param name="DefaultModel">默认模型 id。</param>
public sealed record ProviderPreset(string Id, string DisplayName, string BaseUrl, string DefaultModel);

/// <summary>
/// OpenAI Chat Completions 兼容 provider：内置 GLM（智谱）/ MiniMax 预设，也可通过
/// <c>openai.baseUrl</c> 指向任意兼容端点。请求固定为 <c>POST {baseUrl}/chat/completions</c>，
/// 响应按 SSE 增量解析（文本、思考、工具调用、用量）。
/// </summary>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    /// <summary>API Key 密钥键。</summary>
    public const string ApiKeySecret = "openai.apiKey";

    /// <summary>覆盖预设基址。</summary>
    public const string BaseUrlSetting = "openai.baseUrl";

    /// <summary>预设选择：<c>glm</c>（默认）/ <c>minimax</c>。</summary>
    public const string PresetSetting = "openai.preset";

    /// <summary>覆盖默认模型。</summary>
    public const string ModelSetting = "openai.model";

    /// <summary>token 上限字段名：<c>max_tokens</c>（默认）/ <c>max_completion_tokens</c>。</summary>
    public const string MaxTokensFieldSetting = "openai.maxTokensField";

    /// <summary>是否请求末帧用量（<c>stream_options.include_usage</c>），默认开启；不兼容的端点设为 <c>false</c>。</summary>
    public const string StreamUsageSetting = "openai.streamUsage";

    private const string DefaultMaxTokensField = "max_tokens";
    private const string MaxCompletionTokensField = "max_completion_tokens";
    private const int DefaultMaxTokens = 8192;

    /// <summary>内置预设。第一个同时用于 <see cref="ProviderDescriptor.DefaultModel"/>。</summary>
    public static IReadOnlyList<ProviderPreset> Presets { get; } =
    [
        new("glm", "GLM（智谱）", "https://open.bigmodel.cn/api/paas/v4", "glm-4.6"),
        new("minimax", "MiniMax", "https://api.minimax.chat/v1", "MiniMax-M2"),
    ];

    public ProviderDescriptor Descriptor { get; } = new(
        Id: "chat.provider.openai",
        DisplayName: "OpenAI 兼容（GLM / MiniMax）",
        Family: ProtocolFamily.OpenAiCompatible,
        Version: "1.0.0",
        Capabilities: ProviderCapabilities.Thinking | ProviderCapabilities.ToolCall | ProviderCapabilities.Usage | ProviderCapabilities.PromptCache,
        DefaultModel: "glm-4.6",
        Description: "任意 OpenAI Chat Completions 兼容端点（内置 GLM / MiniMax 预设）。");

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(IProviderContext context, CancellationToken cancellationToken = default)
    {
        var preset = Preset(context);
        if (Setting(context.Settings, ModelSetting) is { Length: > 0 } configured)
            return [new ModelInfo(configured, configured)];

        var baseUrl = BaseUrl(context);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var modelsUri) || !IsHttpScheme(modelsUri))
        {
            context.Log(ProviderLogLevel.Warning, $"openai.baseUrl 必须是 http/https 绝对 URL：{baseUrl}");
            return FallbackModels(preset);
        }

        var key = await context.GetSecretAsync(ApiKeySecret, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key)) return FallbackModels(preset);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await context.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return FallbackModels(preset);

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

            return list.Count > 0 ? list : FallbackModels(preset);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException or InvalidOperationException)
        {
            context.Log(ProviderLogLevel.Warning, "读取模型列表失败：" + ex.Message);
            return FallbackModels(preset);
        }
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        IProviderContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = await context.GetSecretAsync(ApiKeySecret, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
        {
            yield return new ChatEvent.Failed("missing_api_key", "未配置 OpenAI 兼容 API Key（openai.apiKey）。");
            yield break;
        }

        var baseUrl = BaseUrl(context);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !IsHttpScheme(baseUri))
        {
            yield return new ChatEvent.Failed("invalid_base_url", $"openai.baseUrl 必须是 http/https 绝对 URL：{baseUrl}");
            yield break;
        }

        var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        var model = ModelFor(context, request.Model);
        context.Log(ProviderLogLevel.Debug, $"POST {endpoint} model={model}");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
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
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                yield return new ChatEvent.Failed(
                    "http_" + (int)response.StatusCode,
                    $"HTTP {(int)response.StatusCode}: {Truncate(errorBody, 800)}",
                    Retryable: (int)response.StatusCode is >= 500 or 429);
                yield break;
            }

            // 部分兼容端点/网关会以 HTTP 200 + application/json 返回错误体（而非 SSE），
            // 这种情况必须先按错误解析，否则会被误判成"成功但空内容"。
            // 只有 JSON 响应才走整包解析；text/plain 等仍按 SSE 逐行解析，兼容以纯文本
            // Content-Type 输出 data: 帧的网关。
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

                if (TryReadErrorFromBody(body!, out var bodyFailure))
                {
                    yield return bodyFailure;
                    yield break;
                }

                yield return new ChatEvent.Failed(
                    "invalid_response",
                    $"服务端返回非 SSE 响应（Content-Type: {response.Content.Headers.ContentType?.MediaType ?? "unknown"}）：{Truncate(body ?? string.Empty, 500)}");
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

                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line[5..].Trim();
                if (payload.Length == 0) continue;
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
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    if (TryReadError(root, payload, out var failure))
                    {
                        yield return failure;
                        yield break;
                    }

                    var (events, reason) = ReadChoiceEvents(root, pending);
                    foreach (var evt in events) yield return evt;
                    if (reason is { } mapped)
                    {
                        stopReason = mapped;
                        sawFinishReason = true;
                    }

                    if (root.TryGetProperty("usage", out var usage) &&
                        usage.ValueKind == JsonValueKind.Object &&
                        ReadUsage(usage) is { } tokenUsage)
                    {
                        yield return new ChatEvent.UsageEvent(tokenUsage);
                    }
                }
            }

            if (readError is not null)
            {
                yield return new ChatEvent.Failed("network_error", "读取响应流失败：" + readError.Message, Retryable: true);
                yield break;
            }

            // EOF 时既没有 finish_reason 也没有 [DONE] → 流被截断（重试可能成功），
            // 不能发 Done，否则上层会把"半截内容"当成正常完成。
            if (!sawDone && !sawFinishReason)
            {
                yield return new ChatEvent.Failed(
                    "stream_truncated",
                    "响应流在完成前中断（未收到 finish_reason 或 [DONE]）。",
                    Retryable: true);
                yield break;
            }

            var trailing = DrainToolCalls(pending);
            if (trailing.Count > 0 && stopReason == StopReason.EndTurn) stopReason = StopReason.ToolUse;
            foreach (var evt in trailing) yield return evt;
            yield return new ChatEvent.Done(stopReason);
        }
    }

    private static string BaseUrl(IProviderContext context) =>
        Setting(context.Settings, BaseUrlSetting)?.TrimEnd('/') ?? Preset(context).BaseUrl;

    // 只接受 http/https：file://、ftp:// 之类会被 HttpClient 以奇怪的方式拒绝，
    // 提前挡掉能给用户一个明确的 invalid_base_url。
    private static bool IsHttpScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static string ModelFor(IProviderContext context, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        return Setting(context.Settings, ModelSetting) ?? Preset(context).DefaultModel;
    }

    private static ProviderPreset Preset(IProviderContext context) =>
        Setting(context.Settings, PresetSetting) is { Length: > 0 } id
            ? Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Presets[0]
            : Presets[0];

    private static string? Setting(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static IReadOnlyList<ModelInfo> FallbackModels(ProviderPreset preset) =>
        [new ModelInfo(preset.DefaultModel, preset.DefaultModel)];

    private static JsonObject BuildBody(ChatRequest request, IReadOnlyDictionary<string, string> settings, string model)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });

        foreach (var message in request.Messages)
            AppendMessage(messages, message);

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            [MaxTokensField(settings)] = request.MaxTokens ?? DefaultMaxTokens,
        };

        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        if (request.Tools is { Count: > 0 } tools) body["tools"] = BuildTools(tools);
        if (ShouldRequestUsage(settings)) body["stream_options"] = new JsonObject { ["include_usage"] = true };

        return body;
    }

    private static string MaxTokensField(IReadOnlyDictionary<string, string> settings) =>
        string.Equals(Setting(settings, MaxTokensFieldSetting), MaxCompletionTokensField, StringComparison.OrdinalIgnoreCase)
            ? MaxCompletionTokensField
            : DefaultMaxTokensField;

    private static bool ShouldRequestUsage(IReadOnlyDictionary<string, string> settings) =>
        !string.Equals(Setting(settings, StreamUsageSetting), "false", StringComparison.OrdinalIgnoreCase);

    private static void AppendMessage(JsonArray messages, ChatMessage message)
    {
        var text = new StringBuilder();
        List<ChatPart.ToolCall>? calls = null;
        List<ChatPart.ToolResult>? results = null;

        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case ChatPart.Text value when value.Value.Length > 0:
                    text.Append(value.Value);
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
            if (text.Length > 0 || calls is { Count: > 0 })
            {
                var assistant = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = text.Length > 0 ? text.ToString() : null,
                };
                if (calls is { Count: > 0 }) assistant["tool_calls"] = BuildToolCalls(calls);
                messages.Add(assistant);
            }

            AppendToolResults(messages, results);
        }
        else
        {
            AppendToolResults(messages, results);
            if (text.Length > 0)
                messages.Add(new JsonObject { ["role"] = role, ["content"] = text.ToString() });
        }
    }

    private static void AppendToolResults(JsonArray messages, List<ChatPart.ToolResult>? results)
    {
        if (results is not { Count: > 0 }) return;
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

    private static (List<ChatEvent> Events, StopReason? StopReason) ReadChoiceEvents(
        JsonElement root,
        Dictionary<int, ToolCallBuffer> pending)
    {
        var events = new List<ChatEvent>();
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return (events, null);
        }

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object) return (events, null);

        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            // 各厂商对思考片段的字段名不一致：reasoning_content（GLM/DeepSeek）、
            // reasoning（部分兼容网关）、reasoning_text（旧版）。
            var reasoning = FirstString(delta, "reasoning_content")
                ?? FirstString(delta, "reasoning")
                ?? FirstString(delta, "reasoning_text");
            if (!string.IsNullOrEmpty(reasoning)) events.Add(new ChatEvent.ThinkingDelta(reasoning));

            AppendContent(delta, events);
            AppendToolCalls(delta, pending, events);
        }

        StopReason? stop = null;
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
        {
            var reason = finish.GetString();
            if (!string.IsNullOrEmpty(reason))
            {
                stop = MapFinishReason(reason);
                events.AddRange(DrainToolCalls(pending));
            }
        }

        return (events, stop);
    }

    private static void AppendContent(JsonElement delta, List<ChatEvent> events)
    {
        if (!delta.TryGetProperty("content", out var content)) return;

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                if (content.GetString() is { Length: > 0 } text) events.Add(new ChatEvent.TextDelta(text));
                break;
            case JsonValueKind.Array:
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("text", out var textElement) &&
                        textElement.GetString() is { Length: > 0 } part)
                    {
                        events.Add(new ChatEvent.TextDelta(part));
                    }
                }

                break;
        }
    }

    private static void AppendToolCalls(JsonElement delta, Dictionary<int, ToolCallBuffer> pending, List<ChatEvent> events)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array) return;

        var position = 0;
        foreach (var item in toolCalls.EnumerateArray())
        {
            var index = position;
            position++;
            if (item.ValueKind != JsonValueKind.Object) continue;

            if (item.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex))
                index = parsedIndex;

            if (!pending.TryGetValue(index, out var buffer))
            {
                buffer = new ToolCallBuffer();
                pending[index] = buffer;
            }

            // 有的网关第一帧只给 function、id 稍后才到：Start 一旦发出（可能用了按 index
            // 生成的临时 id），后续事件必须继续用同一个 id，否则宿主无法把增量拼回同一调用。
            if (!buffer.Started &&
                item.TryGetProperty("id", out var idElement) &&
                idElement.GetString() is { Length: > 0 } id)
            {
                buffer.Id = id;
            }

            string? chunk = null;
            if (item.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                if (function.TryGetProperty("name", out var nameElement) && nameElement.GetString() is { Length: > 0 } name)
                    buffer.Name = name;

                if (function.TryGetProperty("arguments", out var argumentsElement))
                {
                    chunk = argumentsElement.ValueKind == JsonValueKind.String
                        ? argumentsElement.GetString()
                        : argumentsElement.GetRawText();
                    if (!string.IsNullOrEmpty(chunk)) buffer.Arguments.Append(chunk);
                }
            }

            if (!buffer.Started && buffer.Name.Length > 0)
            {
                buffer.Started = true;
                if (buffer.Id.Length == 0) buffer.Id = $"call_{index}";
                events.Add(new ChatEvent.ToolCallStart(buffer.Id, buffer.Name));
                if (buffer.Arguments.Length > 0)
                    events.Add(new ChatEvent.ToolCallArgumentsDelta(buffer.Id, buffer.Arguments.ToString()));
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
                if (buffer.Id.Length == 0) buffer.Id = $"call_{index}";
                events.Add(new ChatEvent.ToolCallStart(buffer.Id, buffer.Name));
                if (buffer.Arguments.Length > 0)
                    events.Add(new ChatEvent.ToolCallArgumentsDelta(buffer.Id, buffer.Arguments.ToString()));
            }

            events.Add(new ChatEvent.ToolCallCompleted(
                buffer.Id,
                buffer.Arguments.Length > 0 ? buffer.Arguments.ToString() : "{}"));
        }

        pending.Clear();
        return events;
    }

    private static bool IsJsonResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadErrorFromBody(string body, out ChatEvent.Failed failure)
    {
        failure = null!;
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && TryReadError(root, body, out failure);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadError(JsonElement root, string payload, out ChatEvent.Failed failure)
    {
        failure = null!;

        if (TryGetObject(root, "error", out var error))
        {
            var code = FirstString(error, "code") ?? FirstString(error, "type") ?? "provider_error";
            var message = FirstString(error, "message") ?? payload;
            failure = new ChatEvent.Failed(code, Truncate(message, 500));
            return true;
        }

        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            var message = FirstString(root, "message") ?? payload;
            failure = new ChatEvent.Failed("provider_error", Truncate(message, 500));
            return true;
        }

        // MiniMax 旧版错误体：{"base_resp":{"status_code":1004,"status_msg":"..."}}
        if (TryGetObject(root, "base_resp", out var baseResp) &&
            baseResp.TryGetProperty("status_code", out var statusCode) &&
            statusCode.TryGetInt32(out var codeValue) &&
            codeValue != 0)
        {
            var message = FirstString(baseResp, "status_msg") ?? payload;
            failure = new ChatEvent.Failed("provider_" + codeValue, Truncate(message, 500));
            return true;
        }

        return false;
    }

    private static bool TryGetObject(JsonElement element, string property, out JsonElement value) =>
        element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? FirstString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static TokenUsage? ReadUsage(JsonElement usage)
    {
        var input = ReadLong(usage, "prompt_tokens");
        var output = ReadLong(usage, "completion_tokens");
        var cached = ReadLong(usage, "cached_tokens")
            ?? ReadNestedLong(usage, "prompt_tokens_details", "cached_tokens")
            ?? ReadLong(usage, "prompt_cache_hit_tokens");
        var reasoning = ReadNestedLong(usage, "completion_tokens_details", "reasoning_tokens");

        if (input is null && output is null && cached is null && reasoning is null) return null;
        return new TokenUsage(input, output, cached, reasoning);
    }

    private static long? ReadNestedLong(JsonElement element, string parent, string property) =>
        TryGetObject(element, parent, out var nested) ? ReadLong(nested, property) : null;

    private static long? ReadLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
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

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed class ToolCallBuffer
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public StringBuilder Arguments { get; } = new();

        public bool Started { get; set; }
    }
}
