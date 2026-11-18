using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using Chat.App.Services;
using Chat.App.Views;

namespace Chat.App;

public partial class App : Application
{
    private AppServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Create();
            // 启动入口切换到 DeepSeek 风格外壳；旧 MainWindow 与 HomePage 保留给回归测试。
            // 新窗口内部复用 MainWindowViewModel，窗口关闭时仍由下面的 ShutdownRequested 释放服务。
            desktop.MainWindow = new ChatShellWindow(_services);
            desktop.ShutdownRequested += (_, _) =>
            {
                _services?.Dispose();
                _services = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
