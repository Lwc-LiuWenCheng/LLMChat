using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Chat.App.Services;
using Chat.App.ViewModels;
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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_services),
            };
            desktop.ShutdownRequested += (_, _) =>
            {
                _services?.Dispose();
                _services = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
