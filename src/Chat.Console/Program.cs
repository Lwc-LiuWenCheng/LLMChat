namespace Chat.Console;

internal static class Program
{
    public static int Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException ex)
        {
            System.Console.Error.WriteLine("error: " + ex.Message);
            System.Console.Error.WriteLine("用 --help 查看用法。");
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return CliRunner.RunAsync(options, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            System.Console.Error.WriteLine("[cancelled]");
            return 130;
        }
    }

    private static void PrintHelp()
    {
        System.Console.Out.WriteLine(
            """
            用法：llmchat-cli [选项]

            选项：
              --provider <id>     插件 id，默认 chat.provider.deepseek
              --model <id>        模型 id；省略时用 provider 的默认模型或 --set 中的配置
              --api-key <key>     API Key；省略时读 DEEPSEEK_API_KEY / OPENAI_API_KEY / OPENCODE_API_KEY
              --base-url <url>    覆盖 base URL（deepseek / openai / opencode 插件）
              --system <text>     system 提示
              --message <text>    单轮消息；省略则进入交互 REPL（stdin 被重定向时读完全部输入）
              --thinking          请求开启思维链（provider 支持时）
              --set key=value     附加设置，可重复；例如 --set openai.preset=minimax
              --list-models       列出模型后退出
              --data-dir <dir>    数据目录（用户插件目录位于其下；默认按平台）
              --verbose           打印 provider 的调试日志
              -h, --help          显示本帮助

            输出约定：
              stdout  只有助手正文
              stderr  思维链（[thinking] 前缀）、工具/用量摘要、错误与结束原因
            退出码：
              0 成功；1 请求或事件流失败；2 参数或插件错误；130 用户中止

            示例：
              DEEPSEEK_API_KEY=sk-... llmchat-cli --message "你好"
              llmchat-cli --provider chat.provider.openai --set openai.preset=minimax --model MiniMax-M2 --message "你好"
              llmchat-cli --provider chat.provider.opencodego --model deepseek-v4-pro --message "你好"
              llmchat-cli --provider chat.provider.openai --base-url http://127.0.0.1:8080/v1 --api-key test --model mock --message "ping"
            """);
    }
}
