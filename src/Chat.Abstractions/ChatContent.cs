namespace Chat.Abstractions;

/// <summary>对话角色。</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>
/// 消息内容片段。插件收发都走这一套结构，避免把厂商 JSON 泄漏到契约里；
/// 厂商特有的东西放 <see cref="Raw"/> 原样透传（主程序不解析，因此协议变化不触发数据库迁移）。
/// </summary>
public abstract record ChatPart
{
    private ChatPart() { }

    /// <summary>普通文本。</summary>
    public sealed record Text(string Value) : ChatPart;

    /// <summary>思维链/推理文本。</summary>
    public sealed record Thinking(string Value) : ChatPart;

    /// <summary>图片输入，data 为原始字节。</summary>
    public sealed record Image(string MediaType, byte[] Data) : ChatPart;

    /// <summary>插件发出的工具调用。</summary>
    public sealed record ToolCall(string CallId, string Name, string ArgumentsJson) : ChatPart;

    /// <summary>工具执行结果。</summary>
    public sealed record ToolResult(string CallId, string Content, bool IsError = false) : ChatPart;

    /// <summary>附件引用（本地文件存储的 id）。</summary>
    public sealed record FileRef(string FileId, string? MediaType = null) : ChatPart;

    /// <summary>厂商原始片段（例如搜索摘要、加密的思考签名）。主程序只存储与回放。</summary>
    public sealed record Raw(string ProviderId, string Json) : ChatPart;
}

/// <summary>一条消息。</summary>
public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ChatPart> Parts)
{
    public ChatMessage(ChatRole role, string text) : this(role, [new ChatPart.Text(text)]) { }
}

/// <summary>工具声明（OpenAI 风格 JSON Schema）。</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>一次请求。字段为 null 表示"用插件默认值"。</summary>
public sealed record ChatRequest
{
    public required string Model { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public string? System { get; init; }
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}
