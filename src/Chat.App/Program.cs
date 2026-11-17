using Avalonia;
using Chat.App.Services;
using Chat.Kernel;

namespace Chat.App;

internal static class Program
{
    // WinExe 在 Windows 上没有控制台；自检输出仍会写到父进程的标准输出。
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            var root = args.Length >= 2 ? args[1] : Path.Combine(AppContext.BaseDirectory, "plugins");
            return SelfTest.Run(root, AppInfo.Version, Console.Out);
        }

        if (CliRunner.IsCliInvocation(args))
        {
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, cancelArgs) =>
            {
                cancelArgs.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                return CliRunner.RunAsync(args, Console.Out, cancellationToken: cts.Token).GetAwaiter().GetResult();
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}