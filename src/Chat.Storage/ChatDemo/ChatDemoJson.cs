using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chat.Storage.ChatDemo;

/// <summary>
/// chat-demo 字段的容错读取。官方导出字段命名不完全稳定（snake_case / camelCase 混用），
/// 所有读取都按候选名依次尝试，缺失就返回 null，由调用方决定兜底值。
/// </summary>
internal static class ChatDemoJson
{
    private static readonly string[] ContentPropertyNames =
        ["content", "text", "value", "result", "body", "data", "message"];

    public static JsonElement? GetProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)) return value;
        }
        return null;
    }

    public static string? GetString(JsonElement element, params string[] names)
    {
        if (GetProperty(element, names) is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    public static long? GetInt64(JsonElement element, params string[] names)
    {
        if (GetProperty(element, names) is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    public static bool? GetBoolean(JsonElement element, params string[] names)
    {
        if (GetProperty(element, names) is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number != 0 : null,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    public static DateTimeOffset? GetTimestamp(JsonElement element, params string[] names)
    {
        if (GetProperty(element, names) is not { } value) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (DateTimeOffset.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                    return parsed;
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)) return FromUnix(numeric);
                return null;
            case JsonValueKind.Number:
                return value.TryGetInt64(out var number) ? FromUnix(number) : null;
            default:
                return null;
        }
    }

    /// <summary>把片段的 content 读成文本；content 可能是字符串、数组或嵌套对象。</summary>
    public static string? GetContentText(JsonElement? element, int depth = 0)
    {
        if (element is not { } value || depth > 8) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return value.GetRawText();
            case JsonValueKind.Object:
                foreach (var name in ContentPropertyNames)
                {
                    if (!value.TryGetProperty(name, out var inner)) continue;
                    var text = GetContentText(inner, depth + 1);
                    if (text is not null) return text;
                }
                return value.GetRawText();
            case JsonValueKind.Array:
                var pieces = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    var text = GetContentText(item, depth + 1);
                    if (!string.IsNullOrEmpty(text)) pieces.Add(text);
                }
                return pieces.Count == 0 ? null : string.Join("\n", pieces);
            default:
                return null;
        }
    }

    /// <summary>读取 FILE 片段/本地附件元数据里的文件信息。</summary>
    public static ChatDemoFileInfo ReadFileInfo(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return new ChatDemoFileInfo(null, null, null, null, null, null, null);
        try
        {
            using var document = JsonDocument.Parse(metaJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ChatDemoFileInfo(null, null, null, null, null, null, null);
            return new ChatDemoFileInfo(
                GetString(root, "fileId", "file_id"),
                GetString(root, "name", "file_name", "fileName"),
                GetString(root, "mime", "mimeType", "mime_type"),
                GetInt64(root, "size", "fileSize", "file_size"),
                GetString(root, "sha256", "hash"),
                GetString(root, "relPath", "rel_path", "path"),
                GetBoolean(root, "isImage", "is_image"));
        }
        catch (JsonException)
        {
            return new ChatDemoFileInfo(null, null, null, null, null, null, null);
        }
    }

    /// <summary>构造 FILE 片段/本地附件元数据（字段顺序固定，保证重复导入幂等）。</summary>
    public static string BuildFileMeta(
        string? originalJson,
        string? fileId,
        string? name,
        string? mime,
        long? size,
        string? sha256,
        string? relativePath,
        bool? isImage = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("fragmentType", "FILE");
            if (!string.IsNullOrEmpty(fileId)) writer.WriteString("fileId", fileId);
            if (!string.IsNullOrEmpty(name)) writer.WriteString("name", name);
            if (!string.IsNullOrEmpty(mime)) writer.WriteString("mime", mime);
            if (size is not null) writer.WriteNumber("size", size.Value);
            if (!string.IsNullOrEmpty(sha256)) writer.WriteString("sha256", sha256);
            if (!string.IsNullOrEmpty(relativePath)) writer.WriteString("relPath", relativePath);
            if (isImage is not null) writer.WriteBoolean("isImage", isImage.Value);
            if (!string.IsNullOrWhiteSpace(originalJson))
            {
                writer.WritePropertyName("original");
                try
                {
                    using var document = JsonDocument.Parse(originalJson);
                    document.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    writer.WriteNullValue();
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>构造非 FILE 片段（工具结果/风险/未知类型）的元数据，保留官方原始片段。</summary>
    public static string BuildFragmentMeta(string? fragmentType, string? originalJson)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(fragmentType)) writer.WriteString("fragmentType", fragmentType);
            if (!string.IsNullOrWhiteSpace(originalJson))
            {
                writer.WritePropertyName("original");
                try
                {
                    using var document = JsonDocument.Parse(originalJson);
                    document.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    writer.WriteNullValue();
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>元数据里保存的官方原始片段（导出时原样写回）。</summary>
    public static string? ReadOriginalFragment(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metaJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("original", out var original) &&
                original.ValueKind == JsonValueKind.Object)
                return original.GetRawText();
        }
        catch (JsonException)
        {
            // 损坏的元数据按没有处理。
        }
        return null;
    }

    /// <summary>把元数据里的原始片段原样写进导出 JSON；没有原始片段时返回 false。</summary>
    public static bool TryWriteOriginalFragment(Utf8JsonWriter writer, string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(metaJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("original", out var original) || original.ValueKind != JsonValueKind.Object) return false;
            original.WriteTo(writer);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTimeOffset? FromUnix(long value)
    {
        if (value <= 0) return null;
        try
        {
            return value >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

internal sealed record ChatDemoFileInfo(
    string? FileId,
    string? Name,
    string? Mime,
    long? Size,
    string? Sha256,
    string? RelativePath,
    bool? IsImage);
