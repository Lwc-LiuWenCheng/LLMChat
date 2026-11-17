using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chat.Storage;

/// <summary>
/// SQLite 连接工厂 + 建表。单文件数据库是唯一真相源（服务端只保留七天，
/// 因此本地库必须自洽：消息树、附件引用、原始响应全在这里）。
/// </summary>
public sealed class ChatDatabase
{
    /// <summary>当前 schema 版本：v1=四张基础表；v2=files（附件内容寻址索引）。</summary>
    public const int CurrentSchemaVersion = 2;

    private readonly string _connectionString;

    public ChatDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// 幂等建库 + schema 版本迁移（PRAGMA user_version）。v1 用 IF NOT EXISTS 兜底旧库，
    /// v2 只新增 files 表，升级不丢数据；版本号高于本程序的库原样保留，不降级。
    /// </summary>
    public void Initialize()
    {
        using var connection = Open();

        int version;
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }

        if (version >= CurrentSchemaVersion) return;

        using var transaction = connection.BeginTransaction();
        if (version < 2)
        {
            using var files = connection.CreateCommand();
            files.Transaction = transaction;
            files.CommandText = FilesSchema;
            files.ExecuteNonQuery();
        }

        using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = "PRAGMA user_version = 2;";
            bump.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sessions (
            id                 TEXT PRIMARY KEY,
            title              TEXT NOT NULL DEFAULT '',
            provider_id        TEXT,
            model              TEXT,
            system_prompt      TEXT,
            current_message_id INTEGER,
            pinned             INTEGER NOT NULL DEFAULT 0,
            created_at         TEXT NOT NULL,
            updated_at         TEXT NOT NULL,
            deleted_at         TEXT
        );

        CREATE TABLE IF NOT EXISTS messages (
            id          INTEGER NOT NULL,
            session_id  TEXT NOT NULL,
            parent_id   INTEGER,
            role        TEXT NOT NULL,
            status      TEXT NOT NULL,
            provider_id TEXT,
            model       TEXT,
            raw_json    TEXT,
            created_at  TEXT NOT NULL,
            PRIMARY KEY (session_id, id),
            FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_messages_parent ON messages(session_id, parent_id);

        CREATE TABLE IF NOT EXISTS parts (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            message_id INTEGER NOT NULL,
            seq        INTEGER NOT NULL,
            kind       TEXT NOT NULL,
            text       TEXT,
            meta_json  TEXT,
            FOREIGN KEY (session_id, message_id) REFERENCES messages(session_id, id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_parts_message ON parts(session_id, message_id, seq);

        CREATE TABLE IF NOT EXISTS app_settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    /// <summary>v2：附件内容寻址索引。rel_path 以数据目录为根，形如 files/{sha256}{ext}。</summary>
    private const string FilesSchema = """
        CREATE TABLE IF NOT EXISTS files (
            sha256        TEXT PRIMARY KEY,
            rel_path      TEXT NOT NULL,
            size          INTEGER NOT NULL,
            ext           TEXT NOT NULL,
            mime          TEXT,
            original_name TEXT,
            session_id    TEXT,
            message_id    INTEGER,
            created_at    TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS ix_files_session ON files(session_id, message_id);
        """;
}
