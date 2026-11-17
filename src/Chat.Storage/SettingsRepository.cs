namespace Chat.Storage;

/// <summary>键值设置。密钥也放这里（v1 用明文 + 文件权限 600；后续接系统钥匙串）。</summary>
public sealed class SettingsRepository
{
    private readonly ChatDatabase _database;

    public SettingsRepository(ChatDatabase database) => _database = database;

    public string? Get(string key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, string> GetAll()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }
}
