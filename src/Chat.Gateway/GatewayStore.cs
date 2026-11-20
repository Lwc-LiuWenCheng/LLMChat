using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chat.Core;
using Chat.Storage;
using Chat.Storage.ChatDemo;

namespace Chat.Gateway;

/// <summary>
/// 网关持久层：会话 JSON 与 chat-demo v3 ZIP 之间做无损桥接，最终写入统一走
/// <see cref="ChatDemoArchive.ImportAsync(string,string,IProgress{ImportReport},CancellationToken)"/>，
/// 复用现有 SQLite schema、幂等导入、快照与附件去重逻辑。
/// </summary>
public sealed class GatewayStore
{
    public string DataDirectory => _dataDirectory;

    private readonly string _dataDirectory;
    private readonly FileStore _files;

    public GatewayStore(string dataDirectory)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
        new ChatDatabase(StoragePaths.DatabaseFile(_dataDirectory)).Initialize();
        _files = new FileStore(_dataDirectory);
    }

    public IReadOnlyList<StoredSession> ListSessions()
    {
        var database = OpenDatabase();
        var sessions = new SessionRepository(database).List();
        var messages = new MessageRepository(database);
        return sessions.Select(session => new StoredSession(session, messages.ListBySession(session.Id))).ToArray();
    }

    public StoredSession? GetSession(string id)
    {
        var database = OpenDatabase();
        if (IsDeleted(database, id)) return null;
        var session = new SessionRepository(database).Get(id);
        if (session is null) return null;
        return new StoredSession(session, new MessageRepository(database).ListBySession(id));
    }

    private static bool IsDeleted(ChatDatabase database, string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT deleted_at FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is not null && value is not DBNull;
    }

    public void RenameSession(string id, string title)
    {
        var repository = new SessionRepository(OpenDatabase());
        var session = repository.Get(id) ?? throw new GatewayBadRequestException("会话不存在");
        repository.Upsert(session with { Title = title, UpdatedAt = DateTimeOffset.UtcNow });
    }

    public void SoftDeleteSession(string id) => new SessionRepository(OpenDatabase()).SoftDelete(id);

    public async Task<StoredFile> SaveFileAsync(Stream source, string? name, string? mime, CancellationToken cancellationToken)
        => await _files.SaveAsync(source, name, mime, cancellationToken).ConfigureAwait(false);

    public bool TryResolveFile(string fileId, out string path, out string? mime)
    {
        path = "";
        mime = null;
        if (!IsSha256(fileId)) return false;
        try
        {
            var database = new ChatDatabase(StoragePaths.DatabaseFile(_dataDirectory));
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rel_path, mime FROM files WHERE sha256 = $id";
            command.Parameters.AddWithValue("$id", fileId.ToLowerInvariant());
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                path = _files.ResolvePath(reader.GetString(0));
                mime = reader.IsDBNull(1) ? null : reader.GetString(1);
                return true;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            return false;
        }

        // 单独经 POST /api/files 上传的文件只落盘、尚未进 files 表（要等会话导入才登记）。
        try
        {
            var candidate = Directory.EnumerateFiles(_files.Root, fileId + "*").FirstOrDefault();
            if (candidate is null || !File.Exists(candidate)) return false;
            path = candidate;
            mime = Path.GetExtension(candidate) is { Length: > 0 } extension
                ? extension.ToLowerInvariant() switch
                {
                    ".txt" => "text/plain",
                    ".json" => "application/json",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => null,
                }
                : null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task SaveSessionAsync(
        string id,
        JsonElement clientSession,
        IReadOnlyDictionary<string, byte[]> blobs,
        IReadOnlyList<GatewayFilePayload> files,
        CancellationToken cancellationToken)
    {
        var session = ToArchiveSession(id, clientSession, files);
        var scratch = Path.Combine(Path.GetTempPath(), "llmchat-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var zipPath = Path.Combine(scratch, "session.zip");
            await WriteArchiveAsync(zipPath, session, blobs, cancellationToken).ConfigureAwait(false);
            var report = await ChatDemoArchive.ImportAsync(zipPath, _dataDirectory, null, cancellationToken).ConfigureAwait(false);
            if (report.Warnings.Count > 0)
                throw new GatewayBadRequestException(string.Join("；", report.Warnings));
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    private ChatDatabase OpenDatabase()
    {
        var database = new ChatDatabase(StoragePaths.DatabaseFile(_dataDirectory));
        database.Initialize();
        return database;
    }

    private static async Task WriteArchiveAsync(
        string zipPath,
        GatewayArchiveSession session,
        IReadOnlyDictionary<string, byte[]> blobs,
        CancellationToken cancellationToken)
    {
        using var writer = ChatDemoZipWriter.Create(zipPath);
        WriteJson(writer, ChatDemoFormat.ManifestEntryName, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("export_format_version", ChatDemoFormat.SupportedExportFormatVersion);
            json.WriteString("exported_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("generator", "Chat.Gateway");
            json.WriteNumber("session_count", 1);
            json.WriteNumber("message_count", session.Messages.Count);
            json.WriteNumber("file_count", session.Attachments.Count);
            json.WriteEndObject();
        });

        var safeId = ChatDemoFormat.SanitizeFileName(session.Id, "session");
        WriteJson(writer, ChatDemoFormat.SessionsRoot + "/" + safeId + ".json", json => WriteSession(json, session));
        WriteJson(writer, ChatDemoFormat.IndexEntryName, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("export_format_version", ChatDemoFormat.SupportedExportFormatVersion);
            json.WriteString("exported_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.WriteStartArray("sessions");
            json.WriteStartObject();
            json.WriteString("id", session.Id);
            json.WriteString("title", session.Title);
            json.WriteString("created_at", session.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("updated_at", session.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteNumber("current_message_id", session.CurrentMessageId);
            json.WriteNumber("message_count", session.Messages.Count);
            json.WriteBoolean("pinned", session.Pinned);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        });

        foreach (var file in session.Attachments)
        {
            var entryName = ChatDemoFormat.SessionsRoot + "/" + safeId + ChatDemoFormat.FilesFolderSuffix + "/" +
                            ChatDemoFormat.SanitizeFileName(file.Id, "file") + "_" +
                            ChatDemoFormat.SanitizeFileName(file.Name, "file");
            using var stream = writer.CreateEntry(entryName);
            if (blobs.TryGetValue(file.Id, out var bytes))
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        writer.Complete();
    }

    private static void WriteSession(Utf8JsonWriter json, GatewayArchiveSession session)
    {
        json.WriteStartObject();
        json.WriteString("id", session.Id);
        json.WriteString("title", session.Title);
        json.WriteString("provider_id", session.Model is null ? null : "deepseek");
        json.WriteString("model", session.Model);
        json.WriteNumber("current_message_id", session.CurrentMessageId);
        json.WriteBoolean("pinned", session.Pinned);
        json.WriteString("created_at", session.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("updated_at", session.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        json.WriteStartArray("messages");
        foreach (var message in session.Messages)
        {
            json.WriteStartObject();
            json.WriteNumber("id", message.Id);
            if (message.ParentId is { } parent) json.WriteNumber("parent_id", parent);
            else json.WriteNull("parent_id");
            json.WriteString("role", message.Role == MessageRole.User ? "USER" : "ASSISTANT");
            json.WriteString("status", message.Status);
            json.WriteString("created_at", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("model", message.Model);
            json.WriteStartArray("fragments");
            foreach (var fragment in message.Fragments) fragment.Write(json);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteJson(ChatDemoZipWriter writer, string entryName, Action<Utf8JsonWriter> write)
    {
        using var stream = writer.CreateEntry(entryName);
        using var json = new Utf8JsonWriter(stream);
        write(json);
    }

    private static GatewayArchiveSession ToArchiveSession(
        string id,
        JsonElement session,
        IReadOnlyList<GatewayFilePayload> files)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var created = Millis(session, "createdAt", now);
        var updated = Millis(session, "updatedAt", created);
        var title = Text(session, "title");
        var messagesElement = session.TryGetProperty("messages", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray()
            : [];

        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var message in messagesElement) byId[Text(message, "id")] = message;
        var numbers = new Dictionary<string, long>(StringComparer.Ordinal);
        var order = new List<string>();
        void Visit(string messageId)
        {
            if (numbers.ContainsKey(messageId) || !byId.ContainsKey(messageId)) return;
            if (byId[messageId].TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.String)
            {
                var parentId = parent.GetString();
                if (!string.IsNullOrEmpty(parentId) && byId.ContainsKey(parentId)) Visit(parentId);
            }
            numbers[messageId] = order.Count + 1;
            order.Add(messageId);
        }
        foreach (var message in messagesElement) Visit(Text(message, "id"));

        var fileById = files.ToDictionary(file => file.Id ?? "", file => file, StringComparer.Ordinal);
        var archiveMessages = new List<GatewayArchiveMessage>(order.Count);
        var attachments = new List<GatewayArchiveFile>();
        foreach (var messageId in order)
        {
            var message = byId[messageId];
            var role = Text(message, "role") == "user" ? MessageRole.User : MessageRole.Assistant;
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(Millis(message, "createdAt", created));
            long? parentId = null;
            if (message.TryGetProperty("parentId", out var parentElement) && parentElement.ValueKind == JsonValueKind.String)
            {
                var parentKey = parentElement.GetString();
                if (!string.IsNullOrEmpty(parentKey) && numbers.TryGetValue(parentKey, out var number)) parentId = number;
            }

            var fragments = new List<GatewayFragment>();
            if (role == MessageRole.User)
            {
                if (message.TryGetProperty("files", out var filesArray) && filesArray.ValueKind == JsonValueKind.Array)
                {
                    var entries = new List<GatewayArchiveFile>();
                    foreach (var item in filesArray.EnumerateArray())
                    {
                        var fileId = Text(item, "id");
                        if (string.IsNullOrEmpty(fileId) || !fileById.TryGetValue(fileId, out var payload)) continue;
                        var name = string.IsNullOrEmpty(payload.Name) ? Text(item, "name", "fileName") : payload.Name!;
                        var mime = string.IsNullOrEmpty(payload.Mime) ? Text(item, "mimeType") : payload.Mime!;
                        var size = Millis(item, "size", 0);
                        var isImage = IsTrue(item, "isImage");
                        var file = new GatewayArchiveFile(fileId, name, mime, size, isImage);
                        entries.Add(file);
                        attachments.Add(file);
                        fragments.Add(GatewayFragment.File(file, isImage));
                    }
                    if (entries.Count > 0) fragments.Add(GatewayFragment.FileGroup(entries));
                }
                fragments.Add(GatewayFragment.Text(Text(message, "content"), "REQUEST"));
            }
            else
            {
                if (message.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in blocks.EnumerateArray())
                    {
                        switch (Text(block, "type"))
                        {
                            case "thinking":
                                fragments.Add(GatewayFragment.Thinking(Text(block, "thinking")));
                                break;
                            case "text":
                                fragments.Add(GatewayFragment.Text(
                                    Text(block, "text"),
                                    IsTrue(block, "isTemplate") ? "TEMPLATE_RESPONSE" : "RESPONSE"));
                                break;
                            case "server_tool_use":
                                fragments.Add(GatewayFragment.Simple("TOOL_CALL", block.GetRawText()));
                                break;
                            default:
                                fragments.Add(GatewayFragment.Simple("RAW", block.GetRawText()));
                                break;
                        }
                    }
                }
            }
            archiveMessages.Add(new GatewayArchiveMessage(
                numbers[messageId], parentId, role, ReplyStatus(Text(message, "status")),
                createdAt, Text(message, "model"), fragments));
        }

        var currentId = ResolveCurrentId(session, numbers, order);
        var model = archiveMessages.FirstOrDefault(message => message.Role == MessageRole.Assistant)?.Model;
        return new GatewayArchiveSession(
            id, title, model is null ? null : "deepseek", model, currentId,
            IsTrue(session, "pinned"),
            DateTimeOffset.FromUnixTimeMilliseconds(created),
            DateTimeOffset.FromUnixTimeMilliseconds(updated),
            archiveMessages, attachments);
    }

    private static long ResolveCurrentId(JsonElement session, IReadOnlyDictionary<string, long> numbers, IReadOnlyList<string> order)
    {
        string? cursor = null;
        if (session.TryGetProperty("rootMessageId", out var rootElement) && rootElement.ValueKind == JsonValueKind.String)
            cursor = rootElement.GetString();
        else if (order.Count > 0) cursor = order[0];

        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (session.TryGetProperty("messages", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in array.EnumerateArray())
            {
                var messageId = Text(message, "id");
                byId[messageId] = message;
                if (message.TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.String)
                {
                    var parentId = parent.GetString();
                    if (!string.IsNullOrEmpty(parentId))
                    {
                        if (!children.TryGetValue(parentId, out var list)) children[parentId] = list = [];
                        list.Add(messageId);
                    }
                }
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(cursor) && byId.ContainsKey(cursor) && visited.Add(cursor))
        {
            var message = byId[cursor];
            if (message.TryGetProperty("selectedChildId", out var child) && child.ValueKind == JsonValueKind.String)
            {
                var next = child.GetString();
                if (!string.IsNullOrEmpty(next) && byId.ContainsKey(next))
                {
                    cursor = next;
                    continue;
                }
            }
            // 单子节点即使没有 selectedChildId 也要继续往下走，才能定位到真正的可见分支末端。
            if (children.TryGetValue(cursor, out var kids) && kids.Count == 1)
            {
                cursor = kids[0];
                continue;
            }
            break;
        }
        return !string.IsNullOrEmpty(cursor) && numbers.TryGetValue(cursor, out var number)
            ? number
            : numbers.Values.DefaultIfEmpty(0).Max();
    }

    private static string Text(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String)
                return item.GetString() ?? "";
        return "";
    }

    private static long Millis(JsonElement value, string name, long fallback)
    {
        if (!value.TryGetProperty(name, out var item)) return fallback;
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number)) return number;
        if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var parsed)) return parsed;
        return fallback;
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string ReplyStatus(string status) => status switch
    {
        "error" => "FAILED",
        "stopped" => "INCOMPLETE",
        "pending" or "streaming" => "WIP",
        _ => "FINISHED",
    };

    private static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record GatewayArchiveSession(
        string Id, string Title, string? ProviderId, string? Model, long CurrentMessageId,
        bool Pinned, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        IReadOnlyList<GatewayArchiveMessage> Messages, IReadOnlyList<GatewayArchiveFile> Attachments);

    private sealed record GatewayArchiveMessage(
        long Id, long? ParentId, MessageRole Role, string Status, DateTimeOffset CreatedAt,
        string? Model, IReadOnlyList<GatewayFragment> Fragments);

    private sealed record GatewayArchiveFile(string Id, string Name, string Mime, long Size, bool IsImage);

    private sealed class GatewayFragment
    {
        private GatewayFragment(Action<Utf8JsonWriter> write) => Write = write;
        public Action<Utf8JsonWriter> Write { get; }

        public static GatewayFragment Text(string text, string type = "RESPONSE") => new(json =>
        {
            json.WriteStartObject();
            json.WriteString("type", type);
            json.WriteString("content", text);
            json.WriteEndObject();
        });

        public static GatewayFragment Thinking(string text) => new(json =>
        {
            json.WriteStartObject();
            json.WriteString("type", "THINK");
            json.WriteString("content", text);
            json.WriteEndObject();
        });

        public static GatewayFragment Simple(string type, string content) => new(json =>
        {
            json.WriteStartObject();
            json.WriteString("type", type);
            json.WriteString("content", content);
            json.WriteEndObject();
        });

        public static GatewayFragment File(GatewayArchiveFile file, bool isImage) => new(json =>
        {
            json.WriteStartObject();
            json.WriteString("id", file.Id);
            json.WriteString("file_name", file.Name);
            json.WriteNumber("file_size", file.Size);
            json.WriteNull("export_path");
            json.WriteBoolean("is_image", isImage);
            json.WriteString("mime_type", file.Mime);
            json.WriteEndObject();
        });

        public static GatewayFragment FileGroup(IReadOnlyList<GatewayArchiveFile> files) => new(json =>
        {
            json.WriteStartObject();
            json.WriteString("type", "FILE");
            json.WritePropertyName("files");
            json.WriteStartArray();
            foreach (var file in files)
            {
                json.WriteStartObject();
                json.WriteString("id", file.Id);
                json.WriteString("file_name", file.Name);
                json.WriteNumber("file_size", file.Size);
                json.WriteNull("export_path");
                json.WriteBoolean("is_image", file.IsImage);
                json.WriteString("mime_type", file.Mime);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        });
    }
}
