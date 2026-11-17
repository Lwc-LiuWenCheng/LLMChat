using Chat.Core;
using Xunit;

namespace Chat.Core.Tests;

public class SessionTreeTests
{
    private static MessageNode Node(long id, long? parent, MessageRole role, string text) => new()
    {
        Id = id,
        ParentId = parent,
        Role = role,
        Parts = [ContentPart.OfText(text)],
    };

    private static readonly MessageNode[] Tree =
    [
        Node(1, null, MessageRole.User, "q1"),
        Node(2, 1, MessageRole.Assistant, "a1"),
        Node(3, 1, MessageRole.Assistant, "a1-regenerated"),
        Node(4, 3, MessageRole.User, "q2"),
        Node(5, 4, MessageRole.Assistant, "a2"),
    ];

    [Fact]
    public void PathTo_ReturnsRootToNodeInOrder()
    {
        var path = SessionTree.PathTo(Tree, 5);
        Assert.Equal(new long[] { 1, 3, 4, 5 }, path.Select(n => n.Id).ToArray());
    }

    [Fact]
    public void PathTo_DetectsCycleWithoutHanging()
    {
        MessageNode[] cyclic = [Node(1, 2, MessageRole.User, "a"), Node(2, 1, MessageRole.Assistant, "b")];
        var path = SessionTree.PathTo(cyclic, 1);
        Assert.Equal(2, path.Count);
    }

    [Fact]
    public void ChildrenOf_ReturnsBranchesOrderedById()
    {
        var children = SessionTree.ChildrenOf(Tree, 1);
        Assert.Equal(new long[] { 2, 3 }, children.Select(n => n.Id).ToArray());
    }

    [Fact]
    public void BranchPosition_ReportsIndexAndCount()
    {
        var (index, count) = SessionTree.BranchPosition(Tree, Tree[2]);
        Assert.Equal(2, index);
        Assert.Equal(2, count);
    }

    [Fact]
    public void RegenerateParent_FromAssistant_ReturnsItsQuestion()
    {
        var path = SessionTree.PathTo(Tree, 5);
        Assert.Equal(4L, SessionTree.RegenerateParent(path));
    }

    [Fact]
    public void RegenerateParent_FromUser_ReturnsThatUser()
    {
        var path = SessionTree.PathTo(Tree, 4);
        Assert.Equal(4L, SessionTree.RegenerateParent(path));
    }

    [Fact]
    public void DeriveTitle_UsesFirstUserMessageAndTruncates()
    {
        var title = SessionTree.DeriveTitle(Tree, 5);
        Assert.Equal("q1", title);

        var longNode = Node(9, null, MessageRole.User, new string('x', 60));
        Assert.Equal(new string('x', 40) + "…", SessionTree.DeriveTitle([longNode], 9));
    }
}
