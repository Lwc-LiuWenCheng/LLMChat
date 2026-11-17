using System.Text.Encodings.Web;
using System.Text.Json;
using Chat.Storage;
using Chat.Storage.ChatDemo;

namespace Chat.App.Services;

/// <summary>
/// 无 UI 的 CLI 入口：--import / --export 走与界面相同的 Storage 导入导出，
/// 结果输出一行 JSON（成功与失败都是 JSON），退出码 0 = 成功、1 = 失败。
/// </summary>
public static class CliRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string Usage = """
        LLMChat 命令行导入/导出（chat-demo v3 ZIP）
          LLMChat --import <zip>
          LLMChat --export <zip> [--session <id> ...]
          LLMChat --help

        说明：
          --import  合并导入 ZIP；按会话 id 覆盖同名会话，保留本地独有会话，导入前自动快照。
          --export  导出为 v3 ZIP；不带 --session 时导出全部未删除会话。
        """;

    public static bool IsCliInvocation(IReadOnlyList<string> args) =>
        args.Any(arg => arg is "--import" or "--export" or "--session" or "--help" or "-h");

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        string? dataDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        var options = CommandLineOptions.Parse(args);
        var command = options.Action switch
        {
            CommandLineAction.Import => "import",
            CommandLineAction.Export => "export",
            CommandLineAction.Help => "help",
            _ => "unknown",
        };

        if (options.Action == CommandLineAction.Help)
        {
            output.WriteLine(Usage);
            return 0;
        }

        if (options.Error is { Length: > 0 } error)
        {
            WriteJson(output, new { command, status = "error", error });
            return 1;
        }

        var data = dataDirectory ?? StoragePaths.DefaultDataDirectory;

        try
        {
            if (options.Action == CommandLineAction.Import)
            {
                var report = await ArchiveWorkflow.ImportAsync(
                    options.ArchivePath!, data, null, cancellationToken).ConfigureAwait(false);
                WriteJson(output, new
                {
                    command,
                    status = "ok",
                    archive = Path.GetFullPath(options.ArchivePath!),
                    report.Added,
                    report.Updated,
                    report.Skipped,
                    report.AddedMessages,
                    report.UpdatedMessages,
                    report.SkippedMessages,
                    report.Files,
                    report.ReusedFiles,
                    report.SkippedFiles,
                    snapshot = report.SnapshotPath,
                    warnings = report.Warnings,
                });
                return 0;
            }

            if (options.Action == CommandLineAction.Export)
            {
                var report = options.SessionIds.Count > 0
                    ? await ArchiveWorkflow.ExportSessionsAsync(
                        options.ArchivePath!, data, options.SessionIds, null, cancellationToken).ConfigureAwait(false)
                    : await ArchiveWorkflow.ExportAllAsync(
                        options.ArchivePath!, data, null, cancellationToken).ConfigureAwait(false);
                WriteJson(output, new
                {
                    command,
                    status = "ok",
                    path = report.Path,
                    report.Sessions,
                    report.Messages,
                    report.Files,
                    report.Bytes,
                    warnings = report.Warnings,
                });
                return 0;
            }

            WriteJson(output, new
            {
                command,
                status = "error",
                error = "没有指定操作：请使用 --import <zip> 或 --export <zip>。",
            });
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteJson(output, new { command, status = "error", error = "操作已取消。" });
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteJson(output, new { command, status = "error", error = exception.Message });
            return 1;
        }
    }

    private static void WriteJson(TextWriter output, object payload) =>
        output.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
}