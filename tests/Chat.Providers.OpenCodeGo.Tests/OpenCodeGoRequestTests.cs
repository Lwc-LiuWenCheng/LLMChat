using System.Net;
using System.Text.Json;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.OpenCodeGo.Tests;

public sealed class OpenCodeGoRequestTests
{
    private static JsonDocument ParseBody(CapturedRequest request) =>
        JsonDocument.Parse(request.Body ?? "{}");

    private static JsonElement Message(JsonDocument body, int index) =>
        body.RootElement.GetProperty("messages")[index];

    [Fact]
    public async Task Request_UsesMaxTokensAndStreamOptionsByDefault()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));

        await TestHarness.CollectAsync(handler, TestHarness.Request());

        using var body = ParseBody(handler.LastRequest);
        Assert.True(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task Request_CanSwitchToMaxCompletionTokensAndDisableUsage()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));
        var context = TestHarness.Context(
            handler,
            settings:
            [
                (OpenCodeGoProvider.MaxTokensFieldSetting, "max_completion_tokens"),
                (OpenCodeGoProvider.StreamUsageSetting, "false"),
            ]);

        await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        using var body = ParseBody(handler.LastRequest);
        Assert.True(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task Request_SettingModelOverridesRequestModel()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));
        var context = TestHarness.Context(
            handler,
            settings: [(OpenCodeGoProvider.ModelSetting, "glm-5.3")]);

        await TestHarness.CollectAsync(handler, TestHarness.Request(model: "deepseek-v4-pro"), context);

        using var body = ParseBody(handler.LastRequest);
        Assert.Equal("glm-5.3", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Request_IncludesSystemToolsAndToolHistory()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));
        var request = new ChatRequest
        {
            Model = OpenCodeGoProvider.DefaultModel,
            System = "你是助手",
            Messages =
            [
                new ChatMessage(ChatRole.User, "北京天气？"),
                new ChatMessage(ChatRole.Assistant, [new ChatPart.ToolCall("call_1", "get_weather", """{"city":"北京"}""")]),
                new ChatMessage(ChatRole.User, [new ChatPart.ToolResult("call_1", "晴 25℃")]),
            ],
            Tools = [new ToolDefinition("get_weather", "查询天气", """{"type":"object"}""")],
        };

        await TestHarness.CollectAsync(handler, request);

        using var body = ParseBody(handler.LastRequest);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", Message(body, 0).GetProperty("role").GetString());
        Assert.Equal("你是助手", Message(body, 0).GetProperty("content").GetString());
        Assert.Equal("user", Message(body, 1).GetProperty("role").GetString());
        Assert.Equal("assistant", Message(body, 2).GetProperty("role").GetString());
        Assert.Equal(
            "get_weather",
            Message(body, 2).GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", Message(body, 3).GetProperty("role").GetString());
        Assert.Equal("call_1", Message(body, 3).GetProperty("tool_call_id").GetString());

        var tool = body.RootElement.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Request_ReplaysThinkingAsReasoningContent()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));
        var request = new ChatRequest
        {
            Model = OpenCodeGoProvider.DefaultModel,
            Messages =
            [
                new ChatMessage(ChatRole.User, "问题"),
                new ChatMessage(ChatRole.Assistant, [new ChatPart.Thinking("先想一下"), new ChatPart.Text("答案")]),
            ],
        };

        await TestHarness.CollectAsync(handler, request);

        using var body = ParseBody(handler.LastRequest);
        var assistant = Message(body, 1);
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("先想一下", assistant.GetProperty("reasoning_content").GetString());
        Assert.Equal("答案", assistant.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ListModels_ParsesGatewayResponse()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json(
            """{"data":[{"id":"deepseek-v4-pro","display_name":"DeepSeek V4 Pro"},{"id":"glm-5.3"}]}""")));
        var context = TestHarness.Context(handler);

        var models = await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "deepseek-v4-pro", "glm-5.3" }, models.Select(m => m.Id));
        Assert.Equal("DeepSeek V4 Pro", models[0].DisplayName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://opencode.ai/inference/go/openai/v1/models", request.Uri.AbsoluteUri);
        Assert.Equal("test-session", request.SessionHeader);
        Assert.Equal("LLMChat/0.1.0", request.UserAgent);
    }

    [Fact]
    public async Task ListModels_FailureFallsBackToBuiltIns()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json(
            """{"error":{"message":"boom"}}""",
            HttpStatusCode.InternalServerError)));
        var context = TestHarness.Context(handler);

        var models = await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Contains(models, m => m.Id == OpenCodeGoProvider.DefaultModel);
        Assert.Contains(models, m => m.Id == "glm-5.3");
        Assert.Equal(OpenCodeGoProvider.BuiltInModels.Count, models.Count);
    }

    [Fact]
    public async Task ListModels_ConfiguredModelShortCircuits()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json("""{"data":[]}""")));
        var context = TestHarness.Context(
            handler,
            settings: [(OpenCodeGoProvider.ModelSetting, "kimi-k2")]);

        var models = await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        var model = Assert.Single(models);
        Assert.Equal("kimi-k2", model.Id);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListModels_InvalidBaseUrlFallsBackWithoutThrowing()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json("""{"data":[]}""")));
        var context = TestHarness.Context(
            handler,
            settings: [(OpenCodeGoProvider.BaseUrlSetting, "not-an-absolute-url")]);

        var models = await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(OpenCodeGoProvider.BuiltInModels.Count, models.Count);
        Assert.Empty(handler.Requests);
        Assert.Contains(context.Logs, log => log.Contains("回退", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListModels_NoApiKeyFallsBack()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json("""{"data":[]}""")));
        var context = TestHarness.Context(handler, apiKey: null);

        var models = await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(OpenCodeGoProvider.BuiltInModels.Count, models.Count);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Headers_UserAgentOverrideAppliesToBothEndpoints()
    {
        var handler = new StubHttpHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Sse.Json("""{"data":[{"id":"deepseek-v4-pro"}]}""")
            : Sse.Response(
                Sse.Frame("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""") +
                "data: [DONE]\n\n")));
        var context = TestHarness.Context(
            handler,
            settings: [(OpenCodeGoProvider.UserAgentSetting, "LLMChat-custom/9.9")]);

        await TestHarness.CollectAsync(handler, TestHarness.Request(), context);
        await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("LLMChat-custom/9.9", request.UserAgent);
            Assert.Equal("test-session", request.SessionHeader);
        });
    }

    [Fact]
    public async Task ListModels_MissingSessionIdFallsBackWithoutRequest()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json("""{"data":[{"id":"deepseek-v4-pro"}]}""")));
        var context = TestHarness.Context(handler, sessionId: "");

        var models = await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(OpenCodeGoProvider.BuiltInModels.Count, models.Count);
        Assert.Empty(handler.Requests);
        Assert.Contains(context.Logs, log => log.Contains("SessionId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListModels_UserCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHttpHandler((_, ct) => throw new OperationCanceledException(ct));
        var context = TestHarness.Context(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OpenCodeGoProvider().ListModelsAsync(context, cts.Token));
    }

    [Fact]
    public async Task Headers_BlankUserAgentOverrideFallsBackToDefault()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") + "data: [DONE]\n\n")));
        var context = TestHarness.Context(
            handler,
            settings: [(OpenCodeGoProvider.UserAgentSetting, "   ")]);

        await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        Assert.Equal("LLMChat/0.1.0", Assert.Single(handler.Requests).UserAgent);
    }

    [Fact]
    public async Task Stream_RejectsNonHttpSchemeWithoutRequest()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = TestHarness.Context(
            handler,
            settings: [(OpenCodeGoProvider.BaseUrlSetting, "ftp://example.test/v1")]);

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("invalid_base_url", failed.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListModels_NonHttpSchemeFallsBackWithoutRequest()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = TestHarness.Context(
            handler,
            settings: [(OpenCodeGoProvider.BaseUrlSetting, "file:///tmp/endpoint")]);

        var models = await new OpenCodeGoProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(OpenCodeGoProvider.BuiltInModels.Count, models.Count);
        Assert.Empty(handler.Requests);
        Assert.Contains(context.Logs, log => log.Contains("http", StringComparison.OrdinalIgnoreCase));
    }
}
