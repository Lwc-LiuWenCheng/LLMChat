using System.Net;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.OpenCodeGo.Tests;

public sealed class OpenCodeGoStreamingTests
{
    [Fact]
    public async Task Stream_SendsGatewayHeadersAndUrl()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"好"},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));

        await TestHarness.CollectAsync(handler, TestHarness.Request());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://opencode.ai/inference/go/openai/v1/chat/completions", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("test-key", request.AuthorizationParameter);
        Assert.Contains("text/event-stream", request.Accept, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("test-session", request.SessionHeader);
        Assert.Equal("LLMChat/0.1.0", request.UserAgent);
        Assert.Contains("\"stream\":true", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_MissingSessionIdFailsWithoutRequest()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json("""{"error":{"message":"unexpected"}}""")));
        var context = TestHarness.Context(handler, sessionId: "");

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("missing_session_id", failed.Code);
        Assert.False(failed.Retryable);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stream_MapsReasoningContentAndText()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            ": keep-alive\n\n" +
            Sse.Frame("""{"choices":[{"delta":{"reasoning_content":"先想"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"reasoning":"一下"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"content":"答案"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Equal(new[] { "先想", "一下" }, events.OfType<ChatEvent.ThinkingDelta>().Select(e => e.Text));
        Assert.Equal(new[] { "答案" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
        Assert.Equal(StopReason.EndTurn, Assert.IsType<ChatEvent.Done>(events[^1]).Reason);
        Assert.DoesNotContain(events, e => e is ChatEvent.Failed);
    }

    [Fact]
    public async Task Stream_PreservesOpencodeBillingBlockAndUsage()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"好"},"finish_reason":"stop"}]}""") +
            Sse.Frame("""{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":30,"opencode":{"list_cost":0.0123,"billing_source":"monthly","tokens":{"input":120,"output":30,"cached":10,"reasoning":7}}}}""") +
            "data: [DONE]\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var raw = Assert.Single(events.OfType<ChatEvent.RawEvent>());
        Assert.Contains("list_cost", raw.Json, StringComparison.Ordinal);
        Assert.Contains("billing_source", raw.Json, StringComparison.Ordinal);
        Assert.Contains("monthly", raw.Json, StringComparison.Ordinal);

        var usage = Assert.Single(events.OfType<ChatEvent.UsageEvent>()).Usage;
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
        Assert.Equal(10, usage.CachedInputTokens);
        Assert.Equal(7, usage.ReasoningTokens);
        Assert.IsType<ChatEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Stream_BillingTokensFillMissingUsage()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"好"},"finish_reason":"stop"}]}""") +
            Sse.Frame("""{"choices":[],"usage":{"opencode":{"list_cost":0.5,"tokens":{"input":50,"output":12}}}}""") +
            "data: [DONE]\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Single(events.OfType<ChatEvent.RawEvent>());
        var usage = Assert.Single(events.OfType<ChatEvent.UsageEvent>()).Usage;
        Assert.Equal(50, usage.InputTokens);
        Assert.Equal(12, usage.OutputTokens);
        Assert.Null(usage.CachedInputTokens);
        Assert.Null(usage.ReasoningTokens);
    }

    [Fact]
    public async Task Stream_FiveHourLimitIsReadable()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json(
            """{"error":{"code":"rate_limit_exceeded","message":"5-hour limit exceeded for OpenCode Go"}}""",
            HttpStatusCode.TooManyRequests)));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("limit_exceeded", failed.Code);
        Assert.Contains("5 小时限额", failed.Message, StringComparison.Ordinal);
        Assert.True(failed.Retryable);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Theory]
    [InlineData("weekly limit reached", "周限额")]
    [InlineData("monthly quota exhausted", "月限额")]
    [InlineData("too many requests", "请求频率限制")]
    public async Task Stream_OtherLimitsAreReadable(string providerMessage, string expected)
    {
        var payload = "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"" + providerMessage + "\"}}";
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json(payload, HttpStatusCode.TooManyRequests)));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("limit_exceeded", failed.Code);
        Assert.Contains(expected, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_TruncatedStreamFailsWithoutDone()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"半截"}}]}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("stream_truncated", failed.Code);
        Assert.True(failed.Retryable);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_JsonErrorBodyWith200MapsLimit()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json(
            """{"error":{"type":"insufficient_quota","message":"monthly quota exhausted"}}""")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("limit_exceeded", failed.Code);
        Assert.Contains("月限额", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_ErrorFrameStopsStream()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"好"}}]}""") +
            Sse.Frame("""{"error":{"code":"invalid_request_error","message":"bad request"}}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("invalid_request_error", failed.Code);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_MissingApiKeyFailsWithoutRequest()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(string.Empty)));
        var context = TestHarness.Context(handler, apiKey: null);

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("missing_api_key", failed.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stream_CustomBaseUrlIsUsed()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"好"},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));
        var context = TestHarness.Context(handler, settings: [(OpenCodeGoProvider.BaseUrlSetting, "http://127.0.0.1:9999/proxy/openai/v1/")]);

        await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        Assert.Equal(
            "http://127.0.0.1:9999/proxy/openai/v1/chat/completions",
            Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Stream_ToolCallIdArrivingLateKeepsStartId()
    {
        // 首帧没有 id（只有 name/arguments），第二帧才给出真实 id：
        // Start 已经用 call_0 发出，后续 ArgumentsDelta / Completed 必须沿用同一 id。
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"type":"function","function":{"name":"get_weather","arguments":"{\"city\":"}}]}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_real","function":{"arguments":"\"北京\"}"}}]}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""") +
            "data: [DONE]\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var start = Assert.Single(events.OfType<ChatEvent.ToolCallStart>());
        Assert.Equal("call_0", start.CallId);
        Assert.All(events.OfType<ChatEvent.ToolCallArgumentsDelta>(), delta => Assert.Equal("call_0", delta.CallId));
        var completed = Assert.Single(events.OfType<ChatEvent.ToolCallCompleted>());
        Assert.Equal("call_0", completed.CallId);
        Assert.Equal("{\"city\":\"北京\"}", completed.ArgumentsJson);
        Assert.Equal(StopReason.ToolUse, Assert.IsType<ChatEvent.Done>(events[^1]).Reason);
    }

    [Fact]
    public async Task Stream_SkipsMalformedFramesWithSingleWarning()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("{not json") +
            Sse.Frame("not json either") +
            Sse.Frame("""{"choices":[{"delta":{"content":"好"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n")));
        var context = TestHarness.Context(handler);

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        Assert.Equal(new[] { "好" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
        Assert.IsType<ChatEvent.Done>(events[^1]);
        var warning = Assert.Single(context.Logs, log => log.Contains("无法解析", StringComparison.Ordinal));
        Assert.StartsWith("Warning:", warning, StringComparison.Ordinal);
    }
}
