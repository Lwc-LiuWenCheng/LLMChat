using Chat.Abstractions;
using Chat.Core;
using Xunit;

namespace Chat.Core.Tests;

public class PromptAssemblerTests
{
    [Fact]
    public void Build_MapsRolesAndSystemPrompt()
    {
        var session = new Session { Id = "s1", Model = "deepseek-flash", SystemPrompt = "be brief" };
        MessageNode[] path =
        [
            new() { Id = 1, ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("hi")] },
            new() { Id = 2, ParentId = 1, Role = MessageRole.Assistant, Parts = [ContentPart.OfText("hello")] },
        ];

        var request = PromptAssembler.Build(session, path);

        Assert.Equal("deepseek-flash", request.Model);
        Assert.Equal("be brief", request.System);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(ChatRole.User, request.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, request.Messages[1].Role);
    }

    [Fact]
    public void Build_SkipsFailedAssistantButKeepsUser()
    {
        var session = new Session { Id = "s1" };
        MessageNode[] path =
        [
            new() { Id = 1, ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("hi")] },
            new() { Id = 2, ParentId = 1, Role = MessageRole.Assistant, Status = MessageStatus.Failed, Parts = [ContentPart.OfText("boom")] },
        ];

        var request = PromptAssembler.Build(session, path);

        Assert.Single(request.Messages);
        Assert.Equal(ChatRole.User, request.Messages[0].Role);
    }

    [Fact]
    public void Build_ExcludesThinkingFromContext()
    {
        var session = new Session { Id = "s1" };
        MessageNode[] path =
        [
            new()
            {
                Id = 1,
                ParentId = null,
                Role = MessageRole.Assistant,
                Parts = [ContentPart.OfThinking("internal"), ContentPart.OfText("visible")],
            },
        ];

        var request = PromptAssembler.Build(session, path);

        var parts = request.Messages.Single().Parts;
        Assert.Single(parts);
        Assert.IsType<ChatPart.Text>(parts[0]);
    }

    [Fact]
    public void Build_MapsToolPartsRoundTrip()
    {
        var session = new Session { Id = "s1" };
        MessageNode[] path =
        [
            new()
            {
                Id = 1,
                ParentId = null,
                Role = MessageRole.Assistant,
                ProviderId = "chat.provider.deepseek",
                Parts =
                [
                    ContentPart.OfText("calling"),
                    ContentPart.ToolCall("call_1", "search", "{\"q\":\"x\"}"),
                    ContentPart.ToolResult("call_1", "result"),
                ],
            },
        ];

        var parts = PromptAssembler.Build(session, path).Messages.Single().Parts;

        var call = Assert.IsType<ChatPart.ToolCall>(parts[1]);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("search", call.Name);
        Assert.Contains("\"q\"", call.ArgumentsJson);
        Assert.IsType<ChatPart.ToolResult>(parts[2]);
    }
}
