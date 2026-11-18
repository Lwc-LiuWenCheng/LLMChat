using System.Diagnostics;

namespace Chat.Console.Tests;

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// 以独立进程运行已构建的 llmchat-cli.dll：避免测试进程重定向全局 Console，
/// 同时真实验证插件加载、环境变量与退出码。
/// </summary>
internal static class CliProcess
{
    private static readonly string[] SecretEnvVars =
        ["DEEPSEEK_API_KEY", "OPENAI_API_KEY", "OPENCODE_API_KEY"];

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string Configuration =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    public static string ConsoleDll =>
        Path.Combine(RepoRoot, "src", "Chat.Console", "bin", Configuration, "net10.0", "llmchat-cli.dll");

    public static async Task<CliResult> RunAsync(IEnumerable<string> args, int timeoutMs = 60000)
    {
        if (!File.Exists(ConsoleDll))
            throw new InvalidOperationException($"找不到 CLI 程序集：{ConsoleDll}，请先构建 Chat.slnx。");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        startInfo.ArgumentList.Add(ConsoleDll);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        foreach (var name in SecretEnvVars)
            startInfo.Environment.Remove(name);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 llmchat-cli 进程。");

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"llmchat-cli 超过 {timeoutMs}ms 未退出。");
        }

        return new CliResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Chat.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到 Chat.slnx，无法定位仓库根目录。");
    }
}

internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "llmchat-console-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);

            var root = Directory.GetParent(Path)?.FullName;
            if (root is not null &&
                Directory.Exists(root) &&
                !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
