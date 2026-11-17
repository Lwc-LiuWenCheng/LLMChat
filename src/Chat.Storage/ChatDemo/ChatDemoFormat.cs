using System.Text;
using System.Text.Json;
using Chat.Core;

namespace Chat.Storage.ChatDemo;

/// <summary>ZIP 结构或字段不合法（含 zip slip）时抛出。</summary>
public sealed class ChatDemoFormatException : Exception
{
    public ChatDemoFormatException(string message) : base(message) { }

    public ChatDemoFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// chat-demo 官方导出（<c>export_format_version=3</c>）的格式约定：entry 名、状态/角色/片段映射、
/// entry 名安全校验。读取端容错（字段缺失不崩），写入端严格按官方顺序产出。
/// </summary>
public static class ChatDemoFormat
{
    public const int SupportedExportFormatVersion = 3;

    public const string ManifestEntryName = "manifest.json";
    public const string IndexEntryName = "chat_session_index.json";
    public const string SessionsRoot = "chat_sessions";
    public const string FilesFolderSuffix = "_files";

    /// <summary>导出时写入的本地片段种类标记（官方类型无法表达 ToolCall/Image/Raw 等差异）。</summary>
    public const string KindPropertyName = "llmchat_kind";

    /// <summary>导出时原样写回的本地片段元数据（JSON 对象；无法解析时写成字符串）。</summary>
    public const string MetaPropertyName = "llmchat_meta";

    private static readonly HashSet<string> OfficialStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WIP",
        "FINISHED",
        "CONTENT_FILTER",
        "FAILED",
        "INCOMPLETE",
        "CONTEXT_LENGTH_EXCEEDED",
        "TIMEOUT",
    };

    /// <summary>官方 status → 本地状态。无法识别时返回 Finished 并置 recognized=false。</summary>
    public static MessageStatus MapStatus(string? rawStatus, out bool recognized)
    {
        if (TryMapStatus(rawStatus, out var status))
        {
            recognized = true;
            return status;
        }

        recognized = false;
        return MessageStatus.Finished;
    }

    /// <summary>
    /// 状态映射：FINISHED→Finished；WIP|INCOMPLETE→Incomplete；FAILED|CONTEXT_LENGTH_EXCEEDED|TIMEOUT→Failed；
    /// CONTENT_FILTER→ContentFilter。原始 status 由导入端原样保存在消息 raw_json 里。
    /// </summary>
    public static bool TryMapStatus(string? rawStatus, out MessageStatus status)
    {
        status = MessageStatus.Finished;
        if (string.IsNullOrWhiteSpace(rawStatus)) return false;
        switch (rawStatus.Trim().ToUpperInvariant())
        {
            case "FINISHED":
            case "FINISH":
            case "DONE":
                status = MessageStatus.Finished;
                return true;
            case "WIP":
            case "INCOMPLETE":
            case "IN_PROGRESS":
            case "INTERRUPTED":
                status = MessageStatus.Incomplete;
                return true;
            case "FAILED":
            case "CONTEXT_LENGTH_EXCEEDED":
            case "TIMEOUT":
            case "ERROR":
                status = MessageStatus.Failed;
                return true;
            case "CONTENT_FILTER":
            case "CONTENT_FILTERED":
                status = MessageStatus.ContentFilter;
                return true;
            default:
                return false;
        }
    }

    /// <summary>本地状态 → 官方 status。导入过的消息若 raw_json 保留了原始 status，优先写回原始值。</summary>
    public static string ToExportStatus(MessageStatus status, string? rawJson = null)
    {
        if (TryReadOriginalStatus(rawJson, status, out var original)) return original;
        return status switch
        {
            MessageStatus.Wip => "WIP",
            MessageStatus.Finished => "FINISHED",
            MessageStatus.ContentFilter => "CONTENT_FILTER",
            MessageStatus.Failed => "FAILED",
            MessageStatus.Incomplete => "INCOMPLETE",
            MessageStatus.ContextLengthExceeded => "CONTEXT_LENGTH_EXCEEDED",
            MessageStatus.Timeout => "TIMEOUT",
            _ => "FINISHED",
        };
    }

