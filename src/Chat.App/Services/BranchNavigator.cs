using Chat.Core;

namespace Chat.App.Services;

/// <summary>
/// 分支导航的纯函数：同一提问下的多个回答、被编辑出的多个提问都算分支。
/// 停在“路径上最深的有兄弟节点”那一层，并把目标分支下钻到最新末节点。
/// </summary>
public static class BranchNavigator
{
    /// <summary>路径上最深的有多个兄弟的节点；没有分支返回 null。</summary>
    public static MessageNode? FindBranchNode(IReadOnlyList<MessageNode> nodes, long? currentId)
    {
        var path = SessionTree.PathTo(nodes, currentId);
        for (var index = path.Count - 1; index >= 0; index--)
        {
            if (SessionTree.ChildrenOf(nodes, path[index].ParentId).Count > 1) return path[index];
        }

        return null;
    }

    /// <summary>从给定节点沿最后一个子节点下钻到叶子（切分支后展示该分支最新回答）。</summary>
    public static long DescendToLatest(IReadOnlyList<MessageNode> nodes, MessageNode node)
    {
        var current = node;
        var guard = new HashSet<long>();
        while (guard.Add(current.Id))
        {
            var children = SessionTree.ChildrenOf(nodes, current.Id);
            if (children.Count == 0) return current.Id;
            current = children[^1];
        }

        return current.Id;
    }
}
