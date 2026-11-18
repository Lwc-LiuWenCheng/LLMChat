using System.Net;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.DeepSeek.Tests;

public sealed class DeepSeekEndpointTests
{
    private static StubHttpHandler OkHandler() => new((_, _) => Task.FromResult(Sse.Response(
        Sse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"好"}}""") +
        Sse.Frame("""{"type":"message_stop"}"""))));

    [Fact]
    public async Task Stream_DefaultBaseUrlKeepsAnthropicPrefix()
    {
        // 回归：默认 baseUrl 是 https://api.deepseek.com/anthropic/v1，
        // 以前用 new Uri(base, "/messages") 会重置到域根，真实请求变成 /messages → 404。
        var handler = OkHandler();

        await TestHarness.CollectAsync(handler, TestHarness.Request());

        Assert.Equal(
            "https://api.deepseek.com/anthropic/v1/messages",
            Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Stream_TrailingSlashBaseUrlIsNormalized()
    {
        var handler = OkHandler();
        var context = TestHarness.Context(
            handler,
            settings: [(DeepSeekProvider.BaseUrlSetting, "https://api.deepseek.com/anthropic/v1/")]);

        await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        Assert.Equal(
            "https://api.deepseek.com/anthropic/v1/messages",
            Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Stream_CustomBaseUrlKeepsItsPath()
    {
        var handler = OkHandler();
        var context = TestHarness.Context(
            handler,
            settings: [(DeepSeekProvider.BaseUrlSetting, "http://127.0.0.1:9999/mock/anthropic/v1")]);

        await TestHarness.CollectAsync(handler, TestHarness.Request(), context);

        Assert.Equal(
            "http://127.0.0.1:9999/mock/anthropic/v1/messages",
            Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Stream_SendsAnthropicHeadersAndBody()
    {
        var handler = OkHandler();

        await TestHarness.CollectAsync(handler, TestHarness.Request());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("test-key", request.ApiKeyHeader);
        Assert.Equal("2023-06-01", request.AnthropicVersion);
        Assert.Contains("text/event-stream", request.Accept, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"stream\":true", request.Body, StringComparison.Ordinal);
        Assert.Contains("deepseek-flash", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListModels_UsesOriginModelsEndpoint()
    {
        // /anthropic/v1 没有 models 列表，官方 /models 在域根：这里刻意保持 /models。
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json(
            """{"data":[{"id":"deepseek-flash"},{"id":"deepseek-reasoner"}]}""")));
        var context = TestHarness.Context(handler);

        var models = await new DeepSeekProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "deepseek-flash", "deepseek-reasoner" }, models.Select(m => m.Id));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.deepseek.com/models", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
    }

    [Fact]
    public async Task ListModels_CustomBaseUrlStillUsesOriginRoot()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse.Json("""{"data":[]}""")));
        var context = TestHarness.Context(
            handler,
            settings: [(DeepSeekProvider.BaseUrlSetting, "http://127.0.0.1:9999/mock/anthropic/v1")]);

        await new DeepSeekProvider().ListModelsAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:9999/models", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }
}