    /// <summary>
    /// 从导入时原样保存的消息 JSON 里取原始 status。只有原始 status 与当前状态映射一致时才采用，
    /// 避免本地改过状态后导出仍写旧值。
    /// </summary>
    public static bool TryReadOriginalStatus(string? rawJson, MessageStatus currentStatus, out string originalStatus)
    {
        originalStatus = "";
        if (string.IsNullOrWhiteSpace(rawJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.String) return false;
            var raw = statusElement.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var normalized = raw.Trim().ToUpperInvariant();
            if (!OfficialStatuses.Contains(normalized)) return false;
            // 只认 chat-demo 消息形状，避免把厂商原始响应里的同名字段误当导出状态。
            if (!root.TryGetProperty("role", out _) &&
                !root.TryGetProperty("fragments", out _) &&
                !root.TryGetProperty("parts", out _)) return false;
            if (!TryMapStatus(normalized, out var mapped) || mapped != currentStatus) return false;
            originalStatus = normalized;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>官方 role → 本地角色。无法识别时按 Assistant 处理并置 recognized=false。</summary>
    public static MessageRole MapRole(string? rawRole, out bool recognized)
    {
        recognized = true;
        if (string.IsNullOrWhiteSpace(rawRole)) return MessageRole.Assistant;
        switch (rawRole.Trim().ToUpperInvariant())
        {
            case "USER":
            case "HUMAN":
                return MessageRole.User;
            case "ASSISTANT":
            case "AI":
            case "BOT":
                return MessageRole.Assistant;
            default:
                recognized = false;
                return MessageRole.Assistant;
        }
    }

    /// <summary>官方片段类型 → 本地片段种类。未知类型按 Raw 保留原文；缺失类型按 Text。</summary>
    public static PartKind MapFragmentKind(string? fragmentType, out bool recognized)
    {
        recognized = true;
        if (string.IsNullOrWhiteSpace(fragmentType)) return PartKind.Text;
        switch (fragmentType.Trim().ToUpperInvariant())
        {
            case "REQUEST":
            case "RESPONSE":
            case "TEMPLATE_RESPONSE":
                return PartKind.Text;
            case "THINK":
                return PartKind.Thinking;
            case "TIP":
                return PartKind.Tip;
            case "CONTENT_RISK":
                return PartKind.Risk;
            case "PARTIAL_FILE_READ":
            case "TOOL_SEARCH":
            case "READ_LINK":
            case "TOOL_RESULT":
                return PartKind.ToolResult;
            case "TOOL_OPEN":
            case "TOOL_FIND":
            case "TOOL_CALL":
                return PartKind.ToolCall;
            case "IMAGE":
                return PartKind.Image;
            case "RAW":
                return PartKind.Raw;
            case "FILE":
                return PartKind.File;
            default:
                recognized = false;
                return PartKind.Raw;
        }
    }

    /// <summary>本地片段种类 → 官方片段类型。工具调用没有官方对应类型，用 TOOL_OPEN/TOOL_SEARCH 承载。</summary>
    public static string ToExportFragmentType(ContentPart part, MessageRole role) => part.Kind switch
    {
        PartKind.Thinking => "THINK",
        PartKind.Tip => "TIP",
        PartKind.Risk => "CONTENT_RISK",
        PartKind.File => "FILE",
        PartKind.ToolCall => "TOOL_OPEN",
        PartKind.ToolResult => "TOOL_SEARCH",
        PartKind.Raw => "RESPONSE",
        _ => role == MessageRole.User ? "REQUEST" : "RESPONSE",
    };

    /// <summary>
    /// 校验 ZIP entry 名：拒绝绝对路径、盘符、<c>..</c>/<c>.</c> 段和空段（zip slip）。
    /// 读取端从不按 entry 名解压落盘，这项校验是额外的纵深防御。
    /// </summary>
    public static void EnsureSafeEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new ChatDemoFormatException("ZIP 中存在空 entry 名。");
        if (entryName.Contains('\0', StringComparison.Ordinal))
            throw new ChatDemoFormatException($"ZIP entry 名含非法字符：{Printable(entryName)}");

        var normalized = entryName.Replace('\\', '/');
        if (normalized[0] == '/')
            throw new ChatDemoFormatException($"ZIP 含绝对路径 entry，已拒绝：{Printable(entryName)}");
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            throw new ChatDemoFormatException($"ZIP 含盘符路径 entry，已拒绝：{Printable(entryName)}");

        var segments = normalized.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment is "." or "..")
                throw new ChatDemoFormatException($"ZIP 含路径穿越 entry，已拒绝：{Printable(entryName)}");
            if (segment.Length == 0 && index != segments.Length - 1)
                throw new ChatDemoFormatException($"ZIP entry 名含空路径段，已拒绝：{Printable(entryName)}");
        }
    }

    /// <summary>把任意名字收敛成安全的文件名（不含路径分隔符、控制字符）。</summary>
    public static string SanitizeFileName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var value = name.Replace('\\', '/');
        var slash = value.LastIndexOf('/');
        if (slash >= 0) value = value[(slash + 1)..];
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        var cleaned = builder.ToString().Trim().Trim('.');
        if (cleaned.Length == 0) return fallback;
        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    /// <summary>规范化扩展名（只留字母数字，小写，带点）。非法时返回空串。</summary>
    public static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return "";
        var builder = new StringBuilder(extension.Length);
        foreach (var character in extension)
        {
            if (character == '.') continue;
            if (!char.IsLetterOrDigit(character)) return "";
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.Length is 0 or > 12 ? "" : "." + builder;
    }

    /// <summary>常见 MIME → 扩展名。用于附件文件名里没有扩展名的情形。</summary>
    public static string ExtensionForMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return "";
        var normalized = mime.Split(';')[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "application/pdf" => ".pdf",
            "application/json" => ".json",
            "application/zip" => ".zip",
            "text/plain" => ".txt",
            "text/markdown" => ".md",
            "text/csv" => ".csv",
            "text/html" => ".html",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "video/mp4" => ".mp4",
            _ => "",
        };
    }

    private static string Printable(string value) => value.Length <= 120 ? value : value[..120] + "…";
}
