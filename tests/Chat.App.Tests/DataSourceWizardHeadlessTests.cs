using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Chat.App.Services;
using Chat.App.ViewModels;
using Chat.App.Views;
using Xunit;

namespace Chat.App.Tests;

/// <summary>
/// M-A1 headless UI 验证：真窗口 + 真绑定 + 假 launcher/假 HTTP（无浏览器、无真实网络）。
/// 所有交互与断言都在 headless 会话的 window body 内完成——窗口关闭后 services/临时目录即被释放。
/// </summary>
public class DataSourceWizardHeadlessTests
{
    private const string TestKey = "sk-headless-wizard-9876";

    private static Task RunAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Func<AppServices, MainWindowViewModel, MainWindow, FakeUrlLauncher, StubHttpHandler, Task> body) =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, window) =>
        {
            var launcher = new FakeUrlLauncher();
            var handler = new StubHttpHandler((request, _) => Task.FromResult(respond(request)));
            var http = new HttpMessageInvoker(handler);
            // 用注入的 launcher/http 重建 VM，保证向导走假实现。
            vm = new MainWindowViewModel(services, [new FakeChatProvider().Text("ok")], null,
                new DeepSeekAccountService(http, services.Settings, launcher));
            window.DataContext = vm;
            vm.IsSettingsOpen = true;
            HeadlessSession.Pump();
            await body(services, vm, window, launcher, handler).ConfigureAwait(true);
        });

    private static Expander Settings(MainWindow window) =>
        window.FindControl<Expander>("SettingsExpander") ?? throw new InvalidOperationException("设置区未找到");

    [Fact]
    public Task Wizard_ButtonsBindToCommands_AndUseFakeLauncher() => RunAsync(
        _ => new HttpResponseMessage(HttpStatusCode.OK),
        async (_, vm, window, launcher, handler) =>
        {
            var expander = Settings(window);
            var createButton = Assert.Single(expander.GetVisualDescendants().OfType<Button>(),
                b => b.Content as string == "创建 / 复制 Key");
            var topUpButton = Assert.Single(expander.GetVisualDescendants().OfType<Button>(),
                b => b.Content as string == "去充值");
            Assert.NotNull(createButton.Command);
            Assert.NotNull(topUpButton.Command);

            createButton.Command!.Execute(null);
            topUpButton.Command!.Execute(null);
            Assert.Equal(new[] { DeepSeekAccountService.ApiKeysPage, DeepSeekAccountService.TopUpPage }, launcher.Launched);
            Assert.Empty(handler.Requests);
            await Task.CompletedTask;
        });

    [Fact]
    public Task VerifyAndSave_WritesSettingsAndShowsBalance_ThroughRealViewModel()
    {
        const string body = """
            {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"88.880000","granted_balance":"1.000000","topped_up_balance":"87.880000"}]}
            """;
        return RunAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, async (services, vm, window, launcher, handler) =>
        {
            vm.ApiKeyInput = TestKey;
            var button = Assert.Single(Settings(window).GetVisualDescendants().OfType<Button>(),
                b => b.Content as string == "验证并保存");
            HeadlessSession.Pump();
            Assert.True(vm.CanVerifyAccount);
            await vm.VerifyAndSaveAccountCommand.ExecuteAsync(null).ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.False(vm.IsAccountBusy);
            Assert.True(vm.IsAccountAvailable);
            Assert.Contains("88.880000", vm.BalanceText);
            Assert.Contains("CNY", vm.BalanceText);
            Assert.DoesNotContain(TestKey, vm.WizardStatusText);
            Assert.DoesNotContain(TestKey, vm.BalanceText);
            Assert.Equal(TestKey, services.Settings.Get(MainWindowViewModel.SecretKey));
            Assert.Single(handler.Requests);
            Assert.Empty(launcher.Launched);
            var balanceBlock = Settings(window).FindControl<TextBlock>("BalanceText");
            Assert.NotNull(balanceBlock);
            Assert.Equal(vm.BalanceText, balanceBlock!.Text);
        });
    }

    [Fact]
    public Task VerifyAndSave_ClassifiesUnauthorized_AndKeepsOldKey() => RunAsync(
        _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        async (services, vm, _, _, _) =>
        {
            services.Settings.Set(MainWindowViewModel.SecretKey, "sk-existing-key");
            vm.ApiKeyInput = TestKey;
            await vm.VerifyAndSaveAccountCommand.ExecuteAsync(null).ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.False(vm.IsAccountAvailable);
            Assert.Contains("无效", vm.WizardStatusText);
            Assert.DoesNotContain(TestKey, vm.WizardStatusText);
            Assert.Equal("sk-existing-key", services.Settings.Get(MainWindowViewModel.SecretKey));
        });
}
