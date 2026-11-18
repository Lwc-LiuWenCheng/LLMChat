using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Chat.App.Views;

/// <summary>
/// DeepSeek 风格外壳：只做布局编排、断点切换与侧栏折叠等纯 UI 状态，
/// 业务逻辑全部复用继承而来的 MainWindowViewModel DataContext。
/// </summary>
public partial class ChatShellView : UserControl
{
    // 断点阈值集中定义，避免 XAML 与 code-behind 出现魔法数字。
    private const double WideThreshold = 1100;
    private const double MediumThreshold = 720;

    private string? _breakpoint;
    private bool _sidebarOpen;

    public ChatShellView()
    {
        InitializeComponent();
        // 在 code-behind 订阅，避免 XAML 事件特性在部分 XAML 工具链下无法解析。
        if (this.FindControl<Grid>("LayoutRoot") is { } layout)
            layout.SizeChanged += OnLayoutSizeChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnLayoutSizeChanged(object? sender, SizeChangedEventArgs e) =>
        ApplyBreakpoint(e.NewSize.Width);

    private void ApplyBreakpoint(double width)
    {
        var breakpoint = width >= WideThreshold ? "wide"
            : width >= MediumThreshold ? "medium"
            : "narrow";
        if (breakpoint == _breakpoint) return;
        _breakpoint = breakpoint;

        Classes.Set("wide", breakpoint == "wide");
        Classes.Set("medium", breakpoint == "medium");
        Classes.Set("narrow", breakpoint == "narrow");

        // 宽屏默认展开；中/窄屏默认收起，避免遮挡内容。任何断点下汉堡按钮都可切换。
        SetSidebarOpen(breakpoint == "wide");

        if (this.FindControl<WelcomeView>("Welcome") is { } welcome)
            welcome.Classes.Set("narrow", breakpoint == "narrow");
    }

    /// <summary>顶栏汉堡按钮调用：任意断点下都切换侧栏展开状态。</summary>
    public void ToggleSidebar() => SetSidebarOpen(!_sidebarOpen);

    private void SetSidebarOpen(bool open)
    {
        _sidebarOpen = open;
        Classes.Set("sidebar-open", open);

        // 侧栏宽度/遮罩由代码直接设置：Avalonia 样式 setter 在运行时 class 变化后
        // 未必及时重算（且本地值会盖掉样式），这里作为纯 UI 状态直接控制最可靠。
        var host = this.FindControl<Border>("SidebarHost");
        if (host is not null)
        {
            var drawer = _breakpoint != "wide";
            // 收起时保留 60px 图标条（内含展开按钮），保证"展开/收起都在侧栏内完成"。
            host.Width = open ? (drawer ? 280 : 260) : 60;
            Grid.SetColumnSpan(host, open && drawer ? 2 : 1);
        }

        var scrim = this.FindControl<Border>("Scrim");
        if (scrim is not null)
            scrim.IsVisible = open && _breakpoint != "wide";

        // 收起时显示图标条，展开时显示完整列表。
        if (this.FindControl<SidebarView>("Sidebar") is { } sidebar)
            sidebar.IsCompact = !open;
    }

    private void OnScrimPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) =>
        SetSidebarOpen(false);
}
