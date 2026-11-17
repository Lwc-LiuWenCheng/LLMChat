namespace Chat.Core;

/// <summary>片段类型。持久化为字符串，新增类型不影响旧库。</summary>
public enum PartKind
{
    Text,
    Thinking,
    ToolCall,
    ToolResult,
    Image,
    File,
    Tip,
    Risk,
    Raw,
}

/// <summary>持久化片段。<see cref="MetaJson"/> 存放工具调用 id/参数、图片 mime 等结构化附加信息。</summary>
public sealed record ContentPart(PartKind Kind, string? Text, string? MetaJson = null)
{
    public static ContentPart OfText(string text) => new(PartKind.Text, text);

    public static ContentPart OfThinking(string text) => new(PartKind.Thinking, text);

    public static ContentPart ToolCall(string callId, string name, string argumentsJson) =>
        new(PartKind.ToolCall, name, $"{{\"callId\":{Json(callId)},\"arguments\":{Json(argumentsJson)}}}");

    public static ContentPart ToolResult(string callId, string content, bool isError = false) =>
        new(PartKind.ToolResult, content, $"{{\"callId\":{Json(callId)},\"isError\":{(isError ? "true" : "false")}}}");

    public static ContentPart RawJson(string json) => new(PartKind.Raw, json);

    private static string Json(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
