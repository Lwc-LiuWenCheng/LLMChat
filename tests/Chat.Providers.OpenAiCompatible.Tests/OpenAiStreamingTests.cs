using System.Text;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.OpenAiCompatible.Tests;

public sealed class OpenAiStreamingTests
{
    [Fact]
    public async Task Stream_MapsTextDeltasAndDone()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            ": ping" + "\n\n" +
            Sse.Frame("""{"choices":[{"delta":{"content":"你"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"content":"好"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Equal(new[] { "你", "好" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
        var done = Assert.IsType<ChatEvent.Done>(events[^1]);
        Assert.Equal(StopReason.EndTurn, done.Reason);
        Assert.DoesNotContain(events, e => e is ChatEvent.Failed);
        Assert.Empty(events.OfType<ChatEvent.ThinkingDelta>());
    }

    [Fact]
    public async Task Stream_AcceptsAllThinkingFieldNames()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"reasoning_content":"思考一"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"reasoning":"思考二"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"reasoning_text":"思考三"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"content":"答案"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Equal(
            new[] { "思考一", "思考二", "思考三" },
            events.OfType<ChatEvent.ThinkingDelta>().Select(e => e.Text));
        Assert.Equal(new[] { "答案" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
    }

    [Fact]
    public async Task Stream_AggregatesIncrementalToolCalls()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\""}}]}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"北京\"}"}}]}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var start = Assert.IsType<ChatEvent.ToolCallStart>(events[0]);
        Assert.Equal("call_1", start.CallId);
        Assert.Equal("get_weather", start.Name);

        Assert.Equal(
            new[] { "{\"city\":\"", "北京\"}" },
            events.OfType<ChatEvent.ToolCallArgumentsDelta>().Select(e => e.JsonDelta));

        var completed = Assert.Single(events.OfType<ChatEvent.ToolCallCompleted>());
        Assert.Equal("call_1", completed.CallId);
        Assert.Equal("{\"city\":\"北京\"}", completed.ArgumentsJson);

        var done = Assert.IsType<ChatEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
    }

    [Fact]
    public async Task Stream_ReportsUsage()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"好"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""") +
            Sse.Frame("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":80},"completion_tokens_details":{"reasoning_tokens":7}}}""") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var usage = Assert.Single(events.OfType<ChatEvent.UsageEvent>()).Usage;
        Assert.Equal(100L, usage.InputTokens);
        Assert.Equal(20L, usage.OutputTokens);
        Assert.Equal(80L, usage.CachedInputTokens);
        Assert.Equal(7L, usage.ReasoningTokens);
        Assert.IsType<ChatEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Stream_MapsHttpError()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"rate limited"}}""", Encoding.UTF8, "application/json"),
        }));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("http_429", failed.Code);
        Assert.True(failed.Retryable);
        Assert.Contains("rate limited", failed.Message);
    }

    [Fact]
    public async Task Stream_MapsErrorFrameInStream()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"error":{"message":"quota exceeded","code":"insufficient_quota"}}""") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("insufficient_quota", failed.Code);
        Assert.Contains("quota exceeded", failed.Message);
        Assert.False(failed.Retryable);
    }

    [Fact]
    public async Task Stream_MapsNetworkError()
    {
        var handler = new StubHttpHandler((_, _) => throw new HttpRequestException("connection refused"));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("network_error", failed.Code);
        Assert.True(failed.Retryable);
        Assert.Contains("connection refused", failed.Message);
    }

    [Fact]
    public async Task Stream_WithoutApiKey_FailsFast()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response("data: [DONE]" + "\n\n")));
        var context = new StubProviderContext(new HttpMessageInvoker(handler, disposeHandler: false), apiKey: null);

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("missing_api_key", failed.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stream_HonorsCancellation()
    {
        const string prefix = "data: {\"choices\":[{\"delta\":{\"content\":\"半\"}}]}\n\n";
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingSseStream(prefix)),
        }));

        using var cts = new CancellationTokenSource();
        var context = TestHarness.Context(handler);
        var events = new List<ChatEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in new OpenAiCompatibleProvider().StreamAsync(TestHarness.Request(), context, cts.Token))
            {
                events.Add(evt);
                if (evt is ChatEvent.TextDelta) cts.Cancel();
            }
        });

        Assert.Contains(events, e => e is ChatEvent.TextDelta { Text: "半" });
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_TruncatedAtEof_ReturnsFailedWithoutDone()
    {
        // 两帧文本后 EOF：既没有 finish_reason 也没有 [DONE] → 必须报 stream_truncated。
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"半截话"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"content":"话"}}]}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(events[^1]);
        Assert.Equal("stream_truncated", failed.Code);
        Assert.True(failed.Retryable);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
        Assert.Equal(new[] { "半截话", "话" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
    }

    [Fact]
    public async Task Stream_AcceptsDoneWithoutFinishReason()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"完整"}}]}""") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.IsType<ChatEvent.Done>(events[^1]);
        Assert.DoesNotContain(events, e => e is ChatEvent.Failed);
    }

    [Fact]
    public async Task Stream_AcceptsFinishReasonWithoutDone()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"content":"完整"}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var done = Assert.IsType<ChatEvent.Done>(events[^1]);
        Assert.Equal(StopReason.EndTurn, done.Reason);
        Assert.DoesNotContain(events, e => e is ChatEvent.Failed);
    }

    [Fact]
    public async Task Stream_JsonErrorBodyWith200_ReturnsFailed()
    {
        // HTTP 200 + application/json 错误体（非 SSE）。
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"error":{"code":"insufficient_quota","message":"You exceeded your current quota"}}""",
                Encoding.UTF8,
                "application/json"),
        }));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("insufficient_quota", failed.Code);
        Assert.Contains("quota", failed.Message);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_NonSseNonErrorBody_ReturnsInvalidResponse()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"hi"}}]}""", Encoding.UTF8, "application/json"),
        }));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("invalid_response", failed.Code);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_ReadError_MapsToFailed()
    {
        const string prefix = "data: {\"choices\":[{\"delta\":{\"content\":\"半\"}}]}\n\n";
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingSseStream(prefix)),
        }));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(events[^1]);
        Assert.Equal("network_error", failed.Code);
        Assert.True(failed.Retryable);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_Timeout_MapsToNetworkError()
    {
        var handler = new StubHttpHandler((_, _) => throw new TaskCanceledException("The request timed out."));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("network_error", failed.Code);
        Assert.True(failed.Retryable);
        Assert.Contains("超时", failed.Message);
    }

    [Theory]
    [InlineData("length", StopReason.MaxTokens)]
    [InlineData("content_filter", StopReason.ContentFilter)]
    [InlineData("stop_sequence", StopReason.StopSequence)]
    [InlineData("function_call", StopReason.ToolUse)]
    [InlineData("unknown_reason", StopReason.EndTurn)]
    public async Task Stream_MapsFinishReasonVariants(string finishReason, StopReason expected)
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("{\"choices\":[{\"delta\":{},\"finish_reason\":\"" + finishReason + "\"}]}") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var done = Assert.IsType<ChatEvent.Done>(events[^1]);
        Assert.Equal(expected, done.Reason);
        Assert.DoesNotContain(events, e => e is ChatEvent.Failed);
    }

    [Fact]
    public async Task Stream_AggregatesParallelToolCalls()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"get_weather","arguments":"{\"city\":"}},{"index":1,"id":"call_b","type":"function","function":{"name":"get_time","arguments":"{\"tz\":"}}]}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"北京\"}"}},{"index":1,"function":{"arguments":"\"UTC\"}"}}]}}]}""") +
            Sse.Frame("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""") +
            "data: [DONE]" + "\n\n")));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var starts = events.OfType<ChatEvent.ToolCallStart>().ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(("call_a", "get_weather"), (starts[0].CallId, starts[0].Name));
        Assert.Equal(("call_b", "get_time"), (starts[1].CallId, starts[1].Name));

        Assert.Equal(4, events.OfType<ChatEvent.ToolCallArgumentsDelta>().Count());

        var completed = events.OfType<ChatEvent.ToolCallCompleted>().ToList();
        Assert.Equal(2, completed.Count);
        Assert.Equal("call_a", completed[0].CallId);
        Assert.Equal("{\"city\":\"北京\"}", completed[0].ArgumentsJson);
        Assert.Equal("call_b", completed[1].CallId);
        Assert.Equal("{\"tz\":\"UTC\"}", completed[1].ArgumentsJson);

        var done = Assert.IsType<ChatEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
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
            "data: [DONE]" + "\n\n")));

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
            "data: [DONE]" + "\n\n")));
        var context = TestHarness.Context(handler);

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        Assert.Equal(new[] { "好" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
        Assert.IsType<ChatEvent.Done>(events[^1]);
        var warning = Assert.Single(context.Logs, log => log.Contains("无法解析", StringComparison.Ordinal));
        Assert.StartsWith("Warning:", warning, StringComparison.Ordinal);
    }
}
