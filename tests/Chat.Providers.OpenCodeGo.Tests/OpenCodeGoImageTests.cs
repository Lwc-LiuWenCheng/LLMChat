using System.Text;
using System.Text.Json;
using Chat.Abstractions;
using Xunit;

namespace Chat.Providers.OpenCodeGo.Tests;

public sealed class OpenCodeGoImageTests
{
    private static StubHttpHandler OkHandler() => new((_, _) => Task.FromResult(Sse.Response(
        Sse.Frame("{\"choices\":[{\"delta\":{\"content\":\"好\"},\"index\":0}]}") +
        Sse.Frame("data: [DONE]\n\n"))));

    [Fact]
    public async Task Request_TextOnlyKeepsStringContentAndSessionHeader()
    {
        var handler = OkHandler();

        await TestHarness.CollectAsync(handler, TestHarness.Request());

        var captured = handler.LastRequest;
        using var body = JsonDocument.Parse(captured.Body!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("你好", content.GetString());
        Assert.Equal("test-session", captured.SessionHeader);
    }

    [Fact]
    public async Task Request_EncodesImageAsImageUrlDataUri()
    {
        var handler = OkHandler();
        var bytes = Encoding.UTF8.GetBytes("fake-png-bytes");
        var request = new ChatRequest
        {
            Model = OpenCodeGoProvider.DefaultModel,
            Messages =
            [
                new ChatMessage(ChatRole.User,
                [
                    new ChatPart.Text("这是什么？"),
                    new ChatPart.Image("image/png", bytes),
                ]),
            ],
        };

        await TestHarness.CollectAsync(handler, request);

        using var body = JsonDocument.Parse(handler.LastRequest.Body!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("这是什么？", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Request_ImageWithBlankMimeDefaultsToPngAndPreservesOrder()
    {
        var handler = OkHandler();
        var first = Encoding.UTF8.GetBytes("first");
        var second = Encoding.UTF8.GetBytes("second");
        var request = new ChatRequest
        {
            Model = OpenCodeGoProvider.DefaultModel,
            Messages =
            [
                new ChatMessage(ChatRole.User,
                [
                    new ChatPart.Image("", first),
                    new ChatPart.Text("中间"),
                    new ChatPart.Image("image/jpeg", second),
                ]),
            ],
        };

        await TestHarness.CollectAsync(handler, request);

        using var body = JsonDocument.Parse(handler.LastRequest.Body!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(first)}",
            content[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Equal("中间", content[1].GetProperty("text").GetString());
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(second)}",
            content[2].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Request_FileRefIsIgnoredWithoutThrowing()
    {
        var handler = OkHandler();
        var request = new ChatRequest
        {
            Model = OpenCodeGoProvider.DefaultModel,
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
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("你好", content.GetString());
    }

    [Fact]
    public void Descriptor_DeclaresImageInputButNotFileUpload()
    {
        var capabilities = new OpenCodeGoProvider().Descriptor.Capabilities;
        Assert.True(capabilities.HasFlag(ProviderCapabilities.ImageInput));
        Assert.False(capabilities.HasFlag(ProviderCapabilities.FileUpload));
    }
}