namespace Chat.Core;

/// <summary>一个会话。<see cref="CurrentMessageId"/> 指向当前分支的末端节点。</summary>
public sealed record Session
{
    public required string Id { get; init; }

    public string Title { get; init; } = "";

    public string? ProviderId { get; init; }

    public string? Model { get; init; }

    public string? SystemPrompt { get; init; }

    public long? CurrentMessageId { get; init; }

    public bool Pinned { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "新会话" : Title;
}
