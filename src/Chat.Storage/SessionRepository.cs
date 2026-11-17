using Chat.Core;
using Microsoft.Data.Sqlite;

namespace Chat.Storage;

public sealed class SessionRepository
{
    private readonly ChatDatabase _database;

    public SessionRepository(ChatDatabase database) => _database = database;

    public IReadOnlyList<Session> List(bool includeDeleted = false, string? search = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, title, provider_id, model, system_prompt, current_message_id, pinned, created_at, updated_at
            FROM sessions
            WHERE {(includeDeleted ? "1=1" : "deleted_at IS NULL")}
              {(search is { Length: > 0 } ? "AND (title LIKE $q OR EXISTS (SELECT 1 FROM messages m WHERE m.session_id = sessions.id AND m.role='User' AND m.id = (SELECT MIN(id) FROM messages WHERE session_id = sessions.id AND role='User') AND m.raw_json LIKE $q))" : "")}
            ORDER BY pinned DESC, updated_at DESC
            """;
        if (search is { Length: > 0 }) command.Parameters.AddWithValue("$q", "%" + search + "%");
        using var reader = command.ExecuteReader();
        var list = new List<Session>();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    public Session? Get(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, provider_id, model, system_prompt, current_message_id, pinned, created_at, updated_at
            FROM sessions WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Upsert(Session session)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id, title, provider_id, model, system_prompt, current_message_id, pinned, created_at, updated_at, deleted_at)
            VALUES ($id, $title, $provider, $model, $system, $current, $pinned, $created, $updated, NULL)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                provider_id = excluded.provider_id,
                model = excluded.model,
                system_prompt = excluded.system_prompt,
                current_message_id = excluded.current_message_id,
                pinned = excluded.pinned,
                updated_at = excluded.updated_at,
                deleted_at = NULL
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$provider", (object?)session.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)session.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$system", (object?)session.SystemPrompt ?? DBNull.Value);
        command.Parameters.AddWithValue("$current", (object?)session.CurrentMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pinned", session.Pinned ? 1 : 0);
        command.Parameters.AddWithValue("$created", session.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", session.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>软删除：保留数据以便撤销导入/清理。</summary>
    public void SoftDelete(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET deleted_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void HardDelete(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Session Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        ProviderId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Model = reader.IsDBNull(3) ? null : reader.GetString(3),
        SystemPrompt = reader.IsDBNull(4) ? null : reader.GetString(4),
        CurrentMessageId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        Pinned = reader.GetInt64(6) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(8)),
    };
}
