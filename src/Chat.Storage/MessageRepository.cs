using Chat.Core;
using Microsoft.Data.Sqlite;

namespace Chat.Storage;

public sealed class MessageRepository
{
    private readonly ChatDatabase _database;

    public MessageRepository(ChatDatabase database) => _database = database;

    public IReadOnlyList<MessageNode> ListBySession(string sessionId)
    {
        using var connection = _database.Open();
        var messages = new List<MessageNode>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, parent_id, role, status, provider_id, model, raw_json, created_at
                FROM messages WHERE session_id = $sid ORDER BY id
                """;
            command.Parameters.AddWithValue("$sid", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new MessageNode
                {
                    Id = reader.GetInt64(0),
                    ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    Role = Enum.TryParse<MessageRole>(reader.GetString(2), ignoreCase: true, out var role) ? role : MessageRole.Assistant,
                    Status = Enum.TryParse<MessageStatus>(reader.GetString(3), ignoreCase: true, out var status) ? status : MessageStatus.Finished,
                    ProviderId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Model = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RawJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
                });
            }
        }

        var byId = messages.ToDictionary(m => m.Id);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT message_id, kind, text, meta_json FROM parts WHERE session_id = $sid ORDER BY message_id, seq";
            command.Parameters.AddWithValue("$sid", sessionId);
            using var reader = command.ExecuteReader();
            var parts = new Dictionary<long, List<ContentPart>>();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!parts.TryGetValue(id, out var list)) parts[id] = list = [];
                list.Add(new ContentPart(
                    Enum.TryParse<PartKind>(reader.GetString(1), ignoreCase: true, out var kind) ? kind : PartKind.Text,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            foreach (var (id, list) in parts)
                if (byId.TryGetValue(id, out var node)) byId[id] = node with { Parts = list };
        }

        return messages.Select(m => byId[m.Id]).ToList();
    }

    /// <summary>会话内下一个消息 id（父 id 必须小于子 id）。</summary>
    public long NextId(string sessionId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) + 1 FROM messages WHERE session_id = $sid";
        command.Parameters.AddWithValue("$sid", sessionId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Insert(string sessionId, MessageNode node)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO messages (id, session_id, parent_id, role, status, provider_id, model, raw_json, created_at)
                VALUES ($id, $sid, $parent, $role, $status, $provider, $model, $raw, $created)
                """;
            command.Parameters.AddWithValue("$id", node.Id);
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$parent", (object?)node.ParentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$role", node.Role.ToString());
            command.Parameters.AddWithValue("$status", node.Status.ToString());
            command.Parameters.AddWithValue("$provider", (object?)node.ProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$model", (object?)node.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$raw", (object?)node.RawJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", node.CreatedAt.ToString("O"));
            command.ExecuteNonQuery();
        }

        var seq = 0;
        foreach (var part in node.Parts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO parts (session_id, message_id, seq, kind, text, meta_json)
                VALUES ($sid, $mid, $seq, $kind, $text, $meta)
                """;
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$mid", node.Id);
            command.Parameters.AddWithValue("$seq", seq++);
            command.Parameters.AddWithValue("$kind", part.Kind.ToString());
            command.Parameters.AddWithValue("$text", (object?)part.Text ?? DBNull.Value);
            command.Parameters.AddWithValue("$meta", (object?)part.MetaJson ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>更新消息内容与状态（流式结束后调用）。</summary>
    public void Update(string sessionId, MessageNode node)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE messages SET status = $status, raw_json = $raw, provider_id = $provider, model = $model
                WHERE session_id = $sid AND id = $id
                """;
            command.Parameters.AddWithValue("$status", node.Status.ToString());
            command.Parameters.AddWithValue("$raw", (object?)node.RawJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$provider", (object?)node.ProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$model", (object?)node.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$id", node.Id);
            command.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM parts WHERE session_id = $sid AND message_id = $id";
            delete.Parameters.AddWithValue("$sid", sessionId);
            delete.Parameters.AddWithValue("$id", node.Id);
            delete.ExecuteNonQuery();
        }

        var seq = 0;
        foreach (var part in node.Parts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO parts (session_id, message_id, seq, kind, text, meta_json)
                VALUES ($sid, $mid, $seq, $kind, $text, $meta)
                """;
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$mid", node.Id);
            command.Parameters.AddWithValue("$seq", seq++);
            command.Parameters.AddWithValue("$kind", part.Kind.ToString());
            command.Parameters.AddWithValue("$text", (object?)part.Text ?? DBNull.Value);
            command.Parameters.AddWithValue("$meta", (object?)part.MetaJson ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
