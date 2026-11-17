using Microsoft.Data.Sqlite;

namespace Chat.App.Tests;

/// <summary>
/// 每个测试一个临时数据目录（SQLite + 快照 + 用户插件目录），避免碰真实的用户数据；
/// 测试结束清空连接池后整目录删除，清理失败会上报而不是静默忽略。
/// </summary>
internal sealed class TempDataDirectory : IDisposable
{
    private static readonly string RootPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "llmchat-headless-tests");

    public TempDataDirectory()
    {
        Path = System.IO.Path.Combine(RootPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // SQLite 仍是 WAL + 连接池：先清池，再删目录，避免 WAL 句柄拖住文件。
        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path)) break;
                Directory.Delete(Path, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        if (Directory.Exists(Path))
            throw new IOException($"临时数据目录未能清理：{Path}");

        // 测试根目录只在本进程用完时顺手删掉；并发跑测试时删不掉也不影响用例。
        TryRemoveEmptyRoot();
    }

    private static void TryRemoveEmptyRoot()
    {
        try
        {
            if (Directory.Exists(RootPath) && !Directory.EnumerateFileSystemEntries(RootPath).Any())
                Directory.Delete(RootPath);
        }
        catch (IOException)
        {
            // 其他测试进程还在使用这个根目录
        }
        catch (UnauthorizedAccessException)
        {
            // 没有权限清理系统临时目录时忽略
        }
    }
}
