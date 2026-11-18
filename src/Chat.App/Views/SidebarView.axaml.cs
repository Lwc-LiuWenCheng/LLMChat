using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Chat.App.ViewModels;
using Chat.Core;

namespace Chat.App.Views;

/// <summary>侧边栏：搜索、新建对话、分组会话列表、置顶标记与设置入口。</summary>
public partial class SidebarView : UserControl
{
    private bool _isCompact;

    public SidebarView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>中屏收起时只显示图标条；该状态由外壳按断点设置，不进入 ViewModel。</summary>
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (_isCompact == value) return;
            _isCompact = value;

            if (this.FindControl<Control>("FullPanel") is { } full) full.IsVisible = !value;
            if (this.FindControl<Control>("CompactRail") is { } rail) rail.IsVisible = value;
        }
    }

    private void OnExpandRequested(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is ChatShellWindow { Content: ChatShellView shell }) shell.ToggleSidebar();
    }

    /// <summary>点搜索图标展开/收起搜索框。</summary>
    private void OnSearchClicked(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Control>("SearchBox") is { } box)
        {
            box.IsVisible = !box.IsVisible;
            if (box.IsVisible) box.Focus();
        }
    }

    /// <summary>点三点设置按钮展开/收起 API Key 面板。</summary>
    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm)
            vm.IsSettingsOpen = !vm.IsSettingsOpen;
    }

    private void OnSessionClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not Session session) return;
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm)
            vm.SelectSessionCommand.Execute(session);
    }
}
