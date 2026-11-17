using Chat.Core;

namespace Chat.Storage.ChatDemo;

/// <summary>
/// 导入统计。<see cref="Added"/>/<see cref="Updated"/>/<see cref="Skipped"/> 以会话为单位；
/// 消息与附件单独计数（重复导入时新增计数应为 0，<see cref="Skipped"/> 等于 ZIP 里的会话数）。
/// </summary>
public sealed record ImportReport
{
    /// <summary>新增的会话数。</summary>
    public int Added { get; init; }

    /// <summary>被覆盖更新的会话数。</summary>
    public int Updated { get; init; }

    /// <summary>内容没有变化、被跳过的会话数。</summary>
    public int Skipped { get; init; }

    public int AddedMessages { get; init; }

    public int UpdatedMessages { get; init; }

    public int SkippedMessages { get; init; }

    /// <summary>本次新写入磁盘的附件数（按 sha256 去重）。</summary>
    public int Files { get; init; }

    /// <summary>已存在、按内容复用的附件数。</summary>
    public int ReusedFiles { get; init; }

    /// <summary>没有对应 FILE 片段（或所属会话不在本次导入中）而跳过的附件数。</summary>
    public int SkippedFiles { get; init; }

    /// <summary>导入前自动快照的文件路径；没有可导入会话时为 null。</summary>
    public string? SnapshotPath { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>导出统计。</summary>
public sealed record ExportReport
{
    public required string Path { get; init; }

    public int Sessions { get; init; }

    public int Messages { get; init; }

    public int Files { get; init; }

    public long Bytes { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>manifest.json 里解析出的元信息；字段缺失时为 null（不阻断导入）。</summary>
public sealed record ChatDemoManifest
{
    public int? ExportFormatVersion { get; init; }

    public DateTimeOffset? ExportedAt { get; init; }
}

/// <summary>ZIP 里的一个会话文件。会话 id 先按文件名推断，随后以 JSON 里的 id 为准。</summary>
public sealed record ChatDemoSessionEntry(string EntryName, string InferredSessionId);

/// <summary>ZIP 里的一个附件 entry，路径形如 chat_sessions/{session_id}_files/{file_id}_{name}。</summary>
public sealed record ChatDemoAttachment(string SessionId, string? FileId, string FileName, string EntryName, long Length);

internal sealed record ChatDemoSession
{
    public required string Id { get; init; }

    public string Title { get; init; } = "";

    public string? ProviderId { get; init; }

    public string? Model { get; init; }

    public string? SystemPrompt { get; init; }

    public long? CurrentMessageId { get; init; }

    public bool? Pinned { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UnixEpoch;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UnixEpoch;

    public IReadOnlyList<ChatDemoMessage> Messages { get; init; } = [];
}

internal sealed record ChatDemoMessage
{
    public required long Id { get; init; }

    public long? ParentId { get; init; }

    public required MessageRole Role { get; init; }

    public required MessageStatus Status { get; init; }

    public string? ProviderId { get; init; }

    public string? Model { get; init; }

    /// <summary>导入时原样保留的官方消息 JSON（状态映射丢失的信息都在这里）。</summary>
    public string? RawJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UnixEpoch;

    public IReadOnlyList<ContentPart> Parts { get; init; } = [];
}
