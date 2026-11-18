using System.Text;
using System.Text.Json;
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
        IReadOnlyDictionary<string, string>? extra = null,
        AttachmentReader? attachmentReader = null)
    {
        var messages = new List<ChatMessage>();
        foreach (var node in activePath)
        {
            // 失败/被过滤的节点不进入上下文；用户的 WIP 也不会存在。
            if (node.Role == MessageRole.Assistant && node.Status != MessageStatus.Finished)
                continue;

            var parts = ToParts(node, attachmentReader);
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

    public static IReadOnlyList<ChatPart> ToParts(MessageNode node, AttachmentReader? attachmentReader = null)
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
                case PartKind.Image:
                    parts.Add(BuildImagePart(part, attachmentReader));
                    break;
                case PartKind.File:
                    parts.Add(BuildFilePart(part, attachmentReader));
                    break;
            }
        }

        return parts;
    }

    private static ChatPart BuildImagePart(ContentPart part, AttachmentReader? reader)
    {
        var meta = ReadAttachmentMeta(part.MetaJson);
        var bytes = meta.RelativePath is { Length: > 0 } && reader is not null
            ? TryRead(meta.RelativePath, reader)
            : null;
        if (bytes is { Length: > 0 })
            return new ChatPart.Image(NormalizeImageMime(meta.Mime, meta.IsImage), bytes);

        var name = string.IsNullOrWhiteSpace(meta.Name) ? "图片" : meta.Name!;
        return new ChatPart.Text($"[图片：{name}]（附件字节不可用，已在上下文保留占位）");
    }

    private static ChatPart BuildFilePart(ContentPart part, AttachmentReader? reader)
    {
        var meta = ReadAttachmentMeta(part.MetaJson);
        var name = string.IsNullOrWhiteSpace(meta.Name) ? "附件" : meta.Name!;
        var mime = string.IsNullOrWhiteSpace(meta.Mime)
            ? InferMimeFromName(name) ?? "application/octet-stream"
            : meta.Mime!;
        var bytes = meta.RelativePath is { Length: > 0 } && reader is not null
            ? TryRead(meta.RelativePath, reader)
            : null;

        if (bytes is { Length: > 0 } && IsTextMime(mime, name, bytes))
        {
            // 解码失败时回退占位，绝不让单个坏附件影响整条上下文。
            try
            {
                return new ChatPart.Text($"[{name}]\n{Encoding.UTF8.GetString(bytes)}");
            }
            catch (DecoderFallbackException)
            {
                return new ChatPart.Text($"[{name}]（附件解码失败，原始字节 {bytes.Length} 字节）");
            }
        }

        var sizeText = meta.Size is { } size ? $"，{size} 字节" : "";
        return new ChatPart.Text($"[{name} ({mime})]{sizeText}（二进制附件，当前接口不支持内联，请在外接客户端查看）");
    }

    private static byte[]? TryRead(string relativePath, AttachmentReader reader)
    {
        try
        {
            return reader(relativePath);
        }
        catch (Exception)
        {
            // reader 由宿主注入；单个附件读取失败不能中断请求组装。
            return null;
        }
    }

    private static string NormalizeImageMime(string? mime, bool? isImage)
    {
        if (!string.IsNullOrWhiteSpace(mime)) return mime.Split(';')[0].Trim();
        return isImage == true ? "image/jpeg" : "image/png";
    }

    private static string? InferMimeFromName(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".md" or ".markdown" => "text/markdown",
            ".txt" or ".log" or ".csv" => "text/plain",
            ".json" or ".jsonl" or ".ndjson" => "application/json",
            ".xml" => "application/xml",
            ".js" or ".ts" => "application/javascript",
            ".yaml" or ".yml" => "application/x-yaml",
            ".sql" => "application/sql",
            ".html" or ".htm" => "text/html",
            _ => null,
        };
    }

    private static bool IsTextMime(string mime, string name, byte[] bytes)
    {
        // NUL 字节是二进制内容的强信号：即便扩展名像文本也不要把乱码塞进上下文。
        if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
        var normalized = mime.Split(';')[0].Trim().ToLowerInvariant();
        if (normalized.StartsWith("text/", StringComparison.Ordinal)) return true;
        if (normalized is "application/json" or "application/xml" or "application/javascript"
            or "application/x-javascript" or "application/x-ndjson" or "application/x-yaml"
            or "application/yaml" or "application/sql" or "application/x-sh"
            or "application/x-httpd-php")
            return true;
        if (normalized.EndsWith("+json", StringComparison.Ordinal) ||
            normalized.EndsWith("+xml", StringComparison.Ordinal)) return true;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".md" or ".markdown" or ".txt" or ".csv" or ".log" or ".json"
            or ".xml" or ".js" or ".ts" or ".yaml" or ".yml" or ".sql" or ".html" or ".htm";
    }

    private readonly record struct AttachmentMeta(
        string? Name,
        string? Mime,
        string? RelativePath,
        long? Size,
        bool? IsImage);

    private static AttachmentMeta ReadAttachmentMeta(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return default;
        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            return new AttachmentMeta(
                GetString(root, "name", "fileName", "file_name", "filename"),
                GetString(root, "mime", "mimeType", "mime_type", "content_type", "contentType"),
                GetString(root, "relPath", "rel_path", "path", "relativePath"),
                GetInt64(root, "size", "fileSize", "file_size"),
                GetBoolean(root, "isImage", "is_image"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(root, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
            // 兼容 "original" 内嵌的官方原始片段。
            if (root.TryGetProperty("original", out var original) &&
                original.ValueKind == JsonValueKind.Object &&
                TryGet(original, name, out var nested) && nested.ValueKind == JsonValueKind.String)
                return nested.GetString();
        }
        return null;
    }

    private static long? GetInt64(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(root, name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var number)) return number;
        }
        return null;
    }

    private static bool? GetBoolean(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(root, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            if (root.TryGetProperty("original", out var original) &&
                original.ValueKind == JsonValueKind.Object &&
                TryGet(original, name, out var nested) && nested.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return nested.GetBoolean();
        }
        return null;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        var current = element;
        foreach (var part in name.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                value = default;
                return false;
            }
        }
        value = current;
        return true;
    }

    private static bool TryReadToolMeta(string meta, out string callId, out string arguments)
    {
        callId = "";
        arguments = "{}";
        try
        {
            using var doc = JsonDocument.Parse(meta);
            callId = doc.RootElement.TryGetProperty("callId", out var id) ? id.GetString() ?? "" : "";
            arguments = doc.RootElement.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
            return callId.Length > 0;
        }
        catch (JsonException)
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
            using var doc = JsonDocument.Parse(meta);
            callId = doc.RootElement.TryGetProperty("callId", out var id) ? id.GetString() ?? "" : "";
            isError = doc.RootElement.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            return callId.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
