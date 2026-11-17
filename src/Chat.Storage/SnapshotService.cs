using Microsoft.Data.Sqlite;

namespace Chat.Storage;

/// <summary>
/// 导入/批量操作前的自动快照。用 SQLite 在线备份 API，比复制 WAL 文件安全；
/// 保留最近 N 份，其余删除。
/// </summary>
public sealed class SnapshotService
{
    private readonly ChatDatabase _database;
    private readonly string _directory;
    private readonly int _keep;

    public SnapshotService(ChatDatabase database, string directory, int keep = 10)
    {
        _database = database;
        _directory = directory;
        _keep = keep;
    }

    public string Create(string reason = "manual")
    {
        Directory.CreateDirectory(_directory);
        // 毫秒 + 短 guid：同一秒内连续快照（尤其导入/导出并发）不会互相覆盖。
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var unique = Guid.NewGuid().ToString("N")[..8];
        var target = Path.Combine(_directory, $"chat-{stamp}-{unique}-{Sanitize(reason)}.db");

        using (var source = _database.Open())
        using (var destination = new SqliteConnection($"Data Source={target};Mode=ReadWriteCreate"))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        Trim();
        return target;
    }

    public IReadOnlyList<string> List() =>
        Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "chat-*.db").OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : [];

    private void Trim()
    {
        var files = List();
        foreach (var file in files.Skip(_keep))
        {
            try { File.Delete(file); } catch (IOException) { /* 快照删不掉不影响主流程 */ }
        }
    }

    private static string Sanitize(string reason) =>
        new(reason.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
