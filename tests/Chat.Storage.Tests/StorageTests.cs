using Chat.Core;
using Chat.Storage;
using Xunit;

namespace Chat.Storage.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir;
    private readonly ChatDatabase _db;

    public StorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "llmchat-tests-" + Guid.NewGuid().ToString("N"));
        _db = new ChatDatabase(StoragePaths.DatabaseFile(_dir));
        _db.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Session_RoundTrips()
    {
        var repo = new SessionRepository(_db);
        var session = new Session { Id = "s1", Title = "标题", Model = "deepseek-flash", ProviderId = "chat.provider.deepseek" };
        repo.Upsert(session);

        var loaded = repo.Get("s1");
        Assert.NotNull(loaded);
        Assert.Equal("标题", loaded!.Title);
        Assert.Single(repo.List());
    }

    [Fact]
    public void Messages_KeepTreeAndParts()
    {
        var sessions = new SessionRepository(_db);
        var messages = new MessageRepository(_db);
        sessions.Upsert(new Session { Id = "s1" });

        var user = new MessageNode { Id = messages.NextId("s1"), ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("q")] };
        messages.Insert("s1", user);
        var assistant = new MessageNode
        {
            Id = messages.NextId("s1"),
            ParentId = user.Id,
            Role = MessageRole.Assistant,
            Parts = [ContentPart.OfThinking("t"), ContentPart.OfText("a")],
        };
        messages.Insert("s1", assistant);

        var nodes = messages.ListBySession("s1");
        Assert.Equal(2, nodes.Count);
        Assert.Equal(user.Id, nodes[1].ParentId);
        Assert.Equal(2, nodes[1].Parts.Count);
        Assert.Equal(PartKind.Thinking, nodes[1].Parts[0].Kind);
        Assert.Equal(assistant.Id, messages.NextId("s1") - 1);
    }

    [Fact]
    public void Update_ReplacesPartsAndStatus()
    {
        var sessions = new SessionRepository(_db);
        var messages = new MessageRepository(_db);
        sessions.Upsert(new Session { Id = "s1" });
        var node = new MessageNode { Id = 1, ParentId = null, Role = MessageRole.Assistant, Status = MessageStatus.Wip, Parts = [ContentPart.OfText("partial")] };
        messages.Insert("s1", node);

        messages.Update("s1", node with { Status = MessageStatus.Finished, Parts = [ContentPart.OfText("done")] });

        var loaded = messages.ListBySession("s1").Single();
        Assert.Equal(MessageStatus.Finished, loaded.Status);
        Assert.Equal("done", loaded.Parts.Single().Text);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var settings = new SettingsRepository(_db);
        settings.Set("deepseek.apiKey", "sk-test");
        Assert.Equal("sk-test", settings.Get("deepseek.apiKey"));
        Assert.Equal("sk-test", settings.GetAll()["deepseek.apiKey"]);
    }

    [Fact]
    public void Snapshot_CreatesDatabaseCopy()
    {
        new SessionRepository(_db).Upsert(new Session { Id = "s1", Title = "snap" });
        var snapshots = new SnapshotService(_db, StoragePaths.SnapshotsDirectory(_dir), keep: 3);

        var file = snapshots.Create("before-import");

        Assert.True(File.Exists(file));
        var copy = new ChatDatabase(file);
        Assert.Equal("snap", new SessionRepository(copy).Get("s1")!.Title);
    }

    [Fact]
    public void Snapshot_DoesNotOverwriteWithinSameSecond()
    {
        new SessionRepository(_db).Upsert(new Session { Id = "s1", Title = "snap" });
        var snapshots = new SnapshotService(_db, StoragePaths.SnapshotsDirectory(_dir), keep: 10);

        var first = snapshots.Create("import");
        var second = snapshots.Create("import");

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, snapshots.List().Count);
    }
}
