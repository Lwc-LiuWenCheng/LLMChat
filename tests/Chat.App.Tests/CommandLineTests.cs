using System.Text.Json;
using Chat.App.Services;
using Chat.Core;
using Chat.Storage;
using Chat.Storage.ChatDemo;
using Xunit;

namespace Chat.App.Tests;

public sealed class CommandLineTests
{
    private static JsonElement Json(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Parse_ImportAndExportOptions()
    {
        var import = CommandLineOptions.Parse(["--import", "backup.zip"]);
        Assert.Equal(CommandLineAction.Import, import.Action);
        Assert.Equal("backup.zip", import.ArchivePath);
        Assert.Null(import.Error);

        var export = CommandLineOptions.Parse(["--export", "out.zip", "--session", "s1", "s2", "--session", "s2"]);
        Assert.Equal(CommandLineAction.Export, export.Action);
        Assert.Equal("out.zip", export.ArchivePath);
        Assert.Equal(new[] { "s1", "s2" }, export.SessionIds);
        Assert.Null(export.Error);
    }

    [Fact]
    public void Parse_ReportsReadableErrors()
    {
        Assert.Contains("不能同时", CommandLineOptions.Parse(["--import", "a.zip", "--export", "b.zip"]).Error);
        Assert.Contains("需要一个", CommandLineOptions.Parse(["--import"]).Error);
        Assert.Contains("无法识别", CommandLineOptions.Parse(["--wat"]).Error);
        Assert.Contains("只能与 --export", CommandLineOptions.Parse(["--import", "a.zip", "--session", "s1"]).Error);
        Assert.Equal(CommandLineAction.Help, CommandLineOptions.Parse(["--help"]).Action);
        Assert.Equal(CommandLineAction.None, CommandLineOptions.Parse([]).Action);
    }

    [Fact]
    public async Task Import_WritesJsonSummaryAndPersistsSessions()
    {
        using var temp = new TempDataDirectory();
        var zip = TestArchives.WriteV3(Path.Combine(temp.Path, "backup.zip"));
        var output = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--import", zip], output, temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var json = Json(output.ToString());
        Assert.Equal("import", json.GetProperty("command").GetString());
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("added").GetInt32());
        Assert.Equal(2, json.GetProperty("addedMessages").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("snapshot").GetString()));

        var database = new ChatDatabase(StoragePaths.DatabaseFile(temp.Path));
        Assert.NotNull(new SessionRepository(database).Get("s1"));
    }

    [Fact]
    public async Task Import_InvalidArchiveReturnsErrorJsonWithoutThrowing()
    {
        using var temp = new TempDataDirectory();
        var database = new ChatDatabase(StoragePaths.DatabaseFile(temp.Path));
        database.Initialize();
        var sessions = new SessionRepository(database);
        sessions.Upsert(new Session { Id = "local", Title = "保留的本地会话" });

        var zip = Path.Combine(temp.Path, "broken.zip");
        await File.WriteAllTextAsync(zip, "not a zip", TestContext.Current.CancellationToken);
        var output = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--import", zip], output, temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        var json = Json(output.ToString());
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetString()));

        // 失败路径不得清空/污染原库：本地会话仍在，损坏包里的 s1 没有写进去。
        Assert.NotNull(sessions.Get("local"));
        Assert.Null(sessions.Get("s1"));
    }

    [Fact]
    public async Task Help_PrintsUsageAndReturnsZero()
    {
        var output = new StringWriter();
        var exitCode = await CliRunner.RunAsync(["--help"], output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        Assert.Contains("--import", output.ToString());
        Assert.Contains("--export", output.ToString());
        Assert.True(CliRunner.IsCliInvocation(["--import", "x.zip"]));
        Assert.False(CliRunner.IsCliInvocation(["--selftest"]));
        Assert.True(CliRunner.IsCliInvocation(["--session", "s1"]));
    }

    [Fact]
    public async Task Import_IsIdempotentThroughCli()
    {
        using var temp = new TempDataDirectory();
        var zip = TestArchives.WriteV3(Path.Combine(temp.Path, "backup.zip"));

        Assert.Equal(0, await CliRunner.RunAsync(["--import", zip], new StringWriter(), temp.Path, TestContext.Current.CancellationToken));
        var second = new StringWriter();
        Assert.Equal(0, await CliRunner.RunAsync(["--import", zip], second, temp.Path, TestContext.Current.CancellationToken));

        var json = Json(second.ToString());
        Assert.Equal(0, json.GetProperty("added").GetInt32());
        Assert.Equal(1, json.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task ExportSessions_ThenImport_RoundTripsOnlySelectedSession()
    {
        using var temp = new TempDataDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        var database = new ChatDatabase(StoragePaths.DatabaseFile(source));
        database.Initialize();
        var sessions = new SessionRepository(database);
        var messages = new MessageRepository(database);
        sessions.Upsert(new Session { Id = "s1", Title = "保留", CurrentMessageId = 1 });
        sessions.Upsert(new Session { Id = "s2", Title = "不要", CurrentMessageId = 1 });
        messages.Insert("s1", new Chat.Core.MessageNode
        {
            Id = 1,
            ParentId = null,
            Role = Chat.Core.MessageRole.User,
            Parts = [Chat.Core.ContentPart.OfText("one")],
        });
        messages.Insert("s2", new Chat.Core.MessageNode
        {
            Id = 1,
            ParentId = null,
            Role = Chat.Core.MessageRole.User,
            Parts = [Chat.Core.ContentPart.OfText("two")],
        });

        var zip = Path.Combine(temp.Path, "selected.zip");
        var exportOutput = new StringWriter();
        Assert.Equal(0, await CliRunner.RunAsync(
            ["--export", zip, "--session", "s1"], exportOutput, source, TestContext.Current.CancellationToken));
        var exportJson = Json(exportOutput.ToString());
        Assert.Equal(1, exportJson.GetProperty("sessions").GetInt32());

        using (var reader = ChatDemoZipReader.Open(zip))
        {
            Assert.NotNull(reader.Manifest);
            Assert.Single(reader.Sessions);
            Assert.Equal("s1", reader.Sessions[0].InferredSessionId);
        }
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            Assert.Equal("manifest.json", archive.Entries[0].FullName);
        }

        var target = Path.Combine(temp.Path, "target");
        var importOutput = new StringWriter();
        Assert.Equal(0, await CliRunner.RunAsync(
            ["--import", zip], importOutput, target, TestContext.Current.CancellationToken));
        var importJson = Json(importOutput.ToString());
        Assert.Equal(1, importJson.GetProperty("added").GetInt32());

        var targetDatabase = new ChatDatabase(StoragePaths.DatabaseFile(target));
        var targetSessions = new SessionRepository(targetDatabase);
        Assert.NotNull(targetSessions.Get("s1"));
        Assert.Null(targetSessions.Get("s2"));
        Assert.Equal("one", new MessageRepository(targetDatabase).ListBySession("s1").Single().PlainText);
    }

    [Fact]
    public async Task ExportAll_FromEmptyDatabaseWritesValidEmptyArchive()
    {
        using var temp = new TempDataDirectory();
        var zip = Path.Combine(temp.Path, "empty.zip");
        var output = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--export", zip], output, temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, Json(output.ToString()).GetProperty("sessions").GetInt32());
        using var reader = ChatDemoZipReader.Open(zip);
        Assert.Empty(reader.Sessions);
    }
}