using System.Text;
using System.Text.Json;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.DeepSeek.Tests;

public sealed class DeepSeekImageTests
{
    private static StubHttpHandler OkHandler() => new((_, _) => Task.FromResult(Sse.Response(
        Sse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"好"}}""") +
        Sse.Frame("""{"type":"message_stop"}"""))));

    [Fact]
    public async Task Request_TextOnlyKeepsBlockArrayShape()
    {
        var handler = OkHandler();

        await TestHarness.CollectAsync(handler, TestHarness.Request());

        using var body = JsonDocument.Parse(handler.LastRequest.Body!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        var content = messages[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Single(content.EnumerateArray());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("你好", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Request_EncodesImageAsAnthropicBase64Block()
    {
        var handler = OkHandler();
        var bytes = Encoding.UTF8.GetBytes("fake-png-bytes");
        var request = new ChatRequest
        {
            Model = "deepseek-flash",
            Messages =
            [
                new ChatMessage(ChatRole.User,
                [
                    new ChatPart.Text("这是什么？"),
                    new ChatPart.Image("image/jpeg", bytes),
                ]),
            ],
        };

        await TestHarness.CollectAsync(handler, request);

        using var body = JsonDocument.Parse(handler.LastRequest.Body!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("这是什么？", content[0].GetProperty("text").GetString());
        var image = content[1];
        Assert.Equal("image", image.GetProperty("type").GetString());
        var source = image.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(bytes), source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Request_ImageWithBlankMimeDefaultsToPngAndPreservesOrder()
    {
        var handler = OkHandler();
        var first = Encoding.UTF8.GetBytes("first");
        var second = Encoding.UTF8.GetBytes("second");
        var request = new ChatRequest
        {
            Model = "deepseek-flash",
            Messages =
            [
                new ChatMessage(ChatRole.User,
                [
                    new ChatPart.Image("", first),
                    new ChatPart.Text("中间"),
                    new ChatPart.Image("image/webp", second),
                ]),
            ],
        };

        await TestHarness.CollectAsync(handler, request);

        using var body = JsonDocument.Parse(handler.LastRequest.Body!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(first), content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Equal("中间", content[1].GetProperty("text").GetString());
        Assert.Equal("image", content[2].GetProperty("type").GetString());
        Assert.Equal("image/webp", content[2].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(second), content[2].GetProperty("source").GetProperty("data").GetString());
    }

    [Fact]
    public async Task Request_FileRefIsIgnoredWithoutThrowing()
    {
        var handler = OkHandler();
        var request = new ChatRequest
        {
            Model = "deepseek-flash",
            Messages =
            [
                new ChatMessage(ChatRole.User,
                [
                    new ChatPart.Text("你好"),
                    new ChatPart.FileRef("file-1", "application/pdf"),
                ]),
            ],
        };

        await TestHarness.CollectAsync(handler, request);

        using var body = JsonDocument.Parse(handler.LastRequest.Body!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Single(content.EnumerateArray());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Descriptor_DeclaresImageInputButNotFileUpload()
    {
        var capabilities = new DeepSeekProvider().Descriptor.Capabilities;
        Assert.True(capabilities.HasFlag(ProviderCapabilities.ImageInput));
        Assert.False(capabilities.HasFlag(ProviderCapabilities.FileUpload));
    }
}
