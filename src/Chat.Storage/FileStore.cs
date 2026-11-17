using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Chat.Storage;

/// <summary>附件落盘结果。相对路径以数据目录为根，形如 <c>files/{sha256}{ext}</c>。</summary>
public sealed record StoredFile
{
    public required string Sha256 { get; init; }

    public required string RelativePath { get; init; }

    public required string FilePath { get; init; }

    public required long Size { get; init; }

    public required string Extension { get; init; }

    public string? Mime { get; init; }

    public string? OriginalName { get; init; }

    /// <summary>本次是否新写入了内容文件（同一 sha256 已存在时为 false）。</summary>
    public bool IsNew { get; init; }
}

/// <summary>
/// 内容寻址的附件仓库：附件落到 <c>data/files/{sha256}{ext}</c>，同一内容只存一份。
/// 写入先落同目录临时文件，再原子 rename；哈希与写盘一趟完成。
/// </summary>
public sealed class FileStore
{
    public const string FolderName = "files";

    private const int BufferSize = 81920;

    private readonly string _dataDirectory;
    private readonly string _root;

    public FileStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _root = Path.Combine(_dataDirectory, FolderName);
    }

    public string Root => _root;

    /// <summary>读取流并写入仓库，返回 sha256 与相对路径。调用方负责释放 <paramref name="source"/>。</summary>
    public async Task<StoredFile> SaveAsync(
        Stream source,
        string? originalName,
        string? mime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(_root);
        var tempPath = Path.Combine(_root, ".incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sha256;
            long size;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    size = 0;
                    await using (var output = new FileStream(
                                     tempPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     BufferSize,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            hasher.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            size += read;
                        }
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }

            var extension = SanitizeExtension(Path.GetExtension(originalName));
            if (extension.Length == 0) extension = ExtensionForMime(mime);
            var fileName = sha256 + extension;
            var targetPath = Path.Combine(_root, fileName);
            var isNew = !File.Exists(targetPath);
            if (isNew)
            {
                try
                {
                    File.Move(tempPath, targetPath);
                }
                catch (IOException) when (File.Exists(targetPath))
                {
                    // 并发导入相同内容：复用已存在的文件。
                }
            }

            return new StoredFile
            {
                Sha256 = sha256,
                RelativePath = FolderName + "/" + fileName,
                FilePath = targetPath,
                Size = size,
                Extension = extension,
                Mime = mime,
                OriginalName = CleanDisplayName(originalName),
                IsNew = isNew,
            };
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>把相对路径解析成绝对路径，并确保结果仍在数据目录内（防越界路径）。</summary>
    public string ResolvePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(_dataDirectory, relativePath.Replace('\\', '/')));
        var rootWithSeparator = _dataDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _dataDirectory
            : _dataDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new InvalidOperationException($"附件路径越界：{relativePath}");
        return fullPath;
    }

    private static string CleanDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var value = name.Replace('\\', '/');
        var slash = value.LastIndexOf('/');
        if (slash >= 0) value = value[(slash + 1)..];
        return value.Length <= 255 ? value : value[..255];
    }

    private static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension) || extension.Length > 13) return "";
        var builder = new StringBuilder(extension.Length);
        foreach (var character in extension)
        {
            if (character == '.') continue;
            if (!char.IsLetterOrDigit(character)) return "";
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.Length == 0 ? "" : "." + builder;
    }

    private static string ExtensionForMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return "";
        var normalized = mime.Split(';')[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "application/pdf" => ".pdf",
            "application/json" => ".json",
            "application/zip" => ".zip",
            "text/plain" => ".txt",
            "text/markdown" => ".md",
            "text/csv" => ".csv",
            "text/html" => ".html",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "video/mp4" => ".mp4",
            _ => "",
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件删不掉不影响主流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
