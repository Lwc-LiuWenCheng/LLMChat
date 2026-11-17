namespace Chat.Core;

/// <summary>角色。树里只存用户与助手两种，系统提示词挂在会话上。</summary>
public enum MessageRole
{
    User,
    Assistant,
}

/// <summary>消息状态，对齐 chat-demo 的 status 枚举（导入时无损映射）。</summary>
public enum MessageStatus
{
    Wip,
    Finished,
    ContentFilter,
    Failed,
    Incomplete,
    ContextLengthExceeded,
    Timeout,
}

/// <summary>
/// 消息树的一个节点。<see cref="Id"/> 在会话内自增（导入的官方备份要求父 id 小于子 id），
/// 因此仓储层必须按会话分配 id。
/// </summary>
public sealed record MessageNode
{
    public required long Id { get; init; }

    public required long? ParentId { get; init; }

    public required MessageRole Role { get; init; }

    public MessageStatus Status { get; init; } = MessageStatus.Finished;

    public string? ProviderId { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<ContentPart> Parts { get; init; } = [];

    /// <summary>厂商原始响应（不解析，仅回放与排错）。</summary>
    public string? RawJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>拼接所有可见文本，供标题生成与导出使用。</summary>
    public string PlainText => string.Join(
        "\n",
        Parts.Where(p => p.Kind is PartKind.Text or PartKind.Tip).Select(p => p.Text).Where(t => !string.IsNullOrEmpty(t)));
}
