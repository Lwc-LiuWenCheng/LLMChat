namespace Chat.App.Services;

public enum CommandLineAction
{
    None,
    Import,
    Export,
    Help,
}

/// <summary>
/// CLI 参数解析（与 Avalonia 启动参数分离）：--import 或 --export 二选一，
/// --export 可带零到多个 --session id。解析失败保留可读错误，交给 <see cref="CliRunner"/> 输出。
/// </summary>
public sealed class CommandLineOptions
{
    public CommandLineAction Action { get; init; }

    public string? ArchivePath { get; init; }

    public IReadOnlyList<string> SessionIds { get; init; } = [];

    public string? Error { get; init; }

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? importPath = null;
        string? exportPath = null;
        string? error = null;
        var sessionIds = new List<string>();
        var help = false;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help" or "-h":
                    help = true;
                    break;

                case "--import":
                    if (importPath is not null)
                    {
                        error ??= "重复指定了 --import。";
                        break;
                    }
                    if (exportPath is not null)
                    {
                        error ??= "--import 与 --export 不能同时使用。";
                        break;
                    }
                    if (!TryTakeValue(args, ref index, out var importValue))
                    {
                        error ??= "--import 需要一个 ZIP 文件路径。";
                        break;
                    }
                    importPath = importValue;
                    break;

                case "--export":
                    if (exportPath is not null)
                    {
                        error ??= "重复指定了 --export。";
                        break;
                    }
                    if (importPath is not null)
                    {
                        error ??= "--import 与 --export 不能同时使用。";
                        break;
                    }
                    if (!TryTakeValue(args, ref index, out var exportValue))
                    {
                        error ??= "--export 需要一个 ZIP 文件路径。";
                        break;
                    }
                    exportPath = exportValue;
                    break;

                case "--session":
                    if (!TryTakeSessionIds(args, ref index, sessionIds))
                    {
                        error ??= "--session 后至少需要一个会话 id。";
                    }
                    break;

                default:
                    error ??= $"无法识别的参数：{arg}";
                    break;
            }
        }

        if (help)
            return new CommandLineOptions { Action = CommandLineAction.Help };

        var action = importPath is not null
            ? CommandLineAction.Import
            : exportPath is not null ? CommandLineAction.Export : CommandLineAction.None;

        if (error is null)
        {
            if (action == CommandLineAction.None && args.Count > 0)
                error = "没有指定操作：请使用 --import <zip> 或 --export <zip>。";
            else if (action == CommandLineAction.Import && sessionIds.Count > 0)
                error = "--session 只能与 --export 一起使用。";
        }

        return new CommandLineOptions
        {
            Action = action,
            ArchivePath = importPath ?? exportPath,
            SessionIds = sessionIds.Distinct(StringComparer.Ordinal).ToList(),
            Error = error,
        };
    }

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        value = "";
        if (index + 1 >= args.Count) return false;
        var candidate = args[index + 1];
        if (candidate.StartsWith("--", StringComparison.Ordinal)) return false;
        value = candidate;
        index++;
        return true;
    }

    private static bool TryTakeSessionIds(IReadOnlyList<string> args, ref int index, List<string> sessionIds)
    {
        var start = sessionIds.Count;
        while (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            var value = args[index + 1].Trim();
            if (value.Length > 0) sessionIds.Add(value);
            index++;
        }
        return sessionIds.Count > start;
    }
}
