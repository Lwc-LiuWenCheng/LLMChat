using System.Net;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.DeepSeek.Tests;

public sealed class DeepSeekStreamingTests
{
    [Fact]
    public async Task Stream_ReplaysAnthropicFixture()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            "event: message_start\n" +
            Sse.Frame("""{"type":"message_start","message":{"id":"msg_1","role":"assistant"}}""") +
            Sse.Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""") +
            Sse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"先想"}}""") +
            Sse.Frame("""{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"你"}}""") +
            Sse.Frame("""{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"好"}}""") +
            Sse.Frame("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":10}}""") +
            Sse.Frame("""{"type":"message_stop"}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Equal(new[] { "先想" }, events.OfType<ChatEvent.ThinkingDelta>().Select(e => e.Text));
        Assert.Equal(new[] { "你", "好" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
        var usage = Assert.Single(events.OfType<ChatEvent.UsageEvent>()).Usage;
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
        Assert.Equal(10, usage.CachedInputTokens);
        Assert.Equal(StopReason.EndTurn, Assert.IsType<ChatEvent.Done>(events[^1]).Reason);
        Assert.DoesNotContain(events, e => e is ChatEvent.Failed);
        Assert.Single(events.OfType<ChatEvent.Done>());
    }

    [Fact]
    public async Task Stream_MapsToolUseStopReason()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"稍等"}}""") +
            Sse.Frame("""{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""") +
            Sse.Frame("""{"type":"message_stop"}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Equal(StopReason.ToolUse, Assert.IsType<ChatEvent.Done>(events[^1]).Reason);
    }

    [Fact]
    public async Task Stream_MissingApiKeyFailsWithoutRequest()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(string.Empty)));
        var context = TestHarness.Context(handler, apiKey: null);

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Equal("missing_api_key", failed.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stream_HttpErrorIncludesBodyAndStatus()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json(
            """{"error":{"message":"Not Found"}}""",
            HttpStatusCode.NotFound)));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("http_404", failed.Code);
        Assert.Contains("Not Found", failed.Message, StringComparison.Ordinal);
        Assert.False(failed.Retryable);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_ErrorFrameStopsStream()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"半"}}""") +
            Sse.Frame("""{"type":"error","error":{"type":"overloaded_error","message":"overloaded"}}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        var failed = Assert.Single(events.OfType<ChatEvent.Failed>());
        Assert.Equal("provider_error", failed.Code);
        Assert.Contains("overloaded", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is ChatEvent.Done);
    }

    [Fact]
    public async Task Stream_EofWithoutMessageStopStillCompletes()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Response(
            Sse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"截断"}}"""))));

        var events = await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Equal(new[] { "截断" }, events.OfType<ChatEvent.TextDelta>().Select(e => e.Text));
        Assert.Equal(StopReason.EndTurn, Assert.IsType<ChatEvent.Done>(events[^1]).Reason);
        Assert.DoesNotContain(events, e => e is ChatEvent.Failed);
    }
}
