using Avalonia.Controls;
using Avalonia.VisualTree;
using Chat.App.Services;
using Chat.App.ViewModels;
using Chat.App.Views;
using Xunit;

namespace Chat.App.Tests;

/// <summary>
/// DeepSeek 风格外壳的无显示器 UI 测试：在 headless 平台真实创建 <see cref="ChatShellView"/>，
/// 加载 XAML、跑布局与绑定，验证侧栏折叠、断点切换、Composer 绑定与发送/停止状态切换。
/// </summary>
public class ChatShellHeadlessTests
{
    private static async Task RunShellAsync(Func<AppServices, MainWindowViewModel, ChatShellView, Task> body)
    {
        var savedKey = Environment.GetEnvironmentVariable("DEEPSEEK_APIKEY");
        Environment.SetEnvironmentVariable("DEEPSEEK_APIKEY", null);
        try
        {
            await HeadlessSession.RunAsync(async () =>
            {
                using var temp = new TempDataDirectory();
                using var services = AppServices.Create(temp.Path);
                var vm = new MainWindowViewModel(services);
                var shell = new ChatShellView { DataContext = vm };
                var window = new Window { Content = shell, Width = 1280, Height = 800 };
                try
                {
                    window.Show();
                    HeadlessSession.Pump();
                    await body(services, vm, shell).ConfigureAwait(true);
                }
                finally
                {
                    window.Close();
                    HeadlessSession.Pump();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_APIKEY", savedKey);
        }
    }

    private static T Find<T>(ChatShellView shell, string name) where T : Control =>
        shell.FindControl<T>(name) ?? throw new Xunit.Sdk.XunitException($"未找到控件 {name}");

    [Fact]
    public Task Shell_LoadsXamlWithAllSubViews() => RunShellAsync((_, vm, shell) =>
    {
        Assert.Same(vm, shell.DataContext);
        Find<SidebarView>(shell, "Sidebar");
        Find<WelcomeView>(shell, "Welcome");
        Find<ConversationView>(shell, "Conversation");
        // 顶栏已移除，主题切换在窗口标题栏、折叠在侧栏内。
        Assert.Null(shell.FindControl<TopBarView>("TopBar"));
        Assert.NotNull(shell.FindControl<Grid>("LayoutRoot"));
        Assert.NotNull(shell.FindControl<Border>("SidebarHost"));
        Assert.NotNull(shell.FindControl<Border>("Scrim"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Shell_DefaultsToWideAndSidebarOpen() => RunShellAsync((_, _, shell) =>
    {
        Assert.Contains("wide", shell.Classes);
        Assert.Contains("sidebar-open", shell.Classes);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ToggleSidebar_ClosesAndReopensOnWide() => RunShellAsync((_, _, shell) =>
    {
        var host = Find<Border>(shell, "SidebarHost");
        Assert.Contains("sidebar-open", shell.Classes);
        Assert.Equal(260, host.Width);

        shell.ToggleSidebar();
        HeadlessSession.Pump();
        Assert.DoesNotContain("sidebar-open", shell.Classes);
        Assert.Equal(60, host.Width);

        shell.ToggleSidebar();
        HeadlessSession.Pump();
        Assert.Contains("sidebar-open", shell.Classes);
        Assert.Equal(260, host.Width);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Composer_TextBoxRoundTripsToViewModel() => RunShellAsync((_, vm, shell) =>
    {
        var box = shell.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.Name == "ComposerBox")
            ?? throw new Xunit.Sdk.XunitException("未找到 ComposerBox");

        box.Text = "你好，DeepSeek";
        HeadlessSession.Pump();
        Assert.Equal("你好，DeepSeek", vm.Composer);

        vm.Composer = "回写";
        HeadlessSession.Pump();
        Assert.Equal("回写", box.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Composer_SendAndStopButtonsFollowStreamingState() => RunShellAsync((_, vm, shell) =>
    {
        var buttons = shell.GetVisualDescendants().OfType<Button>().ToList();
        var send = buttons.FirstOrDefault(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "发送")
            ?? throw new Xunit.Sdk.XunitException("未找到发送按钮");
        var stop = buttons.FirstOrDefault(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "停止生成")
            ?? throw new Xunit.Sdk.XunitException("未找到停止按钮");

        Assert.True(send.IsVisible);
        Assert.False(stop.IsVisible);

        vm.IsStreaming = true;
        HeadlessSession.Pump();
        Assert.False(send.IsVisible);
        Assert.True(stop.IsVisible);
        return Task.CompletedTask;
    });
}
