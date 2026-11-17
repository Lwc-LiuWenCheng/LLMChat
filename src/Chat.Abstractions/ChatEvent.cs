namespace Chat.Abstractions;

/// <summary>结束原因。映射到数据库状态时：<see cref="EndTurn"/>→完成，<see cref="Cancelled"/>→已停止，其余→错误。</summary>
public enum StopReason
{
    EndTurn,
    MaxTokens,
    StopSequence,
    ToolUse,
    ContentFilter,
    Cancelled,
    Error,
}

/// <summary>用量。字段缺失用 null 而不是 0。</summary>
public sealed record TokenUsage(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CachedInputTokens = null,
    long? ReasoningTokens = null);

/// <summary>
/// 统一事件流。插件按顺序产出：若干增量 →（可选）工具调用 →（可选）用量 → <see cref="Done"/>。
/// 出错时产出 <see cref="Failed"/>，不再产出 <see cref="Done"/>。
/// </summary>
public abstract record ChatEvent
{
    private ChatEvent() { }

    public sealed record TextDelta(string Text) : ChatEvent;

    public sealed record ThinkingDelta(string Text) : ChatEvent;

    public sealed record ToolCallStart(string CallId, string Name) : ChatEvent;

    public sealed record ToolCallArgumentsDelta(string CallId, string JsonDelta) : ChatEvent;

    public sealed record ToolCallCompleted(string CallId, string ArgumentsJson) : ChatEvent;

    public sealed record ToolResultEvent(string CallId, string Content, bool IsError = false) : ChatEvent;

    /// <summary>用量事件（可能多次，以最后一次为准）。</summary>
    public sealed record UsageEvent(TokenUsage Usage) : ChatEvent;

    /// <summary>厂商原始事件（SSE 帧），供排错与缓存回放。</summary>
    public sealed record RawEvent(string Json) : ChatEvent;

    public sealed record Failed(string Code, string Message, bool Retryable = false) : ChatEvent;

    public sealed record Done(StopReason Reason) : ChatEvent;
}
