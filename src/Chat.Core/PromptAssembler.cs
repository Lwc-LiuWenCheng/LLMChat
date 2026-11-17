using Chat.Abstractions;

namespace Chat.Core;

/// <summary>
/// 把"会话 + 当前分支路径"组装成 provider 请求。跨厂商切换时走这里，
/// 因此不允许出现任何厂商专有字段；厂商私有数据只以 <see cref="ChatPart.Raw"/> 原样回放。
/// </summary>
public static class PromptAssembler
{
    public static ChatRequest Build(
        Session session,
        IReadOnlyList<MessageNode> activePath,
        string? modelOverride = null,
        int? maxTokens = null,
        double? temperature = null,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var messages = new List<ChatMessage>();
        foreach (var node in activePath)
        {
            // 失败/被过滤的节点不进入上下文；用户的 WIP 也不会存在。
            if (node.Role == MessageRole.Assistant && node.Status != MessageStatus.Finished)
                continue;

            var parts = ToParts(node);
            if (parts.Count == 0) continue;
            messages.Add(new ChatMessage(
                node.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant,
                parts));
        }

        return new ChatRequest
        {
            Model = modelOverride ?? session.Model ?? "deepseek-flash",
            Messages = messages,
            System = session.SystemPrompt,
            MaxTokens = maxTokens,
            Temperature = temperature,
            Extra = extra,
        };
    }

    public static IReadOnlyList<ChatPart> ToParts(MessageNode node)
    {
        var parts = new List<ChatPart>();
        foreach (var part in node.Parts)
        {
            switch (part.Kind)
            {
                case PartKind.Text when part.Text is { Length: > 0 } text:
                    parts.Add(new ChatPart.Text(text));
                    break;
                case PartKind.ToolCall when part.MetaJson is { } meta && TryReadToolMeta(meta, out var callId, out var arguments):
                    parts.Add(new ChatPart.ToolCall(callId, part.Text ?? "", arguments));
                    break;
                case PartKind.ToolResult when part.MetaJson is { } meta && TryReadToolResultMeta(meta, out var callId, out var isError):
                    parts.Add(new ChatPart.ToolResult(callId, part.Text ?? "", isError));
                    break;
                case PartKind.Raw when part.Text is { Length: > 0 } raw:
                    parts.Add(new ChatPart.Raw(node.ProviderId ?? "unknown", raw));
                    break;
            }
        }

        return parts;
    }

    private static bool TryReadToolMeta(string meta, out string callId, out string arguments)
    {
        callId = "";
        arguments = "{}";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(meta);
            callId = doc.RootElement.TryGetProperty("callId", out var id) ? id.GetString() ?? "" : "";
            arguments = doc.RootElement.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
            return callId.Length > 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool TryReadToolResultMeta(string meta, out string callId, out bool isError)
    {
        callId = "";
        isError = false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(meta);
            callId = doc.RootElement.TryGetProperty("callId", out var id) ? id.GetString() ?? "" : "";
            isError = doc.RootElement.TryGetProperty("isError", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.True;
            return callId.Length > 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
