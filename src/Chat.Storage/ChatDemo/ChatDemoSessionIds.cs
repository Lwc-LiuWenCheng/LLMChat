using System.Text.Json;

namespace Chat.Storage.ChatDemo;

/// <summary>
/// 会话 id 的单一解析规则，预检与导入共用：
/// 会话 JSON 内的 id → <c>chat_session_index.json</c> 中按顺序对应的 id → 文件名（去扩展名）。
/// 官方 ZIP 的文件名不一定等于会话 id；索引只有在条目与会话文件一一对应时才参与回退，
/// 否则宁可使用文件名，避免把索引里的其他会话 id 错配给当前文件。
/// </summary>
public static class ChatDemoSessionIds
{
    private static readonly string[] IdPropertyNames = ["id", "session_id", "sessionId", "conversation_id"];

    private static readonly string[] IndexListPropertyNames = ["sessions", "items", "data", "list"];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// 读取会话 JSON 根节点（或其中嵌套的 <c>session</c> 对象）里的 id；缺失或不是对象时返回 null。
    /// </summary>
    public static string? ReadSessionJsonId(JsonElement root)
    {
        var element = root;
        if (root.ValueKind == JsonValueKind.Object &&
            ChatDemoJson.GetProperty(root, "session") is { ValueKind: JsonValueKind.Object } nested)
        {
            element = nested;
        }

        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = ChatDemoJson.GetString(element, IdPropertyNames);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// 读取索引里的会话 id，并保持与索引条目一一对应的位置（无法解析的槽位用空串占位）。
    /// 这样 <see cref="Resolve"/> 只在索引条目数与 ZIP 会话文件数相等时按顺序回退。
    /// </summary>
    public static IReadOnlyList<string> ReadIndexIds(string? indexJson)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(indexJson)) return ids;

        try
        {
            using var document = JsonDocument.Parse(indexJson, DocumentOptions);
            var root = document.RootElement;
            JsonElement? list = null;
            if (root.ValueKind == JsonValueKind.Array) list = root;
            else if (root.ValueKind == JsonValueKind.Object &&
                     ChatDemoJson.GetProperty(root, IndexListPropertyNames) is { ValueKind: JsonValueKind.Array } property)
                list = property;

            if (list is not { } items) return ids;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    ids.Add("");
                    continue;
                }

                var id = ChatDemoJson.GetString(item, IdPropertyNames);
                ids.Add(string.IsNullOrWhiteSpace(id) ? "" : id);
            }
        }
        catch (JsonException)
        {
            // 索引只是回退来源；损坏时返回空列表，由调用方使用文件名。
        }

        return ids;
    }

    /// <summary>
    /// 按统一顺序解析会话 id：JSON 内 id → 索引按顺序对应的 id（仅数量一一对应时）→ 文件名。
    /// </summary>
    public static string Resolve(
        string? sessionJsonId,
        IReadOnlyList<string> indexIds,
        int entryIndex,
        int entryCount,
        string inferredSessionId)
    {
        ArgumentNullException.ThrowIfNull(indexIds);
        if (!string.IsNullOrWhiteSpace(sessionJsonId)) return sessionJsonId;

        if (entryIndex >= 0 &&
            entryIndex < entryCount &&
            indexIds.Count == entryCount &&
            !string.IsNullOrWhiteSpace(indexIds[entryIndex]))
        {
            return indexIds[entryIndex];
        }

        return inferredSessionId;
    }
}
