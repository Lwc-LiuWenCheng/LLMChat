using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Chat.Storage.ChatDemo;

/// <summary>
/// 只读打开 chat-demo v3 ZIP。读取全部在内存/流上完成，绝不按 entry 名解压落盘；
/// 打开时先统一校验 entry 名（zip slip），并定位 manifest 推断导出根目录（容忍被重新打包进子目录）。
/// </summary>
public sealed class ChatDemoZipReader : IDisposable
{
    private const long MaxJsonEntryBytes = 512L * 1024 * 1024;
    private const long MaxAttachmentBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _rawEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZipArchiveEntry> _relativeEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private readonly string _prefix;

    private ChatDemoZipReader(FileStream stream, ZipArchive archive)
    {
        _stream = stream;
        _archive = archive;

        foreach (var entry in archive.Entries) ChatDemoFormat.EnsureSafeEntryName(entry.FullName);

        ZipArchiveEntry? manifestEntry = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var normalized = Normalize(entry.FullName);
            var slash = normalized.LastIndexOf('/');
            var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;
            if (!fileName.Equals(ChatDemoFormat.ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
            if (manifestEntry is not null)
                throw new ChatDemoFormatException("ZIP 中存在多个 manifest.json，无法确定导出根目录。");
            manifestEntry = entry;
        }

        _prefix = manifestEntry is null ? "" : GetDirectory(Normalize(manifestEntry.FullName));
        if (_prefix.Length > 0) ChatDemoFormat.EnsureSafeEntryName(_prefix);

        var foreign = 0;
        string? foreignSample = null;
        foreach (var entry in archive.Entries)
        {
            var normalized = Normalize(entry.FullName);
            string relative;
            if (_prefix.Length == 0)
            {
                relative = normalized;
            }
            else if (normalized.StartsWith(_prefix + "/", StringComparison.Ordinal))
            {
                relative = normalized[(_prefix.Length + 1)..];
            }
            else
            {
                foreign++;
                foreignSample ??= entry.FullName;
                continue;
            }

            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            _rawEntries.TryAdd(entry.FullName, entry);
            if (!_relativeEntries.TryAdd(relative, entry))
                _warnings.Add($"ZIP 中 entry 重名：{relative}，保留第一条。");
        }

        if (foreign > 0)
            _warnings.Add($"ZIP 中有 {foreign} 个 entry 不在导出根目录下，已忽略（例如 {foreignSample}）。");

        if (manifestEntry is null)
        {
            _warnings.Add("ZIP 缺少 manifest.json，按 chat-demo v3 目录结构尽力导入。");
            Manifest = new ChatDemoManifest();
        }
        else
        {
            var first = archive.Entries.FirstOrDefault();
            if (first is not null && !string.Equals(first.FullName, manifestEntry.FullName, StringComparison.Ordinal))
                _warnings.Add("manifest.json 不是 ZIP 的第一条 entry（官方格式要求首条），仍继续导入。");
            Manifest = ReadManifest(manifestEntry);
        }

        Sessions = BuildSessions();
        Attachments = BuildAttachments();
        IndexJson = ReadIndexJson();
    }

    public ChatDemoManifest Manifest { get; }

    /// <summary>chat_session_index.json 原文；缺失或非法时为 null（不阻断导入）。</summary>
    public string? IndexJson { get; }

    public IReadOnlyList<string> Warnings => _warnings;

    public IReadOnlyList<ChatDemoSessionEntry> Sessions { get; }

    public IReadOnlyList<ChatDemoAttachment> Attachments { get; }

    public static ChatDemoZipReader Open(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return new ChatDemoZipReader(stream, archive);
        }
        catch (InvalidDataException exception)
        {
            archive?.Dispose();
            stream.Dispose();
            throw new ChatDemoFormatException($"不是合法的 ZIP 文件：{Path.GetFileName(zipPath)}", exception);
        }
        catch
        {
            archive?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>读取单个会话文件。返回的流由调用方释放（用 using）。</summary>
    public Stream OpenSession(ChatDemoSessionEntry session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return OpenEntry(session.EntryName, MaxJsonEntryBytes);
    }

    /// <summary>读取单个附件。返回的流由调用方释放（用 using）。</summary>
    public Stream OpenAttachment(ChatDemoAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return OpenEntry(attachment.EntryName, MaxAttachmentBytes);
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }

    private Stream OpenEntry(string entryName, long maxBytes)
    {
        if (!_rawEntries.TryGetValue(entryName, out var entry))
            throw new ChatDemoFormatException($"ZIP 中找不到 entry：{entryName}");
        if (entry.Length > maxBytes)
            throw new ChatDemoFormatException($"ZIP entry 过大（{entry.Length} 字节）：{entryName}");
        return entry.Open();
    }

    private ChatDemoManifest ReadManifest(ZipArchiveEntry entry)
    {
        if (entry.Length > 8L * 1024 * 1024)
        {
            _warnings.Add("manifest.json 过大，已忽略其中的元信息。");
            return new ChatDemoManifest();
        }

        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _warnings.Add("manifest.json 根节点不是对象，已忽略其中的元信息。");
                return new ChatDemoManifest();
            }

            var version = ChatDemoJson.GetInt64(root, "export_format_version", "exportFormatVersion", "version", "format_version");
            var exportedAt = ChatDemoJson.GetTimestamp(root, "exported_at", "exportedAt", "created_at", "createdAt");
            int? formatVersion = null;
            if (version is null)
            {
                _warnings.Add("manifest.json 缺少 export_format_version，按 v3 尽力导入。");
            }
            else if (version.Value is < int.MinValue or > int.MaxValue)
            {
                _warnings.Add($"manifest.json 的导出格式版本 {version} 超出支持范围，按 v3 尽力导入。");
            }
            else
            {
                formatVersion = (int)version.Value;
                if (formatVersion != ChatDemoFormat.SupportedExportFormatVersion)
                    _warnings.Add($"manifest.json 的导出格式版本为 {formatVersion}（本程序支持 {ChatDemoFormat.SupportedExportFormatVersion}），按 v3 尽力导入。");
            }

            return new ChatDemoManifest
            {
                ExportFormatVersion = formatVersion,
                ExportedAt = exportedAt,
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            _warnings.Add($"manifest.json 无法解析（{exception.Message}），按缺省元信息导入。");
            return new ChatDemoManifest();
        }
    }

    private List<ChatDemoSessionEntry> BuildSessions()
    {
        var list = new List<ChatDemoSessionEntry>();
        foreach (var entry in _archive.Entries)
        {
            if (!TryRelative(entry.FullName, out var relative)) continue;
            if (!relative.StartsWith(ChatDemoFormat.SessionsRoot + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = relative[(ChatDemoFormat.SessionsRoot.Length + 1)..];
            if (rest.Contains('/')) continue;
            var inferred = rest[..^5];
            if (inferred.Length == 0) continue;
            list.Add(new ChatDemoSessionEntry(entry.FullName, inferred));
        }
        return list;
    }

    private List<ChatDemoAttachment> BuildAttachments()
    {
        var list = new List<ChatDemoAttachment>();
        foreach (var entry in _archive.Entries)
        {
            if (!TryRelative(entry.FullName, out var relative)) continue;
            if (!relative.StartsWith(ChatDemoFormat.SessionsRoot + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = relative[(ChatDemoFormat.SessionsRoot.Length + 1)..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue;
            var folder = rest[..slash];
            if (!folder.EndsWith(ChatDemoFormat.FilesFolderSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var sessionId = folder[..^ChatDemoFormat.FilesFolderSuffix.Length];
            if (sessionId.Length == 0)
            {
                _warnings.Add($"附件目录名缺少会话 id：{entry.FullName}");
                continue;
            }
            var tail = rest[(slash + 1)..];
            var nameSlash = tail.LastIndexOf('/');
            var fileName = nameSlash >= 0 ? tail[(nameSlash + 1)..] : tail;
            if (fileName.Length == 0) continue;
            list.Add(new ChatDemoAttachment(sessionId, ExtractFileId(fileName), fileName, entry.FullName, entry.Length));
        }
        return list;
    }

    private string? ReadIndexJson()
    {
        if (!_relativeEntries.TryGetValue(ChatDemoFormat.IndexEntryName, out var entry)) return null;
        if (entry.Length > MaxJsonEntryBytes)
        {
            _warnings.Add("chat_session_index.json 过大，已忽略。");
            return null;
        }
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        try
        {
            using var document = JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException)
        {
            _warnings.Add("chat_session_index.json 不是合法 JSON，已忽略。");
            return null;
        }
        return text;
    }

    private bool TryRelative(string fullName, out string relative)
    {
        var normalized = Normalize(fullName);
        if (_prefix.Length == 0)
        {
            relative = normalized;
            return true;
        }
        if (!normalized.StartsWith(_prefix + "/", StringComparison.Ordinal))
        {
            relative = "";
            return false;
        }
        relative = normalized[(_prefix.Length + 1)..];
        return true;
    }

    private static string Normalize(string entryName) => entryName.Replace('\\', '/');

    private static string GetDirectory(string normalized)
    {
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static string? ExtractFileId(string fileName)
    {
        var index = fileName.IndexOf('_');
        if (index <= 0) return null;
        var candidate = fileName[..index];
        return candidate.All(character => char.IsLetterOrDigit(character) || character == '-') ? candidate : null;
    }
}
