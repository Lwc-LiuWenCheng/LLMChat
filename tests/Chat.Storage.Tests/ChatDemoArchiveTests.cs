using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chat.Core;
using Chat.Storage;
using Microsoft.Data.Sqlite;
using Chat.Storage.ChatDemo;
using Xunit;

namespace Chat.Storage.Tests;

public sealed class ChatDemoArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Stamp = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly List<string> _directories = [];

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "llmchat-chatdemo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static string CreateZip(string directory, Action<ZipArchive> write)
    {
        var path = Path.Combine(directory, "export-" + Guid.NewGuid().ToString("N") + ".zip");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        write(archive);
        return path;
    }

    private static void WriteJson(ZipArchive archive, string entryName, string json)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(json);
    }

    private static void WriteBytes(ZipArchive archive, string entryName, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private const string ManifestJson = """{"export_format_version":3,"exported_at":"2024-05-01T12:00:00+00:00"}""";

    [Fact]
    public async Task Import_MergesSessionsMessagesPartsAndStatuses()
    {
        var directory = NewDirectory();
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Chat demo",
              "provider_id": "chat.provider.deepseek",
              "model": "deepseek-flash",
              "current_message_id": 3,
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:05:00Z",
              "messages": [
                {"id":1,"parent_id":null,"role":"USER","status":"FINISHED","created_at":"2024-05-01T12:01:00Z","fragments":[{"type":"REQUEST","content":"hello"}]},
                {"id":2,"parent_id":1,"role":"ASSISTANT","status":"CONTENT_FILTER","created_at":"2024-05-01T12:02:00Z","fragments":[{"type":"THINK","content":"thinking"},{"type":"RESPONSE","content":"filtered"}]},
                {"id":3,"parent_id":2,"role":"ASSISTANT","status":"TIMEOUT","created_at":"2024-05-01T12:03:00Z","fragments":[{"type":"RESPONSE","content":"timeout"}]}
              ]
            }
            """);
            WriteJson(archive, "chat_session_index.json", """{"export_format_version":3,"sessions":[{"id":"s1","title":"Chat demo"}]}""");
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Equal(3, report.AddedMessages);
        Assert.NotNull(report.SnapshotPath);
        Assert.True(File.Exists(report.SnapshotPath));

        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        var session = new SessionRepository(database).Get("s1");
        Assert.NotNull(session);
        Assert.Equal("Chat demo", session!.Title);
        Assert.Equal("deepseek-flash", session.Model);
        Assert.Equal(3, session.CurrentMessageId);

        var nodes = new MessageRepository(database).ListBySession("s1");
        Assert.Equal(3, nodes.Count);
        Assert.Equal(MessageStatus.Finished, nodes[0].Status);
        Assert.Equal(MessageRole.User, nodes[0].Role);
        Assert.Equal("hello", nodes[0].Parts.Single().Text);
        Assert.Equal(MessageStatus.ContentFilter, nodes[1].Status);
        Assert.Equal(new[] { PartKind.Thinking, PartKind.Text }, nodes[1].Parts.Select(part => part.Kind));
        Assert.Equal(MessageStatus.Failed, nodes[2].Status);
        Assert.Equal(2, nodes[2].ParentId);
        Assert.Contains("TIMEOUT", nodes[2].RawJson);
        Assert.Equal(Stamp.Year, session.CreatedAt.Year);
    }

    [Fact]
    public async Task Import_IsIdempotent()
    {
        var directory = NewDirectory();
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Repeat",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:00:00Z",
              "messages": [
                {"id":1,"parent_id":null,"role":"USER","status":"FINISHED","fragments":[{"type":"REQUEST","content":"q"}]},
                {"id":2,"parent_id":1,"role":"ASSISTANT","status":"FINISHED","fragments":[{"type":"RESPONSE","content":"a"}]}
              ]
            }
            """);
        });

        var first = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);
        var second = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Added);
        Assert.Equal(2, first.AddedMessages);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.AddedMessages);
        Assert.Equal(0, second.UpdatedMessages);
        Assert.Equal(2, second.SkippedMessages);

        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        Assert.Equal(2, new MessageRepository(database).ListBySession("s1").Count);
        Assert.Single(new SessionRepository(database).List());
    }

    [Fact]
    public async Task Import_PreservesLocalSessionsAndMessages()
    {
        var directory = NewDirectory();
        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        database.Initialize();
        var sessions = new SessionRepository(database);
        var messages = new MessageRepository(database);
        sessions.Upsert(new Session { Id = "local-1", Title = "Local only" });
        messages.Insert("local-1", new MessageNode { Id = 7, ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("keep me")] });

        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Imported",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:00:00Z",
              "messages": [
                {"id":1,"parent_id":null,"role":"USER","status":"FINISHED","fragments":[{"type":"REQUEST","content":"new"}]}
              ]
            }
            """);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Equal(2, sessions.List().Count);
        Assert.Equal("keep me", messages.ListBySession("local-1").Single().Parts.Single().Text);
        Assert.Single(messages.ListBySession("s1"));
    }

    [Fact]
    public async Task Import_RejectsZipSlipEntries()
    {
        var directory = NewDirectory();
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "../evil.txt", "pwned");
        });
        var outside = Path.Combine(Path.GetDirectoryName(directory)!, "evil.txt");

        await Assert.ThrowsAsync<ChatDemoFormatException>(() => ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(directory, "evil.txt")));
    }

    [Fact]
    public async Task Import_RejectsCorruptedZip()
    {
        var directory = NewDirectory();
        var zip = Path.Combine(directory, "broken.zip");
        await File.WriteAllBytesAsync(zip, "this is not a zip archive"u8.ToArray(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ChatDemoFormatException>(() => ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_ToleratesMissingFieldsAndUnknownEnums()
    {
        var directory = NewDirectory();
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s-missing.json", """
            {
              "messages": [
                {"role":"alien","status":"weird","content":"hello"}
              ]
            }
            """);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.NotEmpty(report.Warnings);

        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        var session = new SessionRepository(database).Get("s-missing");
        Assert.NotNull(session);
        var node = new MessageRepository(database).ListBySession("s-missing").Single();
        Assert.Equal(MessageRole.Assistant, node.Role);
        Assert.Equal(MessageStatus.Finished, node.Status);
        Assert.Equal("hello", node.Parts.Single().Text);
        Assert.Equal(DateTimeOffset.UnixEpoch, session!.CreatedAt);
    }

    [Fact]
    public async Task Import_UsesIndexIdWhenSessionJsonHasNoId()
    {
        var directory = NewDirectory();
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/file-name.json", """
            {
              "title": "No id in JSON",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:05:00Z",
              "messages": [
                {"id":1,"parent_id":null,"role":"USER","status":"FINISHED","fragments":[{"type":"REQUEST","content":"from index"}]}
              ]
            }
            """);
            WriteJson(archive, "chat_session_index.json", """{"export_format_version":3,"sessions":[{"id":"index-session","title":"No id in JSON"}]}""");
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Contains(report.Warnings, warning => warning.Contains("与文件名不一致", StringComparison.Ordinal));

        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        var sessions = new SessionRepository(database);
        var imported = sessions.Get("index-session");
        Assert.NotNull(imported);
        Assert.Equal("No id in JSON", imported!.Title);
        Assert.Null(sessions.Get("file-name"));
        Assert.Equal("from index", new MessageRepository(database).ListBySession("index-session").Single().Parts.Single().Text);
    }

    [Fact]
    public async Task Import_StoresAttachmentsByContentHash()
    {
        var directory = NewDirectory();
        var payload = Encoding.UTF8.GetBytes("hello file");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Files",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:00:00Z",
              "messages": [
                {
                  "id": 1, "parent_id": null, "role": "USER", "status": "FINISHED",
                  "fragments": [
                    {"type": "REQUEST", "content": "see file"},
                    {"type": "FILE", "file_id": "42", "name": "a.txt", "mime": "text/plain", "size": 10}
                  ]
                }
              ]
            }
            """);
            WriteBytes(archive, "chat_sessions/s1_files/42_a.txt", payload);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Files);
        Assert.Equal(1, report.Added);
        var expectedPath = Path.Combine(StoragePaths.FilesDirectory(directory), sha + ".txt");
        Assert.True(File.Exists(expectedPath), "附件应按 sha256 落盘：" + expectedPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(expectedPath, TestContext.Current.CancellationToken));

        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        var filePart = new MessageRepository(database).ListBySession("s1").Single().Parts.Single(part => part.Kind == PartKind.File);
        Assert.Contains(sha, filePart.MetaJson);
        Assert.Contains("files/", filePart.MetaJson);
    }

    [Fact]
    public void Initialize_UpgradesLegacySchemaWithoutDataLoss()
    {
        var directory = NewDirectory();
        var databasePath = StoragePaths.DatabaseFile(directory);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (
                    id TEXT NOT NULL PRIMARY KEY,
                    title TEXT NOT NULL DEFAULT '',
                    provider_id TEXT,
                    model TEXT,
                    system_prompt TEXT,
                    current_message_id INTEGER,
                    pinned INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT);
                INSERT INTO sessions (id, title, created_at, updated_at)
                VALUES ('legacy', 'old data', '2024-01-01T00:00:00+00:00', '2024-01-01T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var database = new ChatDatabase(databasePath);
        database.Initialize();

        using (var connection = database.Open())
        {
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(ChatDatabase.CurrentSchemaVersion, Convert.ToInt32(version.ExecuteScalar()));

            using var files = connection.CreateCommand();
            files.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'files';";
            Assert.Equal(1L, Convert.ToInt64(files.ExecuteScalar()));
        }

        var legacy = new SessionRepository(database).Get("legacy");
        Assert.NotNull(legacy);
        Assert.Equal("old data", legacy!.Title);
    }

    [Fact]
    public async Task Export_RoundTripsThroughV3Archive()
    {
        var source = NewDirectory();
        var payload = Encoding.UTF8.GetBytes("round trip file");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var zip = CreateZip(source, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Round trip",
              "provider_id": "chat.provider.deepseek",
              "model": "deepseek-flash",
              "current_message_id": 2,
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:05:00Z",
              "messages": [
                {"id":1,"parent_id":null,"role":"USER","status":"FINISHED","created_at":"2024-05-01T12:01:00Z","fragments":[{"type":"REQUEST","content":"q"},{"type":"FILE","file_id":"9","name":"b.bin","mime":"application/octet-stream","size":15}]},
                {"id":2,"parent_id":1,"role":"ASSISTANT","status":"FAILED","created_at":"2024-05-01T12:02:00Z","fragments":[{"type":"RESPONSE","content":"a"}]}
              ]
            }
            """);
            WriteBytes(archive, "chat_sessions/s1_files/9_b.bin", payload);
        });

        var first = await ChatDemoArchive.ImportAsync(zip, source, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Added);

        var exportPath = Path.Combine(source, "roundtrip.zip");
        var exported = await ChatDemoArchive.ExportAsync(exportPath, source, null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(exportPath));
        Assert.Equal(1, exported.Sessions);
        Assert.Equal(2, exported.Messages);
        Assert.Equal(1, exported.Files);

        using (var stream = File.OpenRead(exportPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.Equal("manifest.json", archive.Entries[0].FullName);
        }

        var target = NewDirectory();
        var second = await ChatDemoArchive.ImportAsync(exportPath, target, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, second.Added);
        Assert.Equal(2, second.AddedMessages);
        Assert.Equal(1, second.Files);

        var database = new ChatDatabase(StoragePaths.DatabaseFile(target));
        var session = new SessionRepository(database).Get("s1");
        Assert.NotNull(session);
        Assert.Equal("Round trip", session!.Title);
        var nodes = new MessageRepository(database).ListBySession("s1");
        Assert.Equal(2, nodes.Count);
        Assert.Equal(MessageStatus.Failed, nodes[1].Status);
        var filePart = nodes[0].Parts.Single(part => part.Kind == PartKind.File);
        Assert.Contains(sha, filePart.MetaJson);
        Assert.True(File.Exists(Path.Combine(StoragePaths.FilesDirectory(target), sha + ".bin")));
    }

    [Fact]
    public async Task Import_SkipsOrphanAttachmentsInsteadOfFailing()
    {
        var directory = NewDirectory();
        var orphanBytes = Encoding.UTF8.GetBytes("orphan");
        var unreferencedBytes = Encoding.UTF8.GetBytes("unreferenced");
        var validBytes = Encoding.UTF8.GetBytes("valid");
        var validSha = Convert.ToHexStringLower(SHA256.HashData(validBytes));
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Orphan files",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:00:00Z",
              "messages": [
                {
                  "id": 1, "parent_id": null, "role": "USER", "status": "FINISHED",
                  "fragments": [
                    {"type": "REQUEST", "content": "see file"},
                    {"type": "FILE", "file_id": "9", "name": "ok.txt", "mime": "text/plain", "size": 5}
                  ]
                }
              ]
            }
            """);
            WriteBytes(archive, "chat_sessions/orphan_files/7_orphan.txt", orphanBytes);
            WriteBytes(archive, "chat_sessions/s1_files/8_unreferenced.txt", unreferencedBytes);
            WriteBytes(archive, "chat_sessions/s1_files/9_ok.txt", validBytes);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Files);
        Assert.Equal(2, report.SkippedFiles);
        Assert.Contains(report.Warnings, warning => warning.Contains("已跳过", StringComparison.Ordinal));

        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, session_id, message_id FROM files";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(validSha, reader.GetString(0));
        Assert.Equal("s1", reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.False(reader.Read());

        var storedFiles = Directory.GetFiles(StoragePaths.FilesDirectory(directory));
        Assert.Single(storedFiles);
        Assert.Equal(validSha + ".txt", Path.GetFileName(storedFiles[0]));
    }

    [Fact]
    public async Task Import_ToleratesOverflowManifestVersion()
    {
        var directory = NewDirectory();
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", """{"export_format_version":9223372036854775807}""");
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Huge version",
              "messages": [
                {"id":1,"parent_id":null,"role":"USER","status":"FINISHED","fragments":[{"type":"REQUEST","content":"hi"}]}
              ]
            }
            """);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Contains(report.Warnings, warning => warning.Contains("超出", StringComparison.Ordinal));
        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        Assert.Equal("Huge version", new SessionRepository(database).Get("s1")!.Title);
    }

    [Fact]
    public async Task ExportSessions_WithEmptySetExportsNothing()
    {
        var directory = NewDirectory();
        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        database.Initialize();
        new SessionRepository(database).Upsert(new Session { Id = "s1", Title = "Should not export" });
        var zipPath = Path.Combine(directory, "empty.zip");

        var report = await ChatDemoArchive.ExportSessionsAsync(
            zipPath, directory, [], null, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Sessions);
        Assert.Equal(0, report.Messages);
        Assert.Equal(0, report.Files);
        using (var stream = File.OpenRead(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.Equal(2, archive.Entries.Count);
            Assert.Equal("manifest.json", archive.Entries[0].FullName);
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("chat_sessions/", StringComparison.Ordinal));
        }

        var target = NewDirectory();
        var imported = await ChatDemoArchive.ImportAsync(zipPath, target, null, TestContext.Current.CancellationToken);
        Assert.Equal(0, imported.Added);
    }

    [Fact]
    public async Task Export_DisambiguatesCollidingAttachmentEntryNames()
    {
        var directory = NewDirectory();
        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        database.Initialize();
        new SessionRepository(database).Upsert(new Session { Id = "s1", Title = "Collide" });

        var filesDirectory = StoragePaths.FilesDirectory(directory);
        Directory.CreateDirectory(filesDirectory);
        var firstBytes = Encoding.UTF8.GetBytes("one");
        var secondBytes = Encoding.UTF8.GetBytes("two");
        var firstSha = Convert.ToHexStringLower(SHA256.HashData(firstBytes));
        var secondSha = Convert.ToHexStringLower(SHA256.HashData(secondBytes));
        File.WriteAllBytes(Path.Combine(filesDirectory, firstSha + ".txt"), firstBytes);
        File.WriteAllBytes(Path.Combine(filesDirectory, secondSha + ".txt"), secondBytes);
        InsertFileRow(database, firstSha, "same.txt", firstBytes.Length, ".txt", "s1", 1);
        InsertFileRow(database, secondSha, "same.txt", secondBytes.Length, ".txt", "s1", 2);

        var messages = new MessageRepository(database);
        var firstMeta = $$"""{"fragmentType":"FILE","fileId":"1","name":"same.txt","sha256":"{{firstSha}}","relPath":"files/{{firstSha}}.txt"}""";
        var secondMeta = $$"""{"fragmentType":"FILE","fileId":"1","name":"same.txt","sha256":"{{secondSha}}","relPath":"files/{{secondSha}}.txt"}""";
        messages.Insert("s1", new MessageNode
        {
            Id = 1,
            ParentId = null,
            Role = MessageRole.User,
            Parts = [new ContentPart(PartKind.File, null, firstMeta)],
        });
        messages.Insert("s1", new MessageNode
        {
            Id = 2,
            ParentId = 1,
            Role = MessageRole.Assistant,
            Parts = [new ContentPart(PartKind.File, null, secondMeta)],
        });

        var zipPath = Path.Combine(directory, "collide.zip");
        var report = await ChatDemoArchive.ExportAsync(zipPath, directory, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Files);

        using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var attachments = archive.Entries.Where(entry => entry.FullName.Contains("_files/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, attachments.Count);
        Assert.Equal(2, attachments.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var contents = new List<string>();
        foreach (var entry in attachments)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            contents.Add(reader.ReadToEnd());
        }
        Assert.Equal(new[] { "one", "two" }, contents.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Export_RoundTripsEveryPartKind()
    {
        var directory = NewDirectory();
        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        database.Initialize();
        new SessionRepository(database).Upsert(new Session { Id = "rt", Title = "Kinds", CurrentMessageId = 1 });

        var filesDirectory = StoragePaths.FilesDirectory(directory);
        Directory.CreateDirectory(filesDirectory);
        var payload = Encoding.UTF8.GetBytes("file payload");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        File.WriteAllBytes(Path.Combine(filesDirectory, sha + ".bin"), payload);
        InsertFileRow(database, sha, "f.bin", payload.Length, ".bin", "rt", 1);

        var fileMeta = $$"""{"fragmentType":"FILE","fileId":"5","name":"f.bin","mime":"application/octet-stream","size":{{payload.Length}},"sha256":"{{sha}}","relPath":"files/{{sha}}.bin"}""";
        var parts = new List<ContentPart>
        {
            new(PartKind.Text, "plain"),
            new(PartKind.Thinking, "think", """{"reasoning":true}"""),
            new(PartKind.ToolCall, "call", """{"id":"call_1","name":"search"}"""),
            new(PartKind.ToolResult, "result", """{"id":"call_1","result":"ok"}"""),
            new(PartKind.Image, "image", """{"url":"https://example.com/a.png"}"""),
            new(PartKind.File, null, fileMeta),
            new(PartKind.Tip, "tip", """{"tip":true}"""),
            new(PartKind.Risk, "risk", """{"risk":"high"}"""),
            new(PartKind.Raw, "raw", """{"raw":true}"""),
        };
        new MessageRepository(database).Insert("rt", new MessageNode
        {
            Id = 1,
            ParentId = null,
            Role = MessageRole.Assistant,
            Status = MessageStatus.Finished,
            Parts = parts,
        });

        var zipPath = Path.Combine(directory, "kinds.zip");
        await ChatDemoArchive.ExportAsync(zipPath, directory, null, TestContext.Current.CancellationToken);

        var target = NewDirectory();
        var imported = await ChatDemoArchive.ImportAsync(zipPath, target, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, imported.Files);

        var importedDatabase = new ChatDatabase(StoragePaths.DatabaseFile(target));
        var importedParts = new MessageRepository(importedDatabase).ListBySession("rt").Single().Parts;
        Assert.Equal(parts.Select(part => part.Kind), importedParts.Select(part => part.Kind));
        for (var index = 0; index < parts.Count; index++)
        {
            Assert.Equal(parts[index].Text, importedParts[index].Text);
            if (parts[index].Kind != PartKind.File)
                Assert.Equal(parts[index].MetaJson, importedParts[index].MetaJson);
        }
    }

    [Fact]
    public async Task Import_MapsOfficialToolOpenAndFindToToolCallAndRoundTrips()
    {
        var directory = NewDirectory();
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/tools.json", """
            {
              "id": "tools",
              "title": "Official tools",
              "current_message_id": 1,
              "messages": [
                {
                  "id": 1, "parent_id": null, "role": "ASSISTANT", "status": "FINISHED",
                  "fragments": [
                    {"type": "TOOL_OPEN", "content": "open file"},
                    {"type": "TOOL_FIND", "content": "find text"},
                    {"type": "TOOL_SEARCH", "content": "search web"}
                  ]
                }
              ]
            }
            """);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Added);
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("未识别", StringComparison.Ordinal));

        var database = new ChatDatabase(StoragePaths.DatabaseFile(directory));
        var parts = new MessageRepository(database).ListBySession("tools").Single().Parts;
        Assert.Equal(
            new[] { PartKind.ToolCall, PartKind.ToolCall, PartKind.ToolResult },
            parts.Select(part => part.Kind).ToArray());
        Assert.Equal(new[] { "open file", "find text", "search web" }, parts.Select(part => part.Text).ToArray());

        var exportPath = Path.Combine(directory, "official-tools-out.zip");
        await ChatDemoArchive.ExportAsync(exportPath, directory, null, TestContext.Current.CancellationToken);
        string exported;
        using (var stream = File.OpenRead(exportPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        using (var reader = new StreamReader(archive.GetEntry("chat_sessions/tools.json")!.Open(), Encoding.UTF8))
        {
            exported = reader.ReadToEnd();
        }
        Assert.Contains("\"type\":\"TOOL_OPEN\"", exported);
        Assert.Contains("\"type\":\"TOOL_FIND\"", exported);
        Assert.Contains("\"type\":\"TOOL_SEARCH\"", exported);

        var target = NewDirectory();
        await ChatDemoArchive.ImportAsync(exportPath, target, null, TestContext.Current.CancellationToken);
        var reimported = new MessageRepository(new ChatDatabase(StoragePaths.DatabaseFile(target)))
            .ListBySession("tools").Single().Parts;
        Assert.Equal(
            new[] { PartKind.ToolCall, PartKind.ToolCall, PartKind.ToolResult },
            reimported.Select(part => part.Kind).ToArray());
    }

    [Fact]
    public async Task Import_ParsesOfficialNestedFileFragments()
    {
        var directory = NewDirectory();
        var textBytes = Encoding.UTF8.GetBytes("hello");
        var imageBytes = Encoding.UTF8.GetBytes("png123");
        var textSha = Convert.ToHexStringLower(SHA256.HashData(textBytes));
        var imageSha = Convert.ToHexStringLower(SHA256.HashData(imageBytes));
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s1.json", """
            {
              "id": "s1",
              "title": "Official files",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:00:00Z",
              "messages": [
                {
                  "id": 1, "parent_id": null, "role": "USER", "status": "FINISHED",
                  "fragments": [
                    {"type": "REQUEST", "content": "see files"},
                    {
                      "type": "FILE",
                      "files": [
                        {"id": "f1", "file_name": "a.txt", "file_size": 5, "export_path": null, "is_image": false},
                        {"id": "f2", "file_name": "b.png", "file_size": 6, "export_path": null, "is_image": true},
                        {"id": "f3", "file_name": "not-exported.txt", "file_size": 7, "export_path": null, "is_image": false}
                      ]
                    }
                  ]
                }
              ]
            }
            """);
            WriteBytes(archive, "chat_sessions/s1_files/f1_a.txt", textBytes);
            WriteBytes(archive, "chat_sessions/s1_files/f2_b.png", imageBytes);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Equal(2, report.Files);
        Assert.Equal(0, report.SkippedFiles);

        var fileParts = new MessageRepository(new ChatDatabase(StoragePaths.DatabaseFile(directory)))
            .ListBySession("s1").Single().Parts.Where(part => part.Kind == PartKind.File).ToArray();
        Assert.Equal(3, fileParts.Length);
        Assert.Equal(new[] { "f1", "f2", "f3" }, fileParts.Select(ReadMetaString("fileId")).ToArray());
        Assert.Equal(new[] { "a.txt", "b.png", "not-exported.txt" }, fileParts.Select(ReadMetaString("name")).ToArray());
        Assert.Equal(new long?[] { 5, 6, 7 }, fileParts.Select(ReadMetaLong("size")).ToArray());
        Assert.True(ReadMetaBool("isImage")(fileParts[1]));
        Assert.False(ReadMetaBool("isImage")(fileParts[0]));
        Assert.True(File.Exists(Path.Combine(StoragePaths.FilesDirectory(directory), textSha + ".txt")));
        Assert.True(File.Exists(Path.Combine(StoragePaths.FilesDirectory(directory), imageSha + ".png")));
    }

    [Fact]
    public async Task Import_AcceptsSingleNestedObjectFlatFieldsAndMissingFileId()
    {
        var directory = NewDirectory();
        var soloPayload = Encoding.UTF8.GetBytes("solo");
        var flatPayload = Encoding.UTF8.GetBytes("flat");
        var noIdPayload = Encoding.UTF8.GetBytes("noid");
        var noIdSha = Convert.ToHexStringLower(SHA256.HashData(noIdPayload));
        var zip = CreateZip(directory, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/s2.json", """
            {
              "id": "s2",
              "title": "Mixed file shapes",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:00:00Z",
              "messages": [
                {
                  "id": 1, "parent_id": null, "role": "USER", "status": "FINISHED",
                  "fragments": [
                    {"type": "FILE", "files": {"id": "solo", "file_name": "solo.txt", "file_size": 4}},
                    {"type": "FILE", "file_id": "flat", "name": "flat.txt", "mime": "text/plain", "size": 4},
                    {"type": "FILE", "files": [{"file_name": "noid.txt", "file_size": 4}]}
                  ]
                }
              ]
            }
            """);
            WriteBytes(archive, "chat_sessions/s2_files/solo_solo.txt", soloPayload);
            WriteBytes(archive, "chat_sessions/s2_files/flat_flat.txt", flatPayload);
            WriteBytes(archive, "chat_sessions/s2_files/noid_noid.txt", noIdPayload);
        });

        var report = await ChatDemoArchive.ImportAsync(zip, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Files);
        Assert.Equal(0, report.SkippedFiles);
        var parts = new MessageRepository(new ChatDatabase(StoragePaths.DatabaseFile(directory)))
            .ListBySession("s2").Single().Parts.Where(part => part.Kind == PartKind.File).ToArray();
        Assert.Equal(3, parts.Length);
        Assert.Equal(new[] { "solo", "flat", "noid" }, parts.Select(ReadMetaString("fileId")).ToArray());
        Assert.Equal(new[] { "solo.txt", "flat.txt", "noid.txt" }, parts.Select(ReadMetaString("name")).ToArray());
        Assert.Contains("\"mime\":\"text/plain\"", parts[1].MetaJson);
        Assert.True(File.Exists(Path.Combine(StoragePaths.FilesDirectory(directory), noIdSha + ".txt")));
    }

    [Fact]
    public async Task Export_WritesOfficialNestedFilesAndRoundTrips()
    {
        var source = NewDirectory();
        var textBytes = Encoding.UTF8.GetBytes("alpha");
        var imageBytes = Encoding.UTF8.GetBytes("beta");
        var zip = CreateZip(source, archive =>
        {
            WriteJson(archive, "manifest.json", ManifestJson);
            WriteJson(archive, "chat_sessions/nested.json", """
            {
              "id": "nested",
              "title": "Nested export",
              "created_at": "2024-05-01T12:00:00Z",
              "updated_at": "2024-05-01T12:00:00Z",
              "messages": [
                {
                  "id": 1, "parent_id": null, "role": "USER", "status": "FINISHED",
                  "fragments": [
                    {"type": "FILE", "files": [
                      {"id": "f1", "file_name": "a.txt", "file_size": 5, "export_path": null, "is_image": false},
                      {"id": "f2", "file_name": "b.png", "file_size": 4, "export_path": null, "is_image": true}
                    ]}
                  ]
                }
              ]
            }
            """);
            WriteBytes(archive, "chat_sessions/nested_files/f1_a.txt", textBytes);
            WriteBytes(archive, "chat_sessions/nested_files/f2_b.png", imageBytes);
        });

        var first = await ChatDemoArchive.ImportAsync(zip, source, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, first.Files);
        Assert.Equal(0, first.SkippedFiles);

        var exportPath = Path.Combine(source, "nested-export.zip");
        var exported = await ChatDemoArchive.ExportAsync(exportPath, source, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, exported.Files);

        string? sessionJson = null;
        using (var stream = File.OpenRead(exportPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var entry = archive.Entries.Single(item => item.FullName == "chat_sessions/nested.json");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            sessionJson = reader.ReadToEnd();
        }
        Assert.NotNull(sessionJson);
        using (var document = JsonDocument.Parse(sessionJson!))
        {
            var fileFragment = document.RootElement.GetProperty("messages").EnumerateArray()
                .SelectMany(message => message.GetProperty("fragments").EnumerateArray())
                .Single(fragment => fragment.GetProperty("type").GetString() == "FILE");
            Assert.False(fileFragment.TryGetProperty("file_id", out _));
            Assert.False(fileFragment.TryGetProperty("name", out _));
            var files = fileFragment.GetProperty("files");
            Assert.Equal(2, files.GetArrayLength());
            Assert.All(files.EnumerateArray(), file => Assert.Equal(JsonValueKind.Null, file.GetProperty("export_path").ValueKind));
            Assert.Equal(
                new[] { "f1", "f2" },
                files.EnumerateArray().Select(file => file.GetProperty("id").GetString()).ToArray());
            Assert.Equal(
                new[] { "a.txt", "b.png" },
                files.EnumerateArray().Select(file => file.GetProperty("file_name").GetString()).ToArray());
            Assert.True(files[1].GetProperty("is_image").GetBoolean());
        }

        var target = NewDirectory();
        var second = await ChatDemoArchive.ImportAsync(exportPath, target, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, second.Files);
        Assert.Equal(0, second.SkippedFiles);
        var parts = new MessageRepository(new ChatDatabase(StoragePaths.DatabaseFile(target)))
            .ListBySession("nested").Single().Parts.Where(part => part.Kind == PartKind.File).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal(new[] { "f1", "f2" }, parts.Select(ReadMetaString("fileId")).ToArray());
        Assert.Equal(new[] { "a.txt", "b.png" }, parts.Select(ReadMetaString("name")).ToArray());
    }

    private static Func<ContentPart, string?> ReadMetaString(string property)
    {
        return part =>
        {
            using var document = JsonDocument.Parse(part.MetaJson!);
            return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
        };
    }

    private static Func<ContentPart, long?> ReadMetaLong(string property)
    {
        return part =>
        {
            using var document = JsonDocument.Parse(part.MetaJson!);
            return document.RootElement.TryGetProperty(property, out var value) ? value.GetInt64() : null;
        };
    }

    private static Func<ContentPart, bool?> ReadMetaBool(string property)
    {
        return part =>
        {
            using var document = JsonDocument.Parse(part.MetaJson!);
            return document.RootElement.TryGetProperty(property, out var value) ? value.GetBoolean() : null;
        };
    }

    private static void InsertFileRow(
        ChatDatabase database,
        string sha256,
        string originalName,
        long size,
        string extension,
        string? sessionId,
        long? messageId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files (sha256, rel_path, size, ext, mime, original_name, session_id, message_id, created_at)
            VALUES ($sha, $rel, $size, $ext, NULL, $name, $sid, $mid, $created)
            """;
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$rel", "files/" + sha256 + extension);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$ext", extension);
        command.Parameters.AddWithValue("$name", originalName);
        command.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mid", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
