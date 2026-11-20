using System.Net;
using System.Text;
using System.Text.Json;
using Chat.Gateway;
using Chat.Storage;
using Microsoft.AspNetCore.Builder;

namespace WebGatewaySmoke;

/// <summary>
/// 端到端冒烟用的进程内网关宿主：用假 DeepSeek handler 启动真实 Chat.Gateway，
/// 把上游请求记录到 JSONL 文件，供 Node 脚本断言 Bearer 注入与请求路径。
/// 不连接真实网络、不读取真实密钥。
/// </summary>
internal static class Program
{
    private const string ApiKeySetting = "deepseek.apiKey";

    private static async Task<int> Main(string[] args)
    {
        var dataDir = Option(args, "--data-dir") ?? throw new ArgumentException("缺少 --data-dir");
        var demoRoot = Option(args, "--demo-root") ?? throw new ArgumentException("缺少 --demo-root");
        var urls = Option(args, "--urls") ?? "http://127.0.0.1:0";
        var recordPath = Option(args, "--record") ?? throw new ArgumentException("缺少 --record");
        var stopFile = Option(args, "--stop-file") ?? throw new ArgumentException("缺少 --stop-file");
        var urlFile = Option(args, "--url-file") ?? throw new ArgumentException("缺少 --url-file");

        Directory.CreateDirectory(dataDir);
        if (File.Exists(recordPath)) File.Delete(recordPath);

        // 注入一个冒烟专用 Key；node 脚本可在缺 Key 用例里删除它。
        var database = new ChatDatabase(StoragePaths.DatabaseFile(dataDir));
        database.Initialize();
        new SettingsRepository(database).Set(ApiKeySetting, "smoke-server-key");

        var handler = new FakeDeepSeekHandler(recordPath);
        var config = new GatewayConfig(dataDir, urls, demoRoot, handler);
        var app = await GatewayHost.StartAsync(config);
        try
        {
            var address = app.Urls.FirstOrDefault() ?? throw new InvalidOperationException("网关未监听地址");
            await File.WriteAllTextAsync(urlFile, address);
            Console.WriteLine(address);
            Console.Out.Flush();

            // 等 shell 脚本触碰 stop-file 后优雅关停；轮询间隔足够短，不占用 CPU。
            while (!File.Exists(stopFile))
                await Task.Delay(100);
            return 0;
        }
        finally
        {
            await GatewayHost.StopAsync(app);
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal)) return args[index + 1];
        return null;
    }

    private const string SseBody =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_smoke\",\"model\":\"deepseek-chat\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"THINK-smoke\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"TEXT_DELTA-smoke\"}}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private sealed class FakeDeepSeekHandler(string recordPath) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var record = new
            {
                method = request.Method.Method,
                uri = request.RequestUri?.ToString(),
                absolutePath = request.RequestUri?.AbsolutePath,
                authorization = Header(request, "Authorization"),
                anthropicBeta = Header(request, "anthropic-beta"),
                xApiKey = Header(request, "x-api-key"),
                body,
            };
            await File.AppendAllTextAsync(
                recordPath,
                JsonSerializer.Serialize(record) + "\n",
                Encoding.UTF8,
                cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
    }
}
