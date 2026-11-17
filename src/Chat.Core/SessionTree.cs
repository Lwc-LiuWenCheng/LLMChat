namespace Chat.Core;

/// <summary>
/// 消息树的纯函数操作：给定全部节点 + 当前节点，算出路径、兄弟分支、重生成目标。
/// 不做 I/O，便于单测；仓储只负责读写。
/// </summary>
public static class SessionTree
{
    /// <summary>根到 <paramref name="nodeId"/> 的路径。节点缺失时返回空。</summary>
    public static IReadOnlyList<MessageNode> PathTo(IEnumerable<MessageNode> nodes, long? nodeId)
    {
        if (nodeId is null) return [];
        var map = nodes.ToDictionary(n => n.Id);
        var path = new List<MessageNode>();
        var cursor = nodeId;
        var guard = new HashSet<long>();
        while (cursor is { } id && map.TryGetValue(id, out var node) && guard.Add(id))
        {
            path.Add(node);
            cursor = node.ParentId;
        }
        path.Reverse();
        return path;
    }

    /// <summary>某节点的直接子节点（即同一问下的多个回答分支）。按 id 升序。</summary>
    public static IReadOnlyList<MessageNode> ChildrenOf(IEnumerable<MessageNode> nodes, long? parentId) =>
        nodes.Where(n => n.ParentId == parentId).OrderBy(n => n.Id).ToList();

    /// <summary>当前节点在兄弟分支中的位置（1 起）与总数。</summary>
    public static (int Index, int Count) BranchPosition(IEnumerable<MessageNode> nodes, MessageNode node)
    {
        var siblings = ChildrenOf(nodes, node.ParentId);
        var index = siblings.ToList().FindIndex(n => n.Id == node.Id);
        return (index < 0 ? 0 : index + 1, siblings.Count);
    }

    /// <summary>
    /// 计算"重新生成"应挂载的父节点：当前节点是助手回答时回到它的提问，
    /// 当前节点是提问时直接复用该提问作为父节点。
    /// </summary>
    public static long? RegenerateParent(IReadOnlyList<MessageNode> path)
    {
        if (path.Count == 0) return null;
        var last = path[^1];
        return last.Role == MessageRole.Assistant ? last.ParentId : last.Id;
    }

    /// <summary>
    /// 计算"编辑提问"后的兄弟节点父节点：被编辑的提问节点的父节点。
    /// </summary>
    public static long? EditParent(IReadOnlyList<MessageNode> path)
    {
        if (path.Count == 0) return null;
        return path[^1].ParentId;
    }

    /// <summary>会话里当前可见的末节点文本，用于标题兜底。</summary>
    public static string DeriveTitle(IEnumerable<MessageNode> nodes, long? currentId)
    {
        var firstUser = PathTo(nodes, currentId).FirstOrDefault(n => n.Role == MessageRole.User);
        var text = firstUser?.PlainText.Trim();
        if (string.IsNullOrEmpty(text)) return "新会话";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return line.Length <= 40 ? line : line[..40] + "…";
    }
}
