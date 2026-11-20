using Chat.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Chat.Gateway;

public static class GatewayHost
{
    public static async Task<WebApplication> StartAsync(GatewayConfig config)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(config.Urls);
        builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 256L * 1024 * 1024;
        });
        builder.Services.AddSingleton(config);
        var app = builder.Build();
        var store = new GatewayStore(config.DataDirectory);
        var proxy = config.UpstreamHandler is { } handler
            ? new DeepSeekProxy(store, app.Logger, handler)
            : new DeepSeekProxy(store, app.Logger);
        RegisterRoutes(app, store, proxy, config);
        await app.StartAsync();
        return app;
    }

    public static async Task StopAsync(WebApplication app)
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    internal static void RegisterRoutes(WebApplication app, GatewayStore store, DeepSeekProxy proxy, GatewayConfig config)
    {
        app.MapGet("/api/sessions", () => Results.Json(store.ListSessions().Select(SessionJson.ToSummary)));
        app.MapGet("/api/sessions/{id}", (string id) =>
        {
            var session = store.GetSession(id);
            return session is null ? GatewayErrors.NotFound("会话不存在") : Results.Json(SessionJson.ToClient(session));
        });
        app.MapPut("/api/sessions/{id}", async (string id, HttpContext context) =>
        {
            GatewayPutRequest request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<GatewayPutRequest>(GatewayJson.Options, context.RequestAborted)
                           ?? throw new GatewayBadRequestException("请求体不能为空。");
            }
            catch (GatewayBadRequestException error)
            {
                return GatewayErrors.InvalidSession(error.Message);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException)
            {
                return GatewayErrors.InvalidSession($"请求 JSON 无法解析：{error.Message}");
            }
            if (request.Session is null || request.Session.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                return GatewayErrors.InvalidSession("请求缺少 session 对象。");
            if (request.Session.Value.TryGetProperty("id", out var bodyId) && bodyId.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var bodyIdText = bodyId.GetString();
                if (!string.IsNullOrWhiteSpace(bodyIdText) && !string.Equals(bodyIdText, id, StringComparison.Ordinal))
                    return GatewayErrors.InvalidSession("URL 与请求体的会话 id 不一致。");
            }
            try
            {
                var files = request.Files ?? [];
                var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    if (string.IsNullOrWhiteSpace(file.Id))
                        throw new GatewayBadRequestException("附件的 id 不能为空。");
                    if (string.IsNullOrEmpty(file.Base64))
                        throw new GatewayBadRequestException($"附件 {file.Id} 缺少 base64 内容。");
                    blobs[file.Id!] = Convert.FromBase64String(file.Base64!);
                }
                await store.SaveSessionAsync(id, request.Session.Value, blobs, files, context.RequestAborted);
                return Results.NoContent();
            }
            catch (GatewayBadRequestException error)
            {
                return GatewayErrors.InvalidSession(error.Message);
            }
            catch (FormatException)
            {
                return GatewayErrors.InvalidSession("附件的 base64 内容无效。");
            }
            catch (Exception error)
            {
                app.Logger.LogError(error, "保存会话 {SessionId} 失败", id);
                return GatewayErrors.IoError($"保存会话失败：{error.Message}");
            }
        });
        app.MapPatch("/api/sessions/{id}", async (string id, HttpContext context) =>
        {
            GatewayPatchRequest request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<GatewayPatchRequest>(GatewayJson.Options, context.RequestAborted)
                           ?? throw new GatewayBadRequestException("请求体不能为空。");
            }
            catch (GatewayBadRequestException error)
            {
                return GatewayErrors.InvalidSession(error.Message);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException)
            {
                return GatewayErrors.InvalidSession($"请求 JSON 无法解析：{error.Message}");
            }
            if (request.Title is null) return GatewayErrors.InvalidSession("请求缺少 title 字段。");
            if (store.GetSession(id) is null) return GatewayErrors.NotFound("会话不存在");
            store.RenameSession(id, request.Title);
            return Results.NoContent();
        });
        app.MapDelete("/api/sessions/{id}", (string id) =>
        {
            if (store.GetSession(id) is null) return GatewayErrors.NotFound("会话不存在");
            store.SoftDeleteSession(id);
            return Results.NoContent();
        });
        app.MapPost("/api/files", async (HttpContext context) =>
        {
            if (!context.Request.HasFormContentType)
                return GatewayErrors.InvalidSession("需要 multipart/form-data 请求。");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var stored = new List<object>();
            foreach (var file in form.Files)
            {
                if (file.Length == 0) continue;
                await using var stream = file.OpenReadStream();
                var item = await store.SaveFileAsync(stream, file.FileName, file.ContentType, context.RequestAborted);
                stored.Add(new { id = item.Sha256 });
            }
            return Results.Json(stored, statusCode: StatusCodes.Status201Created);
        });
        app.MapGet("/api/files/{id}", (string id) =>
        {
            if (!store.TryResolveFile(id, out var path, out var mime) || !File.Exists(path))
                return GatewayErrors.NotFound("文件不存在");
            return Results.File(path, mime ?? "application/octet-stream", enableRangeProcessing: true);
        });
        app.MapPost("/api/import/clear", () => GatewayErrors.NotImplemented("批量导入暂存尚未实现。"));
        app.MapPost("/api/import/session", () => GatewayErrors.NotImplemented("批量导入暂存尚未实现。"));
        app.MapPost("/api/import/files", () => GatewayErrors.NotImplemented("批量导入暂存尚未实现。"));
        app.MapPost("/api/import/file-types", () => GatewayErrors.NotImplemented("批量导入暂存尚未实现。"));
        app.MapPost("/api/import/commit", () => GatewayErrors.NotImplemented("批量导入暂存尚未实现。"));
        app.MapPost("/anthropic/v1/messages", async (HttpContext context) =>
        {
            var result = await proxy.ForwardMessagesAsync(context);
            await result.ExecuteAsync(context);
        });
        app.MapGet("/models", async (HttpContext context) =>
        {
            var result = await proxy.ForwardModelsAsync(context);
            await result.ExecuteAsync(context);
        });
        app.MapGet("/anthropic/v1/models", async (HttpContext context) =>
        {
            var result = await proxy.ForwardModelsAsync(context);
            await result.ExecuteAsync(context);
        });
        foreach (var path in new[] { "/anthropic/v1/files", "/anthropic/v1/files/{id}" })
            app.MapMethods(path, new[] { "POST", "GET", "DELETE" }, () => GatewayErrors.NotImplemented("Files API 尚未开放。"));
        if (config.HasDemo)
        {
            var fileTypes = new FileExtensionContentTypeProvider();
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(config.DemoRoot),
                ContentTypeProvider = fileTypes,
            });
            app.MapFallback("{**path}", async (HttpContext context) =>
            {
                if (context.Request.Path.StartsWithSegments("/api") ||
                    context.Request.Path.StartsWithSegments("/anthropic") ||
                    context.Request.Path.Equals("/models", StringComparison.Ordinal))
                {
                    await GatewayErrors.NotFound("接口不存在").ExecuteAsync(context);
                    return;
                }
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(Path.Combine(config.DemoRoot, "index.html"), context.RequestAborted);
            });
        }
        else
        {
            app.MapFallback("{**path}", async (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>Chat.Gateway</title>" +
                "<h1>前端资源尚未构建</h1><p>未找到 web/chat-demo/dist/index.html。请在 web/chat-demo 执行构建后重启网关；API 与 /models 仍可正常使用。</p>");
        });
        }
    }
}