using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chat.Core;
using Chat.Storage;

namespace Chat.Gateway;

public sealed record StoredSession(Session Session, IReadOnlyList<MessageNode> Messages);

public static class SessionJson
{
    public static object ToSummary(StoredSession stored) => new
    {
        id = stored.Session.Id,
        title = stored.Session.Title,
        createdAt = stored.Session.CreatedAt.ToUnixTimeMilliseconds(),
        updatedAt = stored.Session.UpdatedAt.ToUnixTimeMilliseconds(),
        messageCount = stored.Messages.Count,
    };

    public static JsonObject ToClient(StoredSession stored)
    {
        var messages = stored.Messages.OrderBy(message => message.Id).ToArray();
        var byId = messages.ToDictionary(message => message.Id);
        var root = messages.FirstOrDefault(message => message.ParentId is null || !byId.ContainsKey(message.ParentId.Value))?.Id;
        var current = stored.Session.CurrentMessageId is { } currentId && byId.ContainsKey(currentId)
            ? currentId
            : messages.Length == 0 ? (long?)null : messages.Max(message => message.Id);

        var children = messages
            .Where(message => message.ParentId is { } parent && byId.ContainsKey(parent))
            .GroupBy(message => message.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(message => message.Id).ToArray());
        var selected = new Dictionary<long, long>();
        if (current is { } cursor && root is { } rootId)
        {
            var path = new List<long>();
            var visited = new HashSet<long>();
            var node = cursor;
            while (visited.Add(node) && byId.TryGetValue(node, out var currentNode))
            {
                path.Add(node);
                if (node == rootId) break;
                if (currentNode.ParentId is not { } parent || !byId.ContainsKey(parent)) break;
                node = parent;
            }
            for (var index = path.Count - 2; index >= 0; index--) selected[path[index + 1]] = path[index];
        }

        var result = new JsonObject
        {
            ["id"] = stored.Session.Id,
            ["title"] = stored.Session.Title,
            ["createdAt"] = stored.Session.CreatedAt.ToUnixTimeMilliseconds(),
            ["updatedAt"] = stored.Session.UpdatedAt.ToUnixTimeMilliseconds(),
            ["rootMessageId"] = root?.ToString(CultureInfo.InvariantCulture),
        };
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var item = new JsonObject
            {
                ["id"] = message.Id.ToString(CultureInfo.InvariantCulture),
                ["createdAt"] = message.CreatedAt.ToUnixTimeMilliseconds(),
            };
            if (message.ParentId is { } parent && byId.ContainsKey(parent))
                item["parentId"] = parent.ToString(CultureInfo.InvariantCulture);
            if (selected.TryGetValue(message.Id, out var child))
                item["selectedChildId"] = child.ToString(CultureInfo.InvariantCulture);
            if (children.TryGetValue(message.Id, out var siblings) && siblings.Length > 1)
                item["childIds"] = new JsonArray(siblings.Select(id => (JsonNode?)id.ToString(CultureInfo.InvariantCulture)).ToArray());
            if (message.Role == MessageRole.User)
            {
                item["role"] = "user";
                item["content"] = string.Join("\n", message.Parts
                    .Where(part => part.Kind is PartKind.Text or PartKind.Raw)
                    .Select(part => part.Text ?? "")
                    .Where(text => text.Length > 0));
                var files = new JsonArray();
                foreach (var part in message.Parts.Where(part => part.Kind == PartKind.File))
                {
                    var info = ReadFileMeta(part.MetaJson);
                    files.Add(new JsonObject
                    {
                        ["id"] = info.FileId,
                        ["serverId"] = string.IsNullOrEmpty(info.Sha256) ? null : info.Sha256,
                        ["name"] = info.Name,
                        ["size"] = info.Size,
                        ["isImage"] = info.IsImage,
                        ["mimeType"] = info.Mime,
                    });
                }
                item["files"] = files;
            }
            else
            {
                item["role"] = "assistant";
                item["status"] = ToClientStatus(message);
                item["model"] = message.Model;
                var blocks = new JsonArray();
                foreach (var part in message.Parts)
                {
                    switch (part.Kind)
                    {
                        case PartKind.Thinking:
                            blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = part.Text ?? "" });
                            break;
                        case PartKind.Text:
                            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = part.Text ?? "" });
                            break;
                        case PartKind.ToolCall:
                        case PartKind.ToolResult:
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "server_tool_use",
                                ["content"] = part.Text ?? "",
                            });
                            break;
                    }
                }
                item["blocks"] = blocks;
                item["prefix"] = DeterminePrefix(message);
                if (message.Parts.Any(part => part.Kind == PartKind.Tip))
                    item["stopReason"] = "refusal";
            }
            array.Add(item);
        }
        result["messages"] = array;
        return result;
    }

    private static (string FileId, string Name, string Mime, long Size, string? Sha256, bool IsImage) ReadFileMeta(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
            return ("", "file", "application/octet-stream", 0, null, false);
        try
        {
            using var document = JsonDocument.Parse(metaJson);
            var root = document.RootElement;
            string Read(params string[] names)
            {
                foreach (var name in names)
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString() ?? "";
                return "";
            }
            long ReadSize()
            {
                foreach (var name in new[] { "size", "fileSize", "file_size" })
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                        return number;
                return 0;
            }
            bool ReadBool()
            {
                foreach (var name in new[] { "isImage", "is_image" })
                    if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return value.GetBoolean();
                return false;
            }
            var sha = Read("sha256", "hash");
            return (
                Read("fileId", "file_id"),
                Read("name", "file_name", "fileName"),
                Read("mime", "mimeType", "mime_type"),
                ReadSize(),
                string.IsNullOrEmpty(sha) ? null : sha,
                ReadBool());
        }
        catch (JsonException)
        {
            return ("", "file", "application/octet-stream", 0, null, false);
        }
    }

    private static string DeterminePrefix(MessageNode message)
    {
        if (message.Status == MessageStatus.ContentFilter) return "refusal";
        if (message.Parts.All(part => part.Kind == PartKind.Thinking)) return "final";
        return "final";
    }

    private static string ToClientStatus(MessageNode message) => message.Status switch
    {
        MessageStatus.Finished => "done",
        MessageStatus.Wip or MessageStatus.Incomplete => "stopped",
        MessageStatus.Failed or MessageStatus.Timeout or MessageStatus.ContextLengthExceeded => "error",
        MessageStatus.ContentFilter => "done",
        _ => "done",
    };
}