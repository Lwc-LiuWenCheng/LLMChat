using System.Text;
using System.Text.Json;
using Chat.Storage.ChatDemo;

namespace Chat.App.Services;

/// <summary>导入前预检结果：让 UI 在真正写库前展示将要合并的会话/附件数量。</summary>
public sealed record ArchivePreflight
{
    public required string ZipPath { get; init; }

    /// <summary>按 ZIP 内顺序解析出的真实会话 id（优先会话 JSON，其次索引/文件名）；导入后用它在列表中定位第一个导入会话。</summary>
    public IReadOnlyList<string> SessionIds { get; init; } = [];

    public int Attachments { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Sessions => SessionIds.Count;

    public string ConfirmationText =>
        $"将合并 {Sessions} 个会话、{Attachments} 个附件；导入前会自动创建快照，已有会话按 id 合并（不清库）。";
}

/// <summary>
/// App 层的导入/导出编排：把 Storage 的静态入口包成后台任务，并统一生成中文摘要。
/// 不依赖 Avalonia，headless 测试与 CLI 共用同一套行为。
/// </summary>
public static class ArchiveWorkflow
{
    public static Task<ArchivePreflight> PreflightAsync(string zipPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = ChatDemoZipReader.Open(zipPath);
            var warnings = reader.Warnings.ToList();
            var sessionIds = ReadSessionIds(reader, warnings);
            return new ArchivePreflight
            {
                ZipPath = Path.GetFullPath(zipPath),
                SessionIds = sessionIds,
                Attachments = reader.Attachments.Count,
                Warnings = warnings,
            };
        }, cancellationToken);

    /// <summary>
    /// 预检时解析每个会话文件的真实 id。顺序与导入端完全一致：
    /// JSON id → 索引按顺序对应的 id → 文件名，由 <see cref="ChatDemoSessionIds.Resolve"/> 统一实现。
    /// 官方 ZIP 的文件名不一定等于会话 id；两边不同源会让确认框数量、实际导入与导入后定位会话都错位。
    /// </summary>
    private static List<string> ReadSessionIds(ChatDemoZipReader reader, List<string> warnings)
    {
        var entries = reader.Sessions;
        var indexIds = ChatDemoSessionIds.ReadIndexIds(reader.IndexJson);
        var ids = new List<string>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var jsonId = TryReadSessionId(reader, entry, warnings);
            var id = ChatDemoSessionIds.Resolve(
                jsonId, indexIds, index, entries.Count, entry.InferredSessionId);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            ids.Add(id);
        }
        return ids;
    }

    private static string? TryReadSessionId(
        ChatDemoZipReader reader,
        ChatDemoSessionEntry entry,
        List<string> warnings)
    {
        try
        {
            using var stream = reader.OpenSession(entry);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = document.RootElement;
            var id = ChatDemoSessionIds.ReadSessionJsonId(root);
            if (id is null) return null;
            if (!string.Equals(id, entry.InferredSessionId, StringComparison.Ordinal))
                warnings.Add($"会话文件 {entry.EntryName} 的 id={id} 与文件名不一致，以 JSON 里的 id 为准。");
            return id;
        }
        catch (Exception exception) when (exception is JsonException or IOException or ChatDemoFormatException)
        {
            warnings.Add($"会话文件 {entry.EntryName} 无法读取 id（{exception.Message}），按文件名推断。");
            return null;
        }
    }

    public static Task<ImportReport> ImportAsync(
        string zipPath,
        string dataDirectory,
        IProgress<ImportReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => ChatDemoArchive.ImportAsync(zipPath, dataDirectory, progress, cancellationToken),
            cancellationToken);

    public static Task<ExportReport> ExportAllAsync(
        string zipPath,
        string dataDirectory,
        IProgress<ExportReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => ChatDemoArchive.ExportAsync(zipPath, dataDirectory, progress, cancellationToken),
            cancellationToken);

    public static Task<ExportReport> ExportSessionsAsync(
        string zipPath,
        string dataDirectory,
        IReadOnlyCollection<string> sessionIds,
        IProgress<ExportReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => ChatDemoArchive.ExportSessionsAsync(zipPath, dataDirectory, sessionIds, progress, cancellationToken),
            cancellationToken);

    public static string DescribeImport(ImportReport report)
    {
        var text = new StringBuilder();
        text.Append($"导入完成：新增 {report.Added}、更新 {report.Updated}、跳过 {report.Skipped} 个会话");
        text.Append($"; 消息 新增 {report.AddedMessages}、更新 {report.UpdatedMessages}、跳过 {report.SkippedMessages}");
        text.Append($"; 附件 新增 {report.Files}、复用 {report.ReusedFiles}、跳过 {report.SkippedFiles}");
        if (report.SnapshotPath is { Length: > 0 } snapshot)
            text.Append($"; 快照 {Path.GetFileName(snapshot)}");

        if (report.Warnings.Count == 0) return text.ToString();

        text.Append($"; 警告 {report.Warnings.Count} 条：");
        text.Append(string.Join("；", report.Warnings.Take(3)));
        if (report.Warnings.Count > 3) text.Append("…");
        return text.ToString();
    }

    public static string DescribeExport(ExportReport report)
    {
        var text = new StringBuilder();
        text.Append($"导出完成：{report.Sessions} 个会话、{report.Messages} 条消息、{report.Files} 个附件");
        text.Append($"; 文件 {Path.GetFileName(report.Path)}（{FormatBytes(report.Bytes)}）");
        if (report.Warnings.Count > 0)
            text.Append($"; 警告 {report.Warnings.Count} 条：{string.Join("；", report.Warnings.Take(3))}");
        return text.ToString();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0") + " KiB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MiB",
    };
}