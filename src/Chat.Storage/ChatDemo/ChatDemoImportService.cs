using System.Globalization;
using System.Text.Json;
using Chat.Core;
using Microsoft.Data.Sqlite;

namespace Chat.Storage.ChatDemo;

/// <summary>
/// chat-demo v3 ZIP 的合并导入：
/// 1) 按会话 id upsert，导入前自动 <see cref="SnapshotService.Create(string)"/> 快照；
/// 2) 消息按 (session_id, message id) upsert，本地新增的消息与分支保留；
/// 3) 附件按 sha256 落到 data/files/{sha256}{ext}，重复导入幂等；
/// 4) entry 名在打开 ZIP 时统一校验，绝不按 entry 名解压落盘（zip slip）。
/// </summary>
public sealed class ChatDemoImportService
{
    private const int DefaultMaxWarnings = 200;

    private readonly ChatDatabase _database;
    private readonly FileStore _files;
    private readonly SnapshotService _snapshots;
    private readonly int _maxWarnings;

    public ChatDemoImportService(
        ChatDatabase database,
        string dataDirectory,
        SnapshotService? snapshots = null,
        int maxWarnings = DefaultMaxWarnings)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _database = database;
        _files = new FileStore(dataDirectory);
        _snapshots = snapshots ?? new SnapshotService(database, StoragePaths.SnapshotsDirectory(dataDirectory));
        _maxWarnings = maxWarnings;
    }

    /// <summary>解析 ZIP、落附件、合并写入数据库，返回统计与 warnings。</summary>
    public async Task<ImportReport> ImportAsync(
        string zipPath,
        IProgress<ImportReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var warnings = new WarningSink(_maxWarnings);
        using var reader = ChatDemoZipReader.Open(zipPath);
        warnings.AddRange(reader.Warnings);

        var titles = ChatDemoIndex.ReadTitles(reader.IndexJson, warnings.Add);
        var indexIds = ChatDemoSessionIds.ReadIndexIds(reader.IndexJson);
        var sessions = new List<ChatDemoSession>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var inferredToActual = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var entryIndex = 0; entryIndex < reader.Sessions.Count; entryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Sessions[entryIndex];
            var session = ParseSession(
                reader, entry, entryIndex, reader.Sessions.Count, indexIds, titles, warnings);
            if (session is null) continue;
            inferredToActual[entry.InferredSessionId] = session.Id;
            if (positions.TryGetValue(session.Id, out var position))
            {
                warnings.Add($"ZIP 中会话 id {session.Id} 重复，后出现的文件覆盖先出现的。");
                sessions[position] = session;
            }
            else
            {
                positions[session.Id] = sessions.Count;
                sessions.Add(session);
            }
        }

        WarnAboutIndex(titles, sessions, warnings);
        if (sessions.Count == 0) warnings.Add("ZIP 中没有可导入的会话文件。");

        var snapshotPath = sessions.Count > 0 ? _snapshots.Create("import") : null;
        var fragments = CollectFileFragments(sessions);
        var (attachments, skippedFiles) = await StoreAttachmentsAsync(reader, sessions, inferredToActual, fragments, warnings, cancellationToken)
            .ConfigureAwait(false);
        sessions = ApplyAttachments(sessions, attachments);

        return WriteToDatabase(sessions, attachments, skippedFiles, snapshotPath, warnings, progress, cancellationToken);
    }

    private static ChatDemoSession? ParseSession(
        ChatDemoZipReader reader,
        ChatDemoSessionEntry entry,
        int entryIndex,
        int entryCount,
        IReadOnlyList<string> indexIds,
        IReadOnlyDictionary<string, string> titles,
        WarningSink warnings)
    {
        JsonDocument document;
        try
        {
            using var stream = reader.OpenSession(entry);
            document = JsonDocument.Parse(stream, DocumentOptions);
        }
        catch (JsonException exception)
        {
            warnings.Add($"会话文件 {entry.EntryName} 不是合法 JSON，已跳过：{exception.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                warnings.Add($"会话文件 {entry.EntryName} 的根节点既不是对象也不是数组，已跳过。");
                return null;
            }

            var sessionElement = root;
            if (root.ValueKind == JsonValueKind.Object &&
                ChatDemoJson.GetProperty(root, "session") is { ValueKind: JsonValueKind.Object } nested)
                sessionElement = nested;
            var hasFields = sessionElement.ValueKind == JsonValueKind.Object;

            // 与 App 预检共用 ChatDemoSessionIds：JSON id → 索引按顺序对应的 id → 文件名。
            // 预检与导入必须同源，否则 no-id ZIP 会在确认框里显示/选中错误的会话。
            var id = ChatDemoSessionIds.Resolve(
                hasFields ? ChatDemoSessionIds.ReadSessionJsonId(root) : null,
                indexIds,
                entryIndex,
                entryCount,
                entry.InferredSessionId);
            if (string.IsNullOrWhiteSpace(id))
            {
                warnings.Add($"会话文件 {entry.EntryName} 缺少会话 id，已跳过。");
                return null;
            }
            if (!string.Equals(id, entry.InferredSessionId, StringComparison.Ordinal))
                warnings.Add($"会话文件 {entry.EntryName} 的 id={id} 与文件名不一致，以 JSON/chat_session_index.json 里的 id 为准。");

            var title = hasFields ? ChatDemoJson.GetString(sessionElement, "title", "name", "topic") : null;
            if (string.IsNullOrEmpty(title)) title = titles.GetValueOrDefault(id) ?? "";

            var created = hasFields
                ? ChatDemoJson.GetTimestamp(sessionElement, "created_at", "createdAt", "create_time", "created")
                : null;
            var updated = hasFields
                ? ChatDemoJson.GetTimestamp(sessionElement, "updated_at", "updatedAt", "update_time", "updated")
                : null;
            if (created is null && updated is not null) created = updated;
            if (created is null)
            {
                warnings.Add($"会话 {id} 缺少时间戳，使用 1970-01-01 兜底（能导入但排序会靠后）。");
                created = DateTimeOffset.UnixEpoch;
            }
            updated ??= created;

            var messagesElement = hasFields
                ? ChatDemoJson.GetProperty(sessionElement, "messages", "chat_messages", "chatMessages", "message_list", "messageList", "nodes")
                : (root.ValueKind == JsonValueKind.Array ? root : null);
            if (messagesElement is null && hasFields)
                warnings.Add($"会话 {id} 缺少 messages 数组，按空会话导入。");

            var messageElements = new List<JsonElement>();
            if (messagesElement is { ValueKind: JsonValueKind.Array } array)
                messageElements.AddRange(array.EnumerateArray());
            else if (messagesElement is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } single)
                messageElements.Add(single);

            var usedIds = new HashSet<long>();
            var nextId = 1L;
            var messages = new List<ChatDemoMessage>(messageElements.Count);
            foreach (var element in messageElements)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"会话 {id} 中有非对象消息，已跳过。");
                    continue;
                }

                var candidate = ChatDemoJson.GetInt64(element, "id", "message_id", "messageId");
                if (candidate is null or <= 0)
                {
                    while (usedIds.Contains(nextId)) nextId++;
                    candidate = nextId;
                    warnings.Add($"会话 {id} 的某条消息缺少 id，已分配 {candidate}。");
                }
                else if (usedIds.Contains(candidate.Value))
                {
                    while (usedIds.Contains(nextId)) nextId++;
                    warnings.Add($"会话 {id} 的消息 id={candidate} 重复，已改为 {nextId}。");
                    candidate = nextId;
                }
                var messageId = candidate.Value;
                usedIds.Add(messageId);
                nextId = Math.Max(nextId, messageId + 1);

                var roleRaw = ChatDemoJson.GetString(element, "role", "sender", "author");
                var role = ChatDemoFormat.MapRole(roleRaw, out var roleRecognized);
                if (!roleRecognized)
                    warnings.Add($"会话 {id} 消息 {messageId} 的角色 {roleRaw ?? "(缺失)"} 未识别，按 ASSISTANT 处理。");

                var statusRaw = ChatDemoJson.GetString(element, "status", "message_status");
                var status = ChatDemoFormat.MapStatus(statusRaw, out var statusRecognized);
                if (!statusRecognized)
                    warnings.Add($"会话 {id} 消息 {messageId} 的 status {statusRaw ?? "(缺失)"} 未识别，按 FINISHED 处理。");

                var parentId = ChatDemoJson.GetInt64(element, "parent_id", "parentId", "parent");
                if (parentId is <= 0) parentId = null;
                if (parentId is { } parent && parent >= messageId)
                    warnings.Add($"会话 {id} 消息 {messageId} 的 parent_id={parent} 不小于自身 id。");

                var parts = new List<ContentPart>();
                if (ChatDemoJson.GetProperty(element, "fragments", "parts", "contents", "segments", "message_parts") is { } fragments)
                {
                    if (fragments.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fragment in fragments.EnumerateArray())
                            parts.Add(ParseFragment(fragment, id, messageId, warnings));
                    }
                    else if (fragments.ValueKind != JsonValueKind.Null)
                    {
                        parts.Add(ParseFragment(fragments, id, messageId, warnings));
                    }
                }
                if (parts.Count == 0 &&
                    ChatDemoJson.GetProperty(element, "content", "content_text", "text") is { ValueKind: not JsonValueKind.Null } direct)
                    parts.Add(new ContentPart(PartKind.Text, ChatDemoJson.GetContentText(direct)));

                var messageCreated = ChatDemoJson.GetTimestamp(element, "created_at", "createdAt", "create_time", "created")
                    ?? updated
                    ?? DateTimeOffset.UnixEpoch;

                messages.Add(new ChatDemoMessage
                {
                    Id = messageId,
                    ParentId = parentId,
                    Role = role,
                    Status = status,
                    ProviderId = ChatDemoJson.GetString(element, "provider_id", "providerId", "provider"),
                    Model = ChatDemoJson.GetString(element, "model", "model_id", "modelId"),
                    RawJson = element.GetRawText(),
                    CreatedAt = messageCreated,
                    Parts = parts,
                });
            }

            foreach (var message in messages)
            {
                if (message.ParentId is { } parent && !usedIds.Contains(parent))
                    warnings.Add($"会话 {id} 消息 {message.Id} 的 parent_id={parent} 不存在（悬空分支）。");
            }

            long? currentId = hasFields
                ? ChatDemoJson.GetInt64(sessionElement, "current_message_id", "currentMessageId", "current_id", "leaf_message_id")
                : null;
            if (currentId is <= 0) currentId = null;
            if (currentId is { } current && !usedIds.Contains(current))
            {
                warnings.Add($"会话 {id} 的 current_message_id={current} 不在消息树里，改用最大消息 id。");
                currentId = null;
            }
            currentId ??= messages.Count > 0 ? messages.Max(message => message.Id) : null;

            var pinned = hasFields ? ChatDemoJson.GetBoolean(sessionElement, "pinned", "is_pinned", "isPinned") : null;

            return new ChatDemoSession
            {
                Id = id,
                Title = title ?? "",
                ProviderId = hasFields ? ChatDemoJson.GetString(sessionElement, "provider_id", "providerId", "provider") : null,
                Model = hasFields ? ChatDemoJson.GetString(sessionElement, "model", "model_id", "modelId") : null,
                SystemPrompt = hasFields ? ChatDemoJson.GetString(sessionElement, "system_prompt", "systemPrompt") : null,
                CurrentMessageId = currentId,
                Pinned = pinned,
                CreatedAt = created.GetValueOrDefault(DateTimeOffset.UnixEpoch),
                UpdatedAt = updated.GetValueOrDefault(DateTimeOffset.UnixEpoch),
                Messages = messages,
            };
        }
    }

    private static ContentPart ParseFragment(JsonElement fragment, string sessionId, long messageId, WarningSink warnings)
    {
        if (fragment.ValueKind == JsonValueKind.String)
            return ContentPart.OfText(fragment.GetString() ?? "");
        if (fragment.ValueKind != JsonValueKind.Object)
            return new ContentPart(PartKind.Text, ChatDemoJson.GetContentText(fragment));

        var fragmentType = ChatDemoJson.GetString(fragment, "type", "kind", "fragment_type", "fragmentType");
        var localKindText = ChatDemoJson.GetString(fragment, ChatDemoFormat.KindPropertyName);
        PartKind localKind = default;
        var hasLocalKind = !string.IsNullOrWhiteSpace(localKindText) &&
                           Enum.TryParse(localKindText, ignoreCase: true, out localKind);
        var recognized = false;
        var kind = hasLocalKind ? localKind : ChatDemoFormat.MapFragmentKind(fragmentType, out recognized);
        if (!hasLocalKind && !recognized)
            warnings.Add($"会话 {sessionId} 消息 {messageId} 的片段类型 {fragmentType} 未识别，按 RAW 保留。");

        var text = ChatDemoJson.GetContentText(
            ChatDemoJson.GetProperty(fragment, "content", "text", "value", "result", "body", "data"));
        var original = fragment.GetRawText();
        var embeddedMeta = ReadEmbeddedMeta(fragment);
        switch (kind)
        {
            case PartKind.File:
                var fileId = ChatDemoJson.GetString(fragment, "file_id", "fileId", "id");
                var name = ChatDemoJson.GetString(fragment, "name", "file_name", "fileName", "filename");
                var mime = ChatDemoJson.GetString(fragment, "mime", "mime_type", "mimeType", "content_type", "contentType");
                var size = ChatDemoJson.GetInt64(fragment, "size", "file_size", "fileSize");
                return new ContentPart(kind, text, ChatDemoJson.BuildFileMeta(original, fileId, name, mime, size, null, null));
            case PartKind.ToolCall:
            case PartKind.ToolResult:
            case PartKind.Risk:
            case PartKind.Raw:
            case PartKind.Image:
                return new ContentPart(kind, text, embeddedMeta ?? ChatDemoJson.BuildFragmentMeta(fragmentType, original));
            default:
                return new ContentPart(kind, text, embeddedMeta);
        }
    }

    private static string? ReadEmbeddedMeta(JsonElement fragment)
    {
        if (ChatDemoJson.GetProperty(fragment, ChatDemoFormat.MetaPropertyName) is not { } meta) return null;
        return meta.ValueKind == JsonValueKind.String ? meta.GetString() : meta.GetRawText();
    }

    private static List<FileFragment> CollectFileFragments(IReadOnlyList<ChatDemoSession> sessions)
    {
        var list = new List<FileFragment>();
        foreach (var session in sessions)
        {
            foreach (var message in session.Messages)
            {
                foreach (var part in message.Parts)
                {
                    if (part.Kind != PartKind.File) continue;
                    var info = ChatDemoJson.ReadFileInfo(part.MetaJson);
                    list.Add(new FileFragment(session.Id, info.FileId, info.Name, info.Mime, message.Id));
                }
            }
        }
        return list;
    }

    private async Task<(List<ImportedAttachment> Attachments, int Skipped)> StoreAttachmentsAsync(
        ChatDemoZipReader reader,
        IReadOnlyList<ChatDemoSession> sessions,
        IReadOnlyDictionary<string, string> inferredToActual,
        IReadOnlyList<FileFragment> fragments,
        WarningSink warnings,
        CancellationToken cancellationToken)
    {
        var result = new List<ImportedAttachment>();
        var skipped = 0;
        var sessionIds = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var attachment in reader.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = inferredToActual.GetValueOrDefault(attachment.SessionId, attachment.SessionId);
            var strippedName = StripFileIdPrefix(attachment.FileName, attachment.FileId);
            var fragment = fragments.FirstOrDefault(candidate =>
                string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal) &&
                ((attachment.FileId is not null && string.Equals(candidate.FileId, attachment.FileId, StringComparison.Ordinal)) ||
                 (candidate.Name is not null && string.Equals(candidate.Name, strippedName, StringComparison.OrdinalIgnoreCase))));

            if (!sessionIds.Contains(sessionId) || fragment is null)
            {
                // 没有对应 FILE 片段/消息的附件无法建立引用；不落盘也不写 files 表，避免悬挂行与 FK 失败。
                var reason = !sessionIds.Contains(sessionId)
                    ? $"所属会话 {sessionId} 不在本次导入中"
                    : "没有被任何 FILE 片段引用";
                warnings.Add($"附件 {attachment.EntryName} {reason}，已跳过。");
                skipped++;
                continue;
            }

            var mime = fragment.Mime;
            var originalName = fragment.Name ?? strippedName;
            await using var source = reader.OpenAttachment(attachment);
            var stored = await _files.SaveAsync(source, originalName, mime, cancellationToken).ConfigureAwait(false);
            result.Add(new ImportedAttachment
            {
                SessionId = sessionId,
                FileId = attachment.FileId ?? fragment.FileId,
                OriginalName = originalName,
                Sha256 = stored.Sha256,
                RelativePath = stored.RelativePath,
                Size = stored.Size,
                Ext = stored.Extension,
                Mime = mime,
                IsNew = stored.IsNew,
            });
        }
        return (result, skipped);
    }

    private static List<ChatDemoSession> ApplyAttachments(
        IReadOnlyList<ChatDemoSession> sessions,
        IReadOnlyList<ImportedAttachment> attachments)
    {
        var byFileId = new Dictionary<(string SessionId, string FileId), ImportedAttachment>();
        var byName = new Dictionary<(string SessionId, string Name), ImportedAttachment>();
        foreach (var attachment in attachments)
        {
            if (attachment.FileId is not null)
                byFileId.TryAdd((attachment.SessionId, attachment.FileId), attachment);
            byName.TryAdd((attachment.SessionId, attachment.OriginalName), attachment);
        }

        var updated = new List<ChatDemoSession>(sessions.Count);
        foreach (var session in sessions)
        {
            var messages = new List<ChatDemoMessage>(session.Messages.Count);
            foreach (var message in session.Messages)
            {
                var parts = new List<ContentPart>(message.Parts.Count);
                foreach (var part in message.Parts)
                {
                    if (part.Kind != PartKind.File)
                    {
                        parts.Add(part);
                        continue;
                    }

                    var info = ChatDemoJson.ReadFileInfo(part.MetaJson);
                    ImportedAttachment? match = null;
                    if (info.FileId is not null) byFileId.TryGetValue((session.Id, info.FileId), out match);
                    if (match is null && info.Name is not null) byName.TryGetValue((session.Id, info.Name), out match);
                    if (match is null)
                    {
                        parts.Add(part);
                        continue;
                    }

                    match.MessageId ??= message.Id;
                    parts.Add(new ContentPart(
                        part.Kind,
                        part.Text,
                        ChatDemoJson.BuildFileMeta(
                            originalJson: ChatDemoJson.ReadOriginalFragment(part.MetaJson),
                            fileId: info.FileId ?? match.FileId,
                            name: info.Name ?? match.OriginalName,
                            mime: info.Mime ?? match.Mime,
                            size: info.Size ?? match.Size,
                            sha256: match.Sha256,
                            relativePath: match.RelativePath)));
                }
                messages.Add(message with { Parts = parts });
            }
            updated.Add(session with { Messages = messages });
        }
        return updated;
    }

    private ImportReport WriteToDatabase(
        IReadOnlyList<ChatDemoSession> sessions,
        IReadOnlyList<ImportedAttachment> attachments,
        int skippedFiles,
        string? snapshotPath,
        WarningSink warnings,
        IProgress<ImportReport>? progress,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var skipped = 0;
        var addedMessages = 0;
        var updatedMessages = 0;
        var skippedMessages = 0;

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = LoadExistingSession(connection, transaction, session.Id);
            var currentId = ResolveCurrentMessageId(session, existing);
            var pinned = session.Pinned ?? existing?.Pinned ?? false;
            var createdAt = existing is null
                ? session.CreatedAt
                : DateTimeOffset.Parse(existing.CreatedAt, CultureInfo.InvariantCulture);
            var updatedAt = session.UpdatedAt;
            var metadataChanged = existing is null
                || !string.Equals(existing.Title, session.Title, StringComparison.Ordinal)
                || !string.Equals(existing.ProviderId, session.ProviderId, StringComparison.Ordinal)
                || !string.Equals(existing.Model, session.Model, StringComparison.Ordinal)
                || !string.Equals(existing.SystemPrompt, session.SystemPrompt, StringComparison.Ordinal)
                || existing.CurrentMessageId != currentId
                || existing.Pinned != pinned
                || !string.Equals(existing.UpdatedAt, updatedAt.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                || existing.DeletedAt is not null;

            if (existing is null)
                InsertSession(connection, transaction, session, currentId, pinned, createdAt, updatedAt);
            else if (metadataChanged)
                UpdateSession(connection, transaction, session, currentId, pinned, updatedAt);

            var messageChanged = false;
            foreach (var message in session.Messages)
            {
                if (existing is not null && existing.Messages.TryGetValue(message.Id, out var current))
                {
                    if (current.Matches(message))
                    {
                        skippedMessages++;
                        continue;
                    }
                    UpdateMessage(connection, transaction, session.Id, message);
                    updatedMessages++;
                }
                else
                {
                    InsertMessage(connection, transaction, session.Id, message);
                    addedMessages++;
                }
                messageChanged = true;
            }

            if (existing is null) added++;
            else if (metadataChanged || messageChanged) updated++;
            else skipped++;

            progress?.Report(CreateReport(
                added, updated, skipped, addedMessages, updatedMessages, skippedMessages,
                attachments, skippedFiles, snapshotPath, warnings));
        }

        foreach (var attachment in attachments.DistinctBy(candidate => candidate.Sha256))
            UpsertFile(connection, transaction, attachment);

        transaction.Commit();

        var report = CreateReport(
            added, updated, skipped, addedMessages, updatedMessages, skippedMessages,
            attachments, skippedFiles, snapshotPath, warnings);
        progress?.Report(report);
        return report;
    }

    private static ImportReport CreateReport(
        int added,
        int updated,
        int skipped,
        int addedMessages,
        int updatedMessages,
        int skippedMessages,
        IReadOnlyList<ImportedAttachment> attachments,
        int skippedFiles,
        string? snapshotPath,
        WarningSink warnings)
    {
        var distinct = attachments.DistinctBy(candidate => candidate.Sha256).ToList();
        var files = distinct.Count(candidate => candidate.IsNew);
        return new ImportReport
        {
            Added = added,
            Updated = updated,
            Skipped = skipped,
            AddedMessages = addedMessages,
            UpdatedMessages = updatedMessages,
            SkippedMessages = skippedMessages,
            Files = files,
            ReusedFiles = distinct.Count - files,
            SkippedFiles = skippedFiles,
            SnapshotPath = snapshotPath,
            Warnings = warnings.Items.ToArray(),
        };
    }

    private static ExistingSession? LoadExistingSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        string title;
        string? providerId;
        string? model;
        string? systemPrompt;
        long? currentId;
        bool pinned;
        string createdAt;
        string updatedAt;
        string? deletedAt;

        using (var command = CreateCommand(
                   connection,
                   transaction,
                   "SELECT title, provider_id, model, system_prompt, current_message_id, pinned, created_at, updated_at, deleted_at FROM sessions WHERE id = $id"))
        {
            command.Parameters.AddWithValue("$id", sessionId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            title = reader.GetString(0);
            providerId = reader.IsDBNull(1) ? null : reader.GetString(1);
            model = reader.IsDBNull(2) ? null : reader.GetString(2);
            systemPrompt = reader.IsDBNull(3) ? null : reader.GetString(3);
            currentId = reader.IsDBNull(4) ? null : reader.GetInt64(4);
            pinned = reader.GetInt64(5) != 0;
            createdAt = reader.GetString(6);
            updatedAt = reader.GetString(7);
            deletedAt = reader.IsDBNull(8) ? null : reader.GetString(8);
        }

        var messages = new Dictionary<long, ExistingMessage>();
        using (var command = CreateCommand(
                   connection,
                   transaction,
                   "SELECT id, parent_id, role, status, provider_id, model, raw_json, created_at FROM messages WHERE session_id = $sid ORDER BY id"))
        {
            command.Parameters.AddWithValue("$sid", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var messageId = reader.GetInt64(0);
                messages[messageId] = new ExistingMessage(
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    ParseRole(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    ParseStatus(reader.IsDBNull(3) ? null : reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? "" : reader.GetString(7));
            }
        }

        using (var command = CreateCommand(
                   connection,
                   transaction,
                   "SELECT message_id, kind, text, meta_json FROM parts WHERE session_id = $sid ORDER BY message_id, seq"))
        {
            command.Parameters.AddWithValue("$sid", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!messages.TryGetValue(reader.GetInt64(0), out var message)) continue;
                message.Parts.Add(new ContentPart(
                    ParsePartKind(reader.IsDBNull(1) ? null : reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return new ExistingSession(
            title,
            providerId,
            model,
            systemPrompt,
            currentId,
            pinned,
            createdAt,
            updatedAt,
            deletedAt,
            messages);
    }

    private static long? ResolveCurrentMessageId(ChatDemoSession session, ExistingSession? existing)
    {
        var importedIds = session.Messages.Select(message => message.Id).ToHashSet();
        long? imported = session.CurrentMessageId is { } current && importedIds.Contains(current)
            ? current
            : session.Messages.Count > 0
                ? session.Messages.Max(message => message.Id)
                : null;
        if (existing?.CurrentMessageId is { } localCurrent &&
            existing.Messages.ContainsKey(localCurrent) &&
            !importedIds.Contains(localCurrent))
            return localCurrent;
        return imported;
    }

    private static void InsertSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatDemoSession session,
        long? currentId,
        bool pinned,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO sessions (id, title, provider_id, model, system_prompt, current_message_id, pinned, created_at, updated_at, deleted_at)
            VALUES ($id, $title, $provider, $model, $system, $current, $pinned, $created, $updated, NULL)
            """);
        AddSessionParameters(command, session, currentId, pinned, updatedAt);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void UpdateSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatDemoSession session,
        long? currentId,
        bool pinned,
        DateTimeOffset updatedAt)
    {
        using var command = CreateCommand(connection, transaction, """
            UPDATE sessions
            SET title = $title, provider_id = $provider, model = $model, system_prompt = $system,
                current_message_id = $current, pinned = $pinned, updated_at = $updated, deleted_at = NULL
            WHERE id = $id
            """);
        AddSessionParameters(command, session, currentId, pinned, updatedAt);
        command.ExecuteNonQuery();
    }

    private static void AddSessionParameters(
        SqliteCommand command,
        ChatDemoSession session,
        long? currentId,
        bool pinned,
        DateTimeOffset updatedAt)
    {
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$title", session.Title);
        AddParameter(command, "$provider", session.ProviderId);
        AddParameter(command, "$model", session.Model);
        AddParameter(command, "$system", session.SystemPrompt);
        AddParameter(command, "$current", currentId);
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void InsertMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        ChatDemoMessage message)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO messages (id, session_id, parent_id, role, status, provider_id, model, raw_json, created_at)
            VALUES ($id, $sid, $parent, $role, $status, $provider, $model, $raw, $created)
            """);
        AddMessageParameters(command, sessionId, message);
        command.ExecuteNonQuery();
        InsertParts(connection, transaction, sessionId, message);
    }

    private static void UpdateMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        ChatDemoMessage message)
    {
        using (var command = CreateCommand(connection, transaction, """
                   UPDATE messages
                   SET parent_id = $parent, role = $role, status = $status, provider_id = $provider,
                       model = $model, raw_json = $raw, created_at = $created
                   WHERE session_id = $sid AND id = $id
                   """))
        {
            AddMessageParameters(command, sessionId, message);
            command.ExecuteNonQuery();
        }

        using (var delete = CreateCommand(
                   connection,
                   transaction,
                   "DELETE FROM parts WHERE session_id = $sid AND message_id = $id"))
        {
            delete.Parameters.AddWithValue("$sid", sessionId);
            delete.Parameters.AddWithValue("$id", message.Id);
            delete.ExecuteNonQuery();
        }

        InsertParts(connection, transaction, sessionId, message);
    }

    private static void InsertParts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        ChatDemoMessage message)
    {
        var sequence = 0;
        foreach (var part in message.Parts)
        {
            using var command = CreateCommand(connection, transaction, """
                INSERT INTO parts (session_id, message_id, seq, kind, text, meta_json)
                VALUES ($sid, $mid, $seq, $kind, $text, $meta)
                """);
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$mid", message.Id);
            command.Parameters.AddWithValue("$seq", sequence);
            command.Parameters.AddWithValue("$kind", part.Kind.ToString());
            AddParameter(command, "$text", part.Text);
            AddParameter(command, "$meta", part.MetaJson);
            command.ExecuteNonQuery();
            sequence++;
        }
    }

    private static void AddMessageParameters(SqliteCommand command, string sessionId, ChatDemoMessage message)
    {
        command.Parameters.AddWithValue("$id", message.Id);
        command.Parameters.AddWithValue("$sid", sessionId);
        AddParameter(command, "$parent", message.ParentId);
        command.Parameters.AddWithValue("$role", message.Role.ToString());
        command.Parameters.AddWithValue("$status", message.Status.ToString());
        AddParameter(command, "$provider", message.ProviderId);
        AddParameter(command, "$model", message.Model);
        AddParameter(command, "$raw", message.RawJson);
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void UpsertFile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportedAttachment attachment)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO files (sha256, rel_path, size, ext, mime, original_name, session_id, message_id, created_at)
            VALUES ($sha, $rel, $size, $ext, $mime, $name, $sid, $mid, $created)
            ON CONFLICT(sha256) DO UPDATE SET
                rel_path = excluded.rel_path,
                size = excluded.size,
                ext = excluded.ext,
                mime = COALESCE(mime, excluded.mime),
                original_name = COALESCE(original_name, excluded.original_name),
                session_id = COALESCE(session_id, excluded.session_id),
                message_id = COALESCE(message_id, excluded.message_id)
            """);
        command.Parameters.AddWithValue("$sha", attachment.Sha256);
        command.Parameters.AddWithValue("$rel", attachment.RelativePath);
        command.Parameters.AddWithValue("$size", attachment.Size);
        command.Parameters.AddWithValue("$ext", attachment.Ext);
        AddParameter(command, "$mime", attachment.Mime);
        AddParameter(command, "$name", attachment.OriginalName);
        AddParameter(command, "$sid", attachment.SessionId);
        AddParameter(command, "$mid", attachment.MessageId);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static MessageRole ParseRole(string? value) =>
        Enum.TryParse<MessageRole>(value, ignoreCase: true, out var parsed) ? parsed : MessageRole.Assistant;

    private static MessageStatus ParseStatus(string? value) =>
        Enum.TryParse<MessageStatus>(value, ignoreCase: true, out var parsed) ? parsed : MessageStatus.Finished;

    private static PartKind ParsePartKind(string? value) =>
        Enum.TryParse<PartKind>(value, ignoreCase: true, out var parsed) ? parsed : PartKind.Text;

    private static string StripFileIdPrefix(string fileName, string? fileId)
    {
        if (!string.IsNullOrEmpty(fileId) && fileName.StartsWith(fileId + "_", StringComparison.Ordinal))
            return fileName[(fileId.Length + 1)..];
        return fileName;
    }

    private static void WarnAboutIndex(
        IReadOnlyDictionary<string, string> titles,
        IReadOnlyList<ChatDemoSession> sessions,
        WarningSink warnings)
    {
        if (titles.Count == 0) return;
        var ids = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        var missing = titles.Keys.Count(id => !ids.Contains(id));
        if (missing > 0)
            warnings.Add($"chat_session_index.json 有 {missing} 个会话在 ZIP 中缺少会话文件，已跳过。");
    }

    private sealed class WarningSink
    {
        private readonly List<string> _items = [];
        private readonly int _limit;

        public WarningSink(int limit) => _limit = Math.Max(1, limit);

        public IReadOnlyList<string> Items => _items;

        public void Add(string message)
        {
            if (_items.Count < _limit)
            {
                _items.Add(message);
            }
            else if (_items.Count == _limit)
            {
                _items.Add($"警告超过 {_limit} 条，其余省略。");
            }
        }

        public void AddRange(IEnumerable<string> messages)
        {
            foreach (var message in messages) Add(message);
        }
    }

    private sealed record FileFragment(string SessionId, string? FileId, string? Name, string? Mime, long MessageId);

    private sealed class ImportedAttachment
    {
        public required string SessionId { get; init; }

        public string? FileId { get; init; }

        public required string OriginalName { get; init; }

        public required string Sha256 { get; init; }

        public required string RelativePath { get; init; }

        public required long Size { get; init; }

        public required string Ext { get; init; }

        public string? Mime { get; init; }

        public bool IsNew { get; init; }

        /// <summary>引用该附件的消息 id（来自 FILE 片段）；未被引用时为 null。</summary>
        public long? MessageId { get; set; }
    }

    private sealed record ExistingSession(
        string Title,
        string? ProviderId,
        string? Model,
        string? SystemPrompt,
        long? CurrentMessageId,
        bool Pinned,
        string CreatedAt,
        string UpdatedAt,
        string? DeletedAt,
        Dictionary<long, ExistingMessage> Messages);

    private sealed class ExistingMessage
    {
        private readonly long? _parentId;
        private readonly MessageRole _role;
        private readonly MessageStatus _status;
        private readonly string? _providerId;
        private readonly string? _model;
        private readonly string? _rawJson;
        private readonly string _createdAt;

        public ExistingMessage(
            long? parentId,
            MessageRole role,
            MessageStatus status,
            string? providerId,
            string? model,
            string? rawJson,
            string createdAt)
        {
            _parentId = parentId;
            _role = role;
            _status = status;
            _providerId = providerId;
            _model = model;
            _rawJson = rawJson;
            _createdAt = createdAt;
        }

        public List<ContentPart> Parts { get; } = [];

        public bool Matches(ChatDemoMessage message) =>
            _parentId == message.ParentId &&
            _role == message.Role &&
            _status == message.Status &&
            string.Equals(_providerId, message.ProviderId, StringComparison.Ordinal) &&
            string.Equals(_model, message.Model, StringComparison.Ordinal) &&
            string.Equals(_rawJson, message.RawJson, StringComparison.Ordinal) &&
            string.Equals(_createdAt, message.CreatedAt.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            Parts.SequenceEqual(message.Parts);
    }

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>chat_session_index.json 的容错解析：只用来补标题，不参与会话发现。</summary>
internal static class ChatDemoIndex
{
    public static IReadOnlyDictionary<string, string> ReadTitles(string? json, Action<string> warn)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return titles;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = document.RootElement;
            JsonElement? list = null;
            if (root.ValueKind == JsonValueKind.Array)
            {
                list = root;
            }
            else if (ChatDemoJson.GetProperty(root, "sessions", "items", "data", "list") is { ValueKind: JsonValueKind.Array } property)
            {
                list = property;
            }

            if (list is not { } items)
            {
                warn("chat_session_index.json 结构无法识别，忽略标题索引。");
                return titles;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ChatDemoJson.GetString(item, "id", "session_id", "sessionId");
                var title = ChatDemoJson.GetString(item, "title", "name", "topic");
                if (!string.IsNullOrEmpty(id) && title is not null) titles[id] = title;
            }
        }
        catch (JsonException)
        {
            warn("chat_session_index.json 不是合法 JSON，忽略标题索引。");
        }
        return titles;
    }
}
