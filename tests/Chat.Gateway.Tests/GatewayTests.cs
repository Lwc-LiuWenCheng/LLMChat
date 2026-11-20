using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chat.Gateway;
using Xunit;

namespace Chat.Gateway.Tests;

public sealed class GatewayTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public GatewayTests(GatewayFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Session_RoundTrip_PreservesBranchesAttachmentsAndThinking()
    {
        var fileId = "att-1";
        var fileBytes = Encoding.UTF8.GetBytes("hello attachment");
        var session = new JsonObject
        {
            ["id"] = "session-branch",
            ["title"] = "分支会话",
            ["createdAt"] = 1_700_000_000_000L,
            ["updatedAt"] = 1_700_000_001_000L,
            ["rootMessageId"] = "m1",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "m1",
                    ["role"] = "user",
                    ["content"] = "question",
                    ["createdAt"] = 1_700_000_000_000L,
                    ["files"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = fileId,
                            ["name"] = "note.txt",
                            ["size"] = fileBytes.Length,
                            ["isImage"] = false,
                            ["mimeType"] = "text/plain",
                        },
                    },
                },
                new JsonObject
                {
                    ["id"] = "m2",
                    ["parentId"] = "m1",
                    ["selectedChildId"] = "m3",
                    ["role"] = "assistant",
                    ["createdAt"] = 1_700_000_000_100L,
                    ["status"] = "done",
                    ["model"] = "deepseek-chat",
                    ["prefix"] = "final",
                    ["blocks"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "thinking", ["thinking"] = "let me think" },
                        new JsonObject { ["type"] = "text", ["text"] = "answer-a" },
                    },
                },
                new JsonObject
                {
                    ["id"] = "m3",
                    ["parentId"] = "m2",
                    ["role"] = "user",
                    ["content"] = "follow-up",
                    ["createdAt"] = 1_700_000_000_200L,
                    ["files"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["id"] = "m4",
                    ["parentId"] = "m2",
                    ["role"] = "user",
                    ["content"] = "other-branch",
                    ["createdAt"] = 1_700_000_000_300L,
                    ["files"] = new JsonArray(),
                },
            },
        };
        var put = new JsonObject
        {
            ["session"] = session,
            ["files"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = fileId,
                    ["name"] = "note.txt",
                    ["mime"] = "text/plain",
                    ["base64"] = Convert.ToBase64String(fileBytes),
                },
            },
        };
        var response = await _fixture.Client.PutAsync("/api/sessions/session-branch", Json(put), _ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var get = await _fixture.Client.GetAsync("/api/sessions/session-branch", _ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var document = JsonDocument.Parse(await get.Content.ReadAsStringAsync(_ct));
        var root = document.RootElement;
        Assert.Equal("session-branch", root.GetProperty("id").GetString());
        Assert.Equal("分支会话", root.GetProperty("title").GetString());
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(4, messages.Length);
        Assert.Contains(messages, message => message.TryGetProperty("content", out var text) && text.GetString() == "question");
        Assert.Contains(messages, message => message.TryGetProperty("content", out var text) && text.GetString() == "other-branch");
        var assistant = messages.Single(message => message.GetProperty("role").GetString() == "assistant");
        Assert.Equal("3", assistant.GetProperty("selectedChildId").GetString());
        Assert.DoesNotContain(assistant.GetProperty("blocks").EnumerateArray(), block => block.GetProperty("type").GetString() == "thinking" && block.GetProperty("thinking").GetString() != "let me think");
        Assert.Contains(assistant.GetProperty("blocks").EnumerateArray(), block => block.GetProperty("type").GetString() == "thinking" && block.GetProperty("thinking").GetString() == "let me think");
        Assert.Contains(assistant.GetProperty("blocks").EnumerateArray(), block => block.GetProperty("type").GetString() == "text" && block.GetProperty("text").GetString() == "answer-a");
        var user = messages.Single(message => message.TryGetProperty("content", out var text) && text.GetString() == "question");
        var attachment = Assert.Single(user.GetProperty("files").EnumerateArray());
        Assert.Equal(fileId, attachment.GetProperty("id").GetString());
        Assert.Equal("note.txt", attachment.GetProperty("name").GetString());

        var fileResponse = await _fixture.Client.GetAsync("/api/files/" + attachment.GetProperty("serverId").GetString(), _ct);
        Assert.Equal(HttpStatusCode.OK, fileResponse.StatusCode);
        Assert.Equal("text/plain", fileResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello attachment", await fileResponse.Content.ReadAsStringAsync(_ct));
    }

    [Fact]
    public async Task Put_IsIdempotent_AndCrudEndpointsWork()
    {
        var session = BasicSession("session-idempotent", "原始标题");
        var body = new JsonObject { ["session"] = session, ["files"] = new JsonArray() };
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.Client.PutAsync("/api/sessions/session-idempotent", Json(body), _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.Client.PutAsync("/api/sessions/session-idempotent", Json(body), _ct)).StatusCode);

        var list = await _fixture.Client.GetAsync("/api/sessions", _ct);
        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync(_ct));
        Assert.Equal(1, listDocument.RootElement.EnumerateArray().Count(item => item.GetProperty("id").GetString() == "session-idempotent"));
        using var after = JsonDocument.Parse(await (await _fixture.Client.GetAsync("/api/sessions/session-idempotent", _ct)).Content.ReadAsStringAsync(_ct));
        Assert.Equal(2, after.RootElement.GetProperty("messages").GetArrayLength());

        var patch = await _fixture.Client.PatchAsync("/api/sessions/session-idempotent", Json(new JsonObject { ["title"] = "新标题" }), _ct);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        using var renamed = JsonDocument.Parse(await (await _fixture.Client.GetAsync("/api/sessions/session-idempotent", _ct)).Content.ReadAsStringAsync(_ct));
        Assert.Equal("新标题", renamed.RootElement.GetProperty("title").GetString());

        var delete = await _fixture.Client.DeleteAsync("/api/sessions/session-idempotent", _ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _fixture.Client.GetAsync("/api/sessions/session-idempotent", _ct)).StatusCode);
    }

    [Fact]
    public async Task Files_PostAndGet_RoundTrip()
    {
        var bytes = Encoding.UTF8.GetBytes("standalone-file");
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(content, "files", "standalone.txt");
        var response = await _fixture.Client.PostAsync("/api/files", form, _ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct));
        var id = Assert.Single(document.RootElement.EnumerateArray()).GetProperty("id").GetString();
        Assert.NotNull(id);
        var download = await _fixture.Client.GetAsync("/api/files/" + id, _ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("standalone-file", await download.Content.ReadAsStringAsync(_ct));
    }

    [Fact]
    public async Task Messages_InjectsBearerIgnoresClientKeyAndStreamsSseVerbatim()
    {
        _fixture.SetApiKey("server-secret-key");
        var frames = new[]
        {
            "event: message_start\ndata: {\"type\":\"message_start\"}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"hi\"}}\n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        };
        _fixture.Handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(frames), Encoding.UTF8, "text/event-stream"),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages");
        request.Headers.TryAddWithoutValidation("x-api-key", "client-forged-key");
        request.Content = new StringContent("{\"model\":\"deepseek-chat\",\"stream\":true}", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.SendAsync(request, _ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Concat(frames), await response.Content.ReadAsStringAsync(_ct));

        var call = Assert.Single(_fixture.Handler.Calls);
        Assert.Equal("/anthropic/v1/messages", call.Uri.AbsolutePath);
        Assert.Equal("Bearer server-secret-key", _fixture.Handler.Header(call, "Authorization"));
        Assert.Equal("files-api-2025-04-14", _fixture.Handler.Header(call, "anthropic-beta"));
        Assert.DoesNotContain(_fixture.Handler.Calls, item => _fixture.Handler.Header(item, "x-api-key").Contains("client-forged-key"));
    }

    [Fact]
    public async Task Messages_WithoutApiKey_Returns428()
    {
        _fixture.SetApiKey("");
        var response = await _fixture.Client.PostAsync("/anthropic/v1/messages", Json(new JsonObject()), _ct);
        Assert.Equal(428, (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct));
        Assert.Equal("missing_api_key", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("DeepSeek API Key", document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Put_WithBadJson_ReturnsInvalidSession()
    {
        var response = await _fixture.Client.PutAsync("/api/sessions/bad", new StringContent("{not-json", Encoding.UTF8, "application/json"), _ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct));
        Assert.Equal("invalid_session", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingDist_ReturnsFriendly503Page()
    {
        var response = await _fixture.Client.GetAsync("/", _ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("前端资源尚未构建", await response.Content.ReadAsStringAsync(_ct));
    }

    [Fact]
    public async Task FilesEndpoint_NotImplemented()
    {
        var response = await _fixture.Client.PostAsync("/anthropic/v1/files", Json(new JsonObject()), _ct);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct));
        Assert.Equal("not_implemented", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SpaFallback_WithDist_ServesIndex()
    {
        var temp = Path.Combine(Path.GetTempPath(), "llmchat-gateway-dist-" + Guid.NewGuid().ToString("N"));
        var dist = Path.Combine(temp, "dist");
        Directory.CreateDirectory(dist);
        await File.WriteAllTextAsync(Path.Combine(dist, "index.html"), "<!doctype html><title>demo</title>", _ct);
        var config = new GatewayConfig(Path.Combine(temp, "data"), "http://127.0.0.1:0", dist);
        var app = await GatewayHost.StartAsync(config);
        try
        {
            using var client = new HttpClient { BaseAddress = GatewayFixture.TestAddress(app) };
            var page = await client.GetAsync("/some/route", _ct);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("demo", await page.Content.ReadAsStringAsync(_ct));
            var asset = await client.GetAsync("/missing.js", _ct);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        }
        finally
        {
            await GatewayHost.StopAsync(app);
            TryDelete(temp);
        }
    }

    private static JsonObject BasicSession(string id, string title) => new()
    {
        ["id"] = id,
        ["title"] = title,
        ["createdAt"] = 1_700_000_000_000L,
        ["updatedAt"] = 1_700_000_000_000L,
        ["rootMessageId"] = "m1",
        ["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "m1",
                ["role"] = "user",
                ["content"] = "hello",
                ["createdAt"] = 1_700_000_000_000L,
                ["files"] = new JsonArray(),
            },
            new JsonObject
            {
                ["id"] = "m2",
                ["parentId"] = "m1",
                ["role"] = "assistant",
                ["createdAt"] = 1_700_000_000_100L,
                ["status"] = "done",
                ["model"] = "deepseek-chat",
                ["prefix"] = "final",
                ["blocks"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "world" } },
            },
        },
    };

    private static StringContent Json(JsonObject value) => new(value.ToJsonString(), Encoding.UTF8, "application/json");

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}