using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Chat.App.Services;
using Chat.App.ViewModels;
using SukiUI;
using SukiUI.Controls;
using SukiUI.Enums;

namespace Chat.App.Views;

/// <summary>
/// 新的启动窗口：以 SukiWindow 为根承载 <see cref="ChatShellView"/>，
/// 复用现有 MainWindowViewModel；旧的 MainWindow / HomePage 保留给回归测试。
/// </summary>
public sealed class ChatShellWindow : SukiWindow
{
    public ChatShellWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 标题栏文本由全局品牌配置提供（空 = 不显示重复的产品名）。
        Title = AppBranding.WindowTitle;
        Width = 1280;
        Height = 800;
        MinWidth = 720;
        MinHeight = 520;

        // 亮色优先：SukiTheme 有自己的 ActiveBaseTheme，必须显式设置，
        // 只改 Application.RequestedThemeVariant 不会同步 Suki 主题（切换"看起来没反应"的根因）。
        ApplyBaseTheme(SukiTheme.GetInstance(Application.Current!), ThemeVariant.Light);

        var viewModel = new MainWindowViewModel(services);
        try
        {
            viewModel.Clipboard = new AvaloniaClipboardService(this);
        }
        catch (InvalidOperationException)
        {
            // 无剪贴板平台时保留 null，复制命令只更新内存中的 LastCopiedText。
        }
        catch (NotSupportedException)
        {
            // 同上。
        }

        DataContext = viewModel;
        Content = new ChatShellView();

        // 主题切换放进 SukiWindow 原生标题栏（与最小化/最大化/关闭同一行）。
        var themeButton = new Button
        {
            Content = "◐",
            FontSize = 16,
            Width = 34,
            Height = 34,
        };
        themeButton.Classes.Add("ghost");
        ToolTip.SetTip(themeButton, "切换明暗主题");
        themeButton.Click += OnToggleThemeClicked;
        RightWindowTitleBarControls.Add(themeButton);
    }

    private static void ApplyBaseTheme(SukiTheme? theme, ThemeVariant variant)
    {
        if (theme is not null && theme.ActiveBaseTheme != variant)
            theme.ChangeBaseTheme(variant);
    }

    private void OnToggleThemeClicked(object? sender, RoutedEventArgs e)
    {
        var theme = SukiTheme.GetInstance(Application.Current!);
        // SwitchBaseTheme 会同时切换 Suki 主题和 Application.RequestedThemeVariant。
        theme?.SwitchBaseTheme();
    }
}
