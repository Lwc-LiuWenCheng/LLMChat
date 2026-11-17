using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chat.Core;

namespace Chat.Storage.ChatDemo;

/// <summary>
/// 把本地会话导出成 chat-demo v3 ZIP：manifest.json（首条）→ chat_sessions/{id}.json →
/// chat_session_index.json → chat_sessions/{id}_files/{file_id}_{name}。
/// 产物可被官方客户端或本程序的 <see cref="ChatDemoImportService"/> 再次导入。
/// </summary>
public sealed class ChatDemoExportService
{
    private readonly ChatDatabase _database;
    private readonly SessionRepository _sessions;
    private readonly MessageRepository _messages;
    private readonly FileStore _files;

    public ChatDemoExportService(ChatDatabase database, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _database = database;
        _sessions = new SessionRepository(database);
        _messages = new MessageRepository(database);
        _files = new FileStore(dataDirectory);
    }

    /// <summary>导出全部未删除会话。</summary>
    public async Task<ExportReport> ExportAsync(
        string zipPath,
        IProgress<ExportReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (exports, warnings) = LoadExports(sessionIds: null);
        return await WriteArchiveAsync(zipPath, exports, warnings, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只导出指定会话 id（用于“导出当前会话”）。</summary>
    public async Task<ExportReport> ExportSessionsAsync(
        string zipPath,
        IReadOnlyCollection<string> sessionIds,
        IProgress<ExportReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        var (exports, warnings) = LoadExports(sessionIds);
        return await WriteArchiveAsync(zipPath, exports, warnings, progress, cancellationToken).ConfigureAwait(false);
    }

    private (List<ExportSession> Exports, List<string> Warnings) LoadExports(IReadOnlyCollection<string>? sessionIds)
    {
        var sessions = _sessions.List(includeDeleted: false);
        if (sessionIds is not null)
        {
            // 空集合表示“明确不导出任何会话”，而不是“导出全部”。
            var ids = sessionIds.ToHashSet(StringComparer.Ordinal);
            sessions = sessions.Where(session => ids.Contains(session.Id)).ToList();
        }

        var fileRows = LoadFileRows();
        var warnings = new List<string>();
        var exports = new List<ExportSession>(sessions.Count);
        foreach (var session in sessions)
        {
            var messages = _messages.ListBySession(session.Id);
            var attachments = CollectAttachments(session, messages, fileRows, warnings);
            exports.Add(new ExportSession(session, messages, attachments));
        }
        return (exports, warnings);
    }

    private Dictionary<string, FileRow> LoadFileRows()
    {
        var rows = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, rel_path, size, ext, mime, original_name FROM files";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sha256 = reader.GetString(0);
            rows[sha256] = new FileRow(
                sha256,
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        return rows;
    }

    private List<ExportAttachment> CollectAttachments(
        Session session,
        IReadOnlyList<MessageNode> messages,
        IReadOnlyDictionary<string, FileRow> files,
        List<string> warnings)
    {
        var attachments = new List<ExportAttachment>();
        var seenFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safeSessionId = ChatDemoFormat.SanitizeFileName(session.Id, "session");
        foreach (var message in messages)
        {
            foreach (var part in message.Parts)
            {
                if (part.Kind != PartKind.File) continue;
                var info = ChatDemoJson.ReadFileInfo(part.MetaJson);
                if (info.Sha256 is null || !files.TryGetValue(info.Sha256, out var row))
                {
                    if (info.Sha256 is not null)
                        warnings.Add($"附件 {info.Sha256} 只存在于消息元数据，files 表里没有记录，导出时跳过。");
                    continue;
                }

                var sourcePath = _files.ResolvePath(row.RelPath);
                if (!File.Exists(sourcePath))
                {
                    warnings.Add($"附件文件缺失：{row.RelPath}");
                    continue;
                }

                var fileId = ChatDemoFormat.SanitizeFileName(info.FileId ?? row.Sha256[..Math.Min(8, row.Sha256.Length)], "file");
                var name = ChatDemoFormat.SanitizeFileName(info.Name ?? row.OriginalName, "file" + row.Ext);

                // 同一片段被多条消息引用时只写一份；不同内容/不同 file_id 清洗后同名则用 sha256 去重命名。
                var fragmentKey = row.Sha256 + "\0" + fileId + "\0" + name;
                if (!seenFragments.Add(fragmentKey)) continue;

                var entryName = ChatDemoFormat.SessionsRoot + "/" + safeSessionId + ChatDemoFormat.FilesFolderSuffix + "/" + fileId + "_" + name;
                if (!usedEntryNames.Add(entryName))
                    entryName = MakeUniqueEntryName(entryName, row.Sha256, usedEntryNames);
                attachments.Add(new ExportAttachment(entryName, sourcePath, new FileInfo(sourcePath).Length));
            }
        }
        return attachments;
    }

    /// <summary>entry 名清洗后碰撞时，在扩展名前插入 sha256 前 8 位；仍碰撞则追加序号。</summary>
    private static string MakeUniqueEntryName(string entryName, string sha256, HashSet<string> used)
    {
        var slash = entryName.LastIndexOf('/');
        var directory = slash >= 0 ? entryName[..(slash + 1)] : "";
        var fileName = slash >= 0 ? entryName[(slash + 1)..] : entryName;
        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        var extension = dot > 0 ? fileName[dot..] : "";
        var tag = sha256.Length >= 8 ? sha256[..8] : sha256;
        if (tag.Length == 0) tag = "file";

        var candidate = directory + stem + "-" + tag + extension;
        var counter = 2;
        while (!used.Add(candidate))
        {
            candidate = directory + stem + "-" + tag + "-" + counter + extension;
            counter++;
        }
        return candidate;
    }

    private async Task<ExportReport> WriteArchiveAsync(
        string zipPath,
        IReadOnlyList<ExportSession> exports,
        List<string> warnings,
        IProgress<ExportReport>? progress,
        CancellationToken cancellationToken)
    {
        var exportedAt = DateTimeOffset.UtcNow;
        var messageCount = exports.Sum(export => export.Messages.Count);
        var fileCount = exports.Sum(export => export.Attachments.Count);
        var sessionCount = 0;
        var messagesWritten = 0;

        using var writer = ChatDemoZipWriter.Create(zipPath);
        WriteJsonEntry(writer, ChatDemoFormat.ManifestEntryName, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("export_format_version", ChatDemoFormat.SupportedExportFormatVersion);
            json.WriteString("exported_at", exportedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("generator", "LLMChat");
            json.WriteString("generator_version", typeof(ChatDemoExportService).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            json.WriteNumber("session_count", exports.Count);
            json.WriteNumber("message_count", messageCount);
            json.WriteNumber("file_count", fileCount);
            json.WriteEndObject();
        });

        var usedSessionEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var export in exports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionFileName = ChatDemoFormat.SanitizeFileName(export.Session.Id, "session");
            var sessionEntryName = ChatDemoFormat.SessionsRoot + "/" + sessionFileName + ".json";
            if (!usedSessionEntries.Add(sessionEntryName))
                sessionEntryName = MakeUniqueEntryName(sessionEntryName, Hash(export.Session.Id), usedSessionEntries);
            WriteSessionEntry(writer, export, sessionEntryName);
            sessionCount++;
            messagesWritten += export.Messages.Count;
            progress?.Report(new ExportReport
            {
                Path = Path.GetFullPath(zipPath),
                Sessions = sessionCount,
                Messages = messagesWritten,
                Files = 0,
                Bytes = 0,
                Warnings = warnings.ToArray(),
            });
        }

        WriteIndexEntry(writer, exports, exportedAt);

        foreach (var export in exports)
        {
            foreach (var attachment in export.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var input = new FileStream(
                    attachment.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var output = writer.CreateEntry(attachment.EntryName);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }

        writer.Complete();
        var bytes = new FileInfo(zipPath).Length;
        var report = new ExportReport
        {
            Path = Path.GetFullPath(zipPath),
            Sessions = exports.Count,
            Messages = messageCount,
            Files = fileCount,
            Bytes = bytes,
            Warnings = warnings.ToArray(),
        };
        progress?.Report(report);
        return report;
    }

    private static void WriteJsonEntry(ChatDemoZipWriter writer, string entryName, Action<Utf8JsonWriter> write)
    {
        using var stream = writer.CreateEntry(entryName);
        using var json = new Utf8JsonWriter(stream);
        write(json);
    }

    private static void WriteSessionEntry(ChatDemoZipWriter writer, ExportSession export, string entryName)
    {
        var session = export.Session;
        using var stream = writer.CreateEntry(entryName);
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteString("id", session.Id);
        json.WriteString("title", session.Title);
        WriteNullableString(json, "provider_id", session.ProviderId);
        WriteNullableString(json, "model", session.Model);
        WriteNullableString(json, "system_prompt", session.SystemPrompt);
        if (session.CurrentMessageId is { } current) json.WriteNumber("current_message_id", current);
        else json.WriteNull("current_message_id");
        json.WriteBoolean("pinned", session.Pinned);
        json.WriteString("created_at", session.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("updated_at", session.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        json.WriteStartArray("messages");
        foreach (var message in export.Messages)
        {
            json.WriteStartObject();
            json.WriteNumber("id", message.Id);
            if (message.ParentId is { } parent) json.WriteNumber("parent_id", parent);
            else json.WriteNull("parent_id");
            json.WriteString("role", message.Role == MessageRole.User ? "USER" : "ASSISTANT");
            json.WriteString("status", ChatDemoFormat.ToExportStatus(message.Status, message.RawJson));
            json.WriteString("created_at", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            WriteNullableString(json, "provider_id", message.ProviderId);
            WriteNullableString(json, "model", message.Model);
            json.WriteStartArray("fragments");
            foreach (var part in message.Parts) WriteFragment(json, part, message.Role);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteIndexEntry(ChatDemoZipWriter writer, IReadOnlyList<ExportSession> exports, DateTimeOffset exportedAt)
    {
        WriteJsonEntry(writer, ChatDemoFormat.IndexEntryName, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("export_format_version", ChatDemoFormat.SupportedExportFormatVersion);
            json.WriteString("exported_at", exportedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteStartArray("sessions");
            foreach (var export in exports)
            {
                var session = export.Session;
                json.WriteStartObject();
                json.WriteString("id", session.Id);
                json.WriteString("title", session.Title);
                json.WriteString("created_at", session.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                json.WriteString("updated_at", session.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                if (session.CurrentMessageId is { } current) json.WriteNumber("current_message_id", current);
                else json.WriteNull("current_message_id");
                json.WriteNumber("message_count", export.Messages.Count);
                json.WriteBoolean("pinned", session.Pinned);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    private static void WriteFragment(Utf8JsonWriter json, ContentPart part, MessageRole role)
    {
        if (part.Kind != PartKind.File && ChatDemoJson.TryWriteOriginalFragment(json, part.MetaJson)) return;

        json.WriteStartObject();
        json.WriteString("type", ChatDemoFormat.ToExportFragmentType(part, role));
        // 官方片段类型无法表达 ToolCall/Image/Raw 等本地种类；写入私有标记保证本程序 round-trip 不丢类型。
        if (part.Kind != PartKind.File)
            json.WriteString(ChatDemoFormat.KindPropertyName, part.Kind.ToString());
        switch (part.Kind)
        {
            case PartKind.File:
                var info = ChatDemoJson.ReadFileInfo(part.MetaJson);
                if (info.FileId is not null) WriteId(json, "file_id", info.FileId);
                if (info.Name is not null) json.WriteString("name", info.Name);
                if (info.Size is { } size) json.WriteNumber("size", size);
                if (info.Mime is not null) json.WriteString("mime", info.Mime);
                if (info.Sha256 is not null) json.WriteString("sha256", info.Sha256);
                if (!string.IsNullOrEmpty(part.Text)) json.WriteString("content", part.Text);
                break;
            default:
                if (part.Text is not null) json.WriteString("content", part.Text);
                break;
        }
        if (part.Kind != PartKind.File && part.MetaJson is not null)
        {
            json.WritePropertyName(ChatDemoFormat.MetaPropertyName);
            WriteRawJson(json, part.MetaJson);
        }
        json.WriteEndObject();
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void WriteId(Utf8JsonWriter json, string name, string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            json.WriteNumber(name, numeric);
        else
            json.WriteString(name, value);
    }

    private static void WriteRawJson(Utf8JsonWriter writer, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private sealed record ExportSession(
        Session Session,
        IReadOnlyList<MessageNode> Messages,
        IReadOnlyList<ExportAttachment> Attachments);

    private sealed record ExportAttachment(string EntryName, string SourcePath, long Size);

    private sealed record FileRow(
        string Sha256,
        string RelPath,
        long Size,
        string Ext,
        string? Mime,
        string? OriginalName);
}
