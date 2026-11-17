using System.Net;
using System.Text.Json;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.OpenAiCompatible.Tests;

public sealed class OpenAiRequestTests
{
    private static StubHttpHandler OkHandler() => new((_, _) => Task.FromResult(Sse.Response(
        Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
        "data: [DONE]" + "\n\n")));

    [Fact]
    public async Task Request_PostsOpenAiChatCompletionsBody()
    {
        var handler = OkHandler();
        var context = TestHarness.Context(handler);
        var request = new ChatRequest
        {
            Model = "glm-4.6",
            System = "你是助手",
            Messages =
            [
                new ChatMessage(ChatRole.User, "北京天气"),
                new ChatMessage(ChatRole.Assistant,
                [
                    new ChatPart.Text("查天气"),
                    new ChatPart.ToolCall("call_9", "get_weather", """{"city":"北京"}"""),
                ]),
                new ChatMessage(ChatRole.User, [new ChatPart.ToolResult("call_9", "晴 25℃")]),
            ],
            MaxTokens = 512,
            Temperature = 0.2,
            Tools = [new ToolDefinition("get_weather", "查天气", """{"type":"object"}""")],
        };

        await TestHarness.CollectAsync(handler, request, context);

        var captured = handler.LastRequest;
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4/chat/completions", captured.Uri.ToString());
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.Equal("test-key", captured.AuthorizationParameter);
        Assert.Equal("text/event-stream", captured.Accept);

        using var document = JsonDocument.Parse(captured.Body!);
        var root = document.RootElement;
        Assert.Equal("glm-4.6", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("你是助手", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("北京天气", messages[1].GetProperty("content").GetString());

        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("查天气", messages[2].GetProperty("content").GetString());
        var call = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("call_9", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"city":"北京"}""", call.GetProperty("function").GetProperty("arguments").GetString());

        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_9", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("晴 25℃", messages[3].GetProperty("content").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("get_weather", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("查天气", tool.GetProperty("function").GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Request_HonorsMaxCompletionTokensAndStreamUsageSettings()
    {
        var handler = OkHandler();
        var context = TestHarness.Context(
            handler,
            ("openai.maxTokensField", "max_completion_tokens"),
            ("openai.streamUsage", "false"));
        var request = TestHarness.Request() with { MaxTokens = 64 };

        await TestHarness.CollectAsync(handler, request, context);

        using var document = JsonDocument.Parse(handler.LastRequest.Body!);
        var root = document.RootElement;
        Assert.Equal(64, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task Request_UsesOverriddenBaseUrlAndModel()
    {
        var handler = OkHandler();
        var context = TestHarness.Context(
            handler,
            ("openai.baseUrl", "https://example.test/v1/"),
            ("openai.model", "custom-model"));

        await TestHarness.CollectAsync(handler, TestHarness.Request(model: ""), context);

        Assert.Equal("https://example.test/v1/chat/completions", handler.LastRequest.Uri.ToString());
        using var document = JsonDocument.Parse(handler.LastRequest.Body!);
        Assert.Equal("custom-model", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Request_UsesMiniMaxPreset()
    {
        var handler = OkHandler();
        var context = TestHarness.Context(handler, ("openai.preset", "minimax"));

        await TestHarness.CollectAsync(handler, TestHarness.Request(model: "MiniMax-M2"), context);

        Assert.Equal("https://api.minimax.chat/v1/chat/completions", handler.LastRequest.Uri.ToString());
    }

    [Fact]
    public async Task Stream_RejectsInvalidBaseUrl()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = TestHarness.Context(handler, ("openai.baseUrl", "not a url"));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("invalid_base_url", failed.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListModels_ParsesEndpoint()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"glm-4.6"},{"id":"glm-4.5"}]}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        }));

        var models = await new OpenAiCompatibleProvider().ListModelsAsync(TestHarness.Context(handler), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "glm-4.6", "glm-4.5" }, models.Select(m => m.Id));
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4/models", handler.LastRequest.Uri.ToString());
        Assert.Equal("Bearer", handler.LastRequest.AuthorizationScheme);
        Assert.Equal("test-key", handler.LastRequest.AuthorizationParameter);
    }

    [Fact]
    public async Task ListModels_WithoutKey_ReturnsPresetDefault()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = new StubProviderContext(new HttpMessageInvoker(handler, disposeHandler: false), apiKey: null);
        context.SettingsMap["openai.preset"] = "minimax";

        var models = await new OpenAiCompatibleProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "MiniMax-M2" }, models.Select(m => m.Id));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListModels_ConfiguredModelShortCircuits()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = TestHarness.Context(handler, ("openai.model", "glm-4.5-flash"));

        var models = await new OpenAiCompatibleProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "glm-4.5-flash" }, models.Select(m => m.Id));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListModels_InvalidBaseUrl_FallsBackToPreset()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = TestHarness.Context(handler, ("openai.baseUrl", "not a url"));

        var models = await new OpenAiCompatibleProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "glm-4.6" }, models.Select(m => m.Id));
        Assert.Empty(handler.Requests);
        Assert.Contains(context.Logs, log => log.Contains("openai.baseUrl"));
    }

    [Fact]
    public async Task Stream_RejectsNonHttpSchemeWithoutRequest()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = TestHarness.Context(handler, ("openai.baseUrl", "ftp://example.test/v1"));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("invalid_base_url", failed.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListModels_NonHttpSchemeFallsBackToPreset()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var context = TestHarness.Context(handler, ("openai.baseUrl", "file:///tmp/endpoint"));

        var models = await new OpenAiCompatibleProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "glm-4.6" }, models.Select(m => m.Id));
        Assert.Empty(handler.Requests);
        Assert.Contains(context.Logs, log => log.Contains("http", StringComparison.OrdinalIgnoreCase));
    }
}
