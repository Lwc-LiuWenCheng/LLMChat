using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Chat.App.Tests;

/// <summary>合成 chat-demo export_format_version=3 ZIP，供 App 层导入/导出测试使用。</summary>
internal static class TestArchives
{
    public static string WriteV3(
        string path,
        string sessionId = "s1",
        string title = "导入的会话",
        Action<ZipArchive>? extra = null,
        string? fileNameSessionId = null,
        string? sessionJson = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        // 必须首条：manifest.json。
        WriteJson(archive, "manifest.json", """{"export_format_version":3,"exported_at":"2024-05-01T12:00:00+00:00"}""");
        WriteJson(archive, "chat_sessions/" + (fileNameSessionId ?? sessionId) + ".json",
            sessionJson ?? SessionJson(sessionId, title));
        WriteJson(archive, "chat_session_index.json",
            JsonSerializer.Serialize(new { export_format_version = 3, sessions = new[] { new { id = sessionId, title } } }));
        extra?.Invoke(archive);
        return path;
    }

    public static string SessionJson(string sessionId, string title) =>
        JsonSerializer.Serialize(new
        {
            id = sessionId,
            title,
            provider_id = "chat.provider.deepseek",
            model = "deepseek-flash",
            current_message_id = 2,
            created_at = "2024-05-01T12:00:00Z",
            updated_at = "2024-05-01T12:05:00Z",
            messages = new object[]
            {
                new
                {
                    id = 1,
                    parent_id = (int?)null,
                    role = "USER",
                    status = "FINISHED",
                    created_at = "2024-05-01T12:01:00Z",
                    fragments = new object[] { new { type = "REQUEST", content = "你好" } },
                },
                new
                {
                    id = 2,
                    parent_id = (int?)1,
                    role = "ASSISTANT",
                    status = "FINISHED",
                    created_at = "2024-05-01T12:02:00Z",
                    fragments = new object[]
                    {
                        new { type = "THINK", content = "想一下" },
                        new { type = "RESPONSE", content = "收到" },
                    },
                },
            },
        });

    public static void WriteJson(ZipArchive archive, string entryName, string json)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(json);
    }

    public static void WriteBytes(ZipArchive archive, string entryName, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}