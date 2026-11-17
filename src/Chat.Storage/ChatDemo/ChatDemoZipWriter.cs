using System.IO.Compression;
using System.Text;

namespace Chat.Storage.ChatDemo;

/// <summary>
/// chat-demo v3 ZIP 写入器。先写同目录临时文件，<see cref="Complete"/> 时提交并原子 rename 到目标路径，
/// 失败时删除临时文件，绝不留下半截 ZIP。
/// </summary>
public sealed class ChatDemoZipWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly string _temporaryPath;
    private readonly string _targetPath;
    private bool _completed;
    private bool _disposed;

    private ChatDemoZipWriter(FileStream stream, ZipArchive archive, string temporaryPath, string targetPath)
    {
        _stream = stream;
        _archive = archive;
        _temporaryPath = temporaryPath;
        _targetPath = targetPath;
    }

    public static ChatDemoZipWriter Create(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var target = Path.GetFullPath(zipPath);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException($"无法解析导出目录：{zipPath}");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
            return new ChatDemoZipWriter(stream, archive, temporary, target);
        }
        catch
        {
            stream.Dispose();
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>创建下一条 entry。写入顺序就是调用顺序（manifest 必须第一条）。</summary>
    public Stream CreateEntry(string entryName)
    {
        ChatDemoFormat.EnsureSafeEntryName(entryName);
        return _archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
    }

    /// <summary>提交 ZIP 并原子替换目标文件。</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) return;

        _archive.Dispose();
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        try
        {
            File.Move(_temporaryPath, _targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(_temporaryPath);
            throw;
        }
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_completed) return;
        try
        {
            _archive.Dispose();
        }
        catch (InvalidDataException)
        {
            // 未提交的 ZIP 不需要保证结构完整。
        }
        _stream.Dispose();
        TryDelete(_temporaryPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件清理失败不改变主流程结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
