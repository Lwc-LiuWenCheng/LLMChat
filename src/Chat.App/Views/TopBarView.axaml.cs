using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Chat.App.Views;

/// <summary>顶栏：汉堡、标题/模型、新建对话、主题与窗口按钮。仅处理视图级交互。</summary>
public partial class TopBarView : UserControl
{
    public TopBarView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnHamburgerClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is ChatShellWindow { Content: ChatShellView shell }) shell.ToggleSidebar();
    }

    private void OnToggleThemeClicked(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = app.RequestedThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }
}
