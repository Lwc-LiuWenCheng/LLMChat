namespace Chat.Storage.ChatDemo;

/// <summary>
/// CLI/UI 级静态入口：默认数据目录与显式数据目录两个重载。
/// 导入 = <see cref="ChatDemoZipReader"/> + <see cref="ChatDemoImportService"/>；
/// 导出 = <see cref="ChatDemoExportService"/>。UI 接线由后续任务完成。
/// </summary>
public static class ChatDemoArchive
{
    /// <summary>导入到默认数据目录（<see cref="StoragePaths.DefaultDataDirectory"/>）。</summary>
    public static Task<ImportReport> ImportAsync(
        string zipPath,
        IProgress<ImportReport>? progress = null,
        CancellationToken cancellationToken = default)
        => ImportAsync(zipPath, StoragePaths.DefaultDataDirectory, progress, cancellationToken);

    /// <summary>导入到指定数据目录（自动建库、自动迁移、自动快照）。</summary>
    public static async Task<ImportReport> ImportAsync(
        string zipPath,
        string dataDirectory,
        IProgress<ImportReport>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var database = new ChatDatabase(StoragePaths.DatabaseFile(dataDirectory));
        database.Initialize();
        var service = new ChatDemoImportService(database, dataDirectory);
        return await service.ImportAsync(zipPath, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从默认数据目录导出全部会话。</summary>
    public static Task<ExportReport> ExportAsync(
        string zipPath,
        IProgress<ExportReport>? progress = null,
        CancellationToken cancellationToken = default)
        => ExportAsync(zipPath, StoragePaths.DefaultDataDirectory, progress, cancellationToken);

    /// <summary>从指定数据目录导出全部会话。</summary>
    public static async Task<ExportReport> ExportAsync(
        string zipPath,
        string dataDirectory,
        IProgress<ExportReport>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var database = new ChatDatabase(StoragePaths.DatabaseFile(dataDirectory));
        database.Initialize();
        var service = new ChatDemoExportService(database, dataDirectory);
        return await service.ExportAsync(zipPath, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从指定数据目录导出指定会话。</summary>
    public static async Task<ExportReport> ExportSessionsAsync(
        string zipPath,
        string dataDirectory,
        IReadOnlyCollection<string> sessionIds,
        IProgress<ExportReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(sessionIds);
        Directory.CreateDirectory(dataDirectory);
        var database = new ChatDatabase(StoragePaths.DatabaseFile(dataDirectory));
        database.Initialize();
        var service = new ChatDemoExportService(database, dataDirectory);
        return await service.ExportSessionsAsync(zipPath, sessionIds, progress, cancellationToken).ConfigureAwait(false);
    }
}
