using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Chat.Abstractions;
using Chat.App.Rendering;
using Chat.App.Services;
using Chat.App.ViewModels;
using Chat.App.Views;
using Chat.Core;
using Xunit;

namespace Chat.App.Tests;

/// <summary>
/// 无显示器 UI 测试：在 Avalonia headless 平台里真实创建 <see cref="MainWindow"/>、加载 XAML、
/// 跑布局与绑定。每个用例使用独立临时数据目录，provider 可以是内置插件（真实加载，无 Key 不发网络）
/// 或注入的 <see cref="FakeChatProvider"/>（确定性覆盖流式、分支、停止、失败重试）。
/// </summary>
public class MainWindowHeadlessTests
{
    private const string DeepSeekId = "chat.provider.deepseek";
    private const string OpenAiId = "chat.provider.openai";
    private const string OpenCodeGoId = "chat.provider.opencodego";

    internal static async Task RunWindowAsync(
        Func<AppServices, MainWindowViewModel, MainWindow, Task> body,
        IEnumerable<IChatProvider>? providers = null,
        IClipboardService? clipboard = null)
    {
        // 环境变量是 ProviderContext.GetSecretAsync 的兜底：宿主如果设了 DEEPSEEK_APIKEY，
        // "未配置 Key" 用例会真的去连网。测试期间强制清空，结束后恢复。
        var savedKey = Environment.GetEnvironmentVariable("DEEPSEEK_APIKEY");
        Environment.SetEnvironmentVariable("DEEPSEEK_APIKEY", null);
        try
        {
            await HeadlessSession.RunAsync(async () =>
            {
                using var temp = new TempDataDirectory();
                using var services = AppServices.Create(temp.Path);
                var vm = new MainWindowViewModel(services, providers, clipboard);
                var window = new MainWindow { DataContext = vm };
                try
                {
                    window.Show();
                    HeadlessSession.Pump();
                    await body(services, vm, window).ConfigureAwait(true);
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

    private static async Task SendAsync(MainWindowViewModel vm, string text)
    {
        vm.Composer = text;
        await vm.SendCommand.ExecuteAsync(null).ConfigureAwait(true);
        HeadlessSession.Pump();
        HeadlessSession.Pump();
    }

    private static void SelectDeepSeek(MainWindowViewModel vm) =>
        vm.SelectedProvider = vm.Providers.Single(provider => provider.Id == DeepSeekId);

    [Fact]
    public Task Window_LoadsXamlAndBindsProvidersAndSessions() => RunWindowAsync((services, vm, window) =>
    {
        Assert.True(window.IsVisible);
        Assert.True(window.IsLoaded);
        Assert.NotNull(window.Content);
        Assert.Equal("LLMChat", window.Title);
        Assert.Same(vm, window.DataContext);

        // 三个内置插件都被复制到测试输出目录并真实加载。
        Assert.Equal(3, services.Plugins.Count);
        Assert.Empty(services.Failures);
        Assert.Equal(3, vm.Providers.Count);
        Assert.Contains(vm.Providers, provider => provider.Id == DeepSeekId);
        Assert.Contains(vm.Providers, provider => provider.Id == OpenAiId);
        Assert.Contains(vm.Providers, provider => provider.Id == OpenCodeGoId);
        Assert.Equal("插件 3 个已加载", vm.StatusText);

        var providerBox = window.FindControl<ComboBox>("ProvidersCombo");
        Assert.NotNull(providerBox);
        Assert.Same(vm.Providers, providerBox.ItemsSource);
        Assert.Same(vm.SelectedProvider, providerBox.SelectedItem);

        var sessionList = window.FindControl<ListBox>("SessionsList");
        Assert.NotNull(sessionList);
        Assert.Same(vm.Sessions, sessionList.ItemsSource);
        Assert.Empty(vm.Sessions);

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == vm.StatusText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SettingsExpander_ExpandsAndApiKeyRoundTrips() => RunWindowAsync((_, vm, window) =>
    {
        var expander = window.FindControl<Expander>("SettingsExpander");
        Assert.NotNull(expander);
        Assert.False(expander.IsExpanded);

        vm.IsSettingsOpen = true;
        HeadlessSession.Pump();
        Assert.True(expander.IsExpanded);

        var keyBox = Assert.Single(expander.GetVisualDescendants().OfType<TextBox>(), textBox => textBox.PasswordChar == '•');
        keyBox.Text = "sk-smoke";
        HeadlessSession.Pump();
        Assert.Equal("sk-smoke", vm.ApiKeyInput);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Composer_TextBoxRoundTripsToViewModel() => RunWindowAsync((_, vm, window) =>
    {
        var composer = window.FindControl<TextBox>("Composer");
        Assert.NotNull(composer);
        Assert.Equal("", composer.Text);

        composer.Text = "你好，headless";
        HeadlessSession.Pump();
        Assert.Equal("你好，headless", vm.Composer);

        vm.Composer = "回写方向";
        HeadlessSession.Pump();
        Assert.Equal("回写方向", composer.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Send_EmptyComposer_DoesNothing() => RunWindowAsync(async (services, vm, _) =>
    {
        vm.Composer = "   ";
        await vm.SendCommand.ExecuteAsync(null).ConfigureAwait(true);
        HeadlessSession.Pump();

        Assert.Empty(vm.Sessions);
        Assert.Empty(vm.Messages);
        Assert.Empty(services.Sessions.List());
        Assert.Equal("", vm.ErrorText);
        Assert.False(vm.IsStreaming);
        Assert.Equal("   ", vm.Composer);
    });

    [Fact]
    public Task Send_WithoutApiKey_PersistsUserMessageAndReportsError() => RunWindowAsync(async (services, vm, _) =>
    {
        SelectDeepSeek(vm);
        await SendAsync(vm, "冒烟测试：发一条消息").ConfigureAwait(true);

        Assert.Single(vm.Sessions);
        Assert.Equal(2, vm.Messages.Count);
        Assert.True(vm.Messages[0].IsUser);
        Assert.Equal("冒烟测试：发一条消息", vm.Messages[0].Text);
        Assert.False(vm.Messages[1].IsUser);
        Assert.Equal(MessageStatus.Failed, vm.Messages[1].Status);
        Assert.Equal("未配置 DeepSeek API Key。", vm.ErrorText);
        Assert.True(vm.HasError);
        Assert.False(vm.IsStreaming);
        Assert.Equal("", vm.Composer);

        var session = Assert.Single(services.Sessions.List());
        Assert.Equal("冒烟测试：发一条消息", session.Title);
        Assert.Equal(2, services.Messages.ListBySession(session.Id).Count);
    });

    [Fact]
    public Task SessionCommands_ExecuteWithoutStreaming() => RunWindowAsync((services, vm, _) =>
    {
        Assert.True(vm.StopCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        vm.RegenerateCommand.Execute(null);
        vm.PrevBranchCommand.Execute(null);
        vm.NextBranchCommand.Execute(null);
        HeadlessSession.Pump();

        Assert.Empty(vm.Messages);
        Assert.Equal("", vm.ErrorText);

        vm.NewSessionCommand.Execute(null);
        HeadlessSession.Pump();
        Assert.Single(vm.Sessions);
        Assert.Same(vm.Sessions[0], vm.SelectedSession);
        Assert.Single(services.Sessions.List());
        return Task.CompletedTask;
    });

    [Fact]
    public Task AssistantMarkdown_RendersIntoRichBlocksAndPresenters()
    {
        const string markdown = """
            # 标题

            段落 **粗体** 与 `code`，以及 [链接](https://example.com)。

            - 项目一
            - 项目二

            ```cs
            var x = 1;
            ```

            | A | B |
            |---|---|
            | 1 | 2 |

            > 引用文本
            """;

        var fake = new FakeChatProvider().Text(markdown);
        return RunWindowAsync(async (_, vm, window) =>
        {
            await SendAsync(vm, "渲染一下").ConfigureAwait(true);
            var message = vm.Messages[1];

            Assert.Equal(MessageStatus.Finished, message.Status);
            Assert.Contains(message.Blocks, block => block is MarkdownHeading { Level: 1 });
            Assert.Contains(message.Blocks, block => block is MarkdownList { IsOrdered: false });
            Assert.Contains(message.Blocks, block => block is MarkdownCodeBlock code && code.Language == "cs" && code.Code.Contains("var x = 1;"));
            Assert.Contains(message.Blocks, block => block is MarkdownTable { HasHeader: true });
            Assert.Contains(message.Blocks, block => block is MarkdownQuote);
            Assert.Contains(message.Blocks.OfType<MarkdownParagraph>(), paragraph =>
                paragraph.Inlines.Any(inline => inline.Kind == MarkdownInlineKind.Strong) &&
                paragraph.Inlines.Any(inline => inline.Kind == MarkdownInlineKind.Code) &&
                paragraph.Inlines.Any(inline => inline.Kind == MarkdownInlineKind.Link));

            HeadlessSession.Pump();
            Assert.NotEmpty(window.GetVisualDescendants().OfType<MarkdownParagraphPresenter>());
            Assert.NotEmpty(window.GetVisualDescendants().OfType<MarkdownTablePresenter>());
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(),
                block => block.Text?.Contains("var x = 1;") == true);
        }, [fake]);
    }

    [Fact]
    public Task Thinking_IsCollapsedAndSeparatedFromAnswer()
    {
        var fake = new FakeChatProvider().Thinking("先想一下", "答案在这里");
        return RunWindowAsync(async (_, vm, window) =>
        {
            await SendAsync(vm, "问题").ConfigureAwait(true);
            var message = vm.Messages[1];

            Assert.True(message.HasThinking);
            Assert.Equal("先想一下", message.Thinking);
            Assert.Equal("答案在这里", message.Text);
            Assert.Equal("思考过程", message.ThinkingHeader);
            Assert.DoesNotContain("先想一下", message.Text);

            // 用户气泡也会实例化思考 Expander（只是 IsVisible=false），这里只看可见的那个。
            var thinking = Assert.Single(window.GetVisualDescendants().OfType<Expander>(),
                expander => expander.IsVisible && (expander.Header as string) == "思考过程");
            Assert.False(thinking.IsExpanded);
        }, [fake]);
    }

    [Fact]
    public Task Edit_CreatesSiblingBranchAndNavigates()
    {
        var fake = new FakeChatProvider().Text("第一版回答").Text("第二版回答");
        return RunWindowAsync(async (services, vm, _) =>
        {
            await SendAsync(vm, "第一版").ConfigureAwait(true);
            var sessionId = vm.SelectedSession!.Id;
            Assert.Equal("", vm.BranchText);

            vm.BeginEditCommand.Execute(vm.Messages[0]);
            Assert.True(vm.IsEditing);
            Assert.Equal("第一版", vm.Composer);

            vm.Composer = "第二版";
            await vm.SendCommand.ExecuteAsync(null).ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.Equal(4, services.Messages.ListBySession(sessionId).Count);
            Assert.Equal(2, vm.Messages.Count);
            Assert.Equal("第二版", vm.Messages[0].Text);
            Assert.Equal("第二版回答", vm.Messages[1].Text);
            Assert.Equal("2/2", vm.BranchText);
            Assert.True(vm.CanNavigateBranch);

            vm.PrevBranchCommand.Execute(null);
            HeadlessSession.Pump();
            Assert.Equal("第一版", vm.Messages[0].Text);
            Assert.Equal("第一版回答", vm.Messages[1].Text);
            Assert.Equal("1/2", vm.BranchText);

            vm.NextBranchCommand.Execute(null);
            HeadlessSession.Pump();
            Assert.Equal("第二版", vm.Messages[0].Text);
            Assert.Equal("2/2", vm.BranchText);
        }, [fake]);
    }

    [Fact]
    public Task Regenerate_CreatesSiblingAssistantAndSwitches()
    {
        var fake = new FakeChatProvider().Text("v1").Text("v2");
        return RunWindowAsync(async (services, vm, _) =>
        {
            await SendAsync(vm, "问题").ConfigureAwait(true);
            var sessionId = vm.SelectedSession!.Id;

            await vm.RegenerateCommand.ExecuteAsync(null).ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.Equal(3, services.Messages.ListBySession(sessionId).Count);
            Assert.Equal("v2", vm.Messages[1].Text);
            Assert.Equal("2/2", vm.BranchText);

            vm.PrevBranchCommand.Execute(null);
            HeadlessSession.Pump();
            Assert.Equal("v1", vm.Messages[1].Text);
            Assert.Equal("1/2", vm.BranchText);
        }, [fake]);
    }

    [Fact]
    public Task RegenerateMessage_UsesClickedMessageParentAndSwitches()
    {
        // q1 → a1 → q2 → a2；点 a1 的“重新生成”必须挂在 q1 下建兄弟，而不是重生成 a2。
        var fake = new FakeChatProvider().Text("a1").Text("a2").Text("a1b");
        return RunWindowAsync(async (services, vm, _) =>
        {
            await SendAsync(vm, "q1").ConfigureAwait(true);
            await SendAsync(vm, "q2").ConfigureAwait(true);
            var sessionId = vm.SelectedSession!.Id;

            var nodes = services.Messages.ListBySession(sessionId);
            Assert.Equal(4, nodes.Count);
            Assert.Equal(MessageRole.User, nodes[0].Role);
            Assert.Equal(MessageRole.Assistant, nodes[1].Role);
            Assert.Equal(MessageRole.User, nodes[2].Role);
            Assert.Equal(MessageRole.Assistant, nodes[3].Role);
            Assert.Equal("a1", vm.Messages[1].Text);

            await vm.RegenerateMessageCommand.ExecuteAsync(vm.Messages[1]).ConfigureAwait(true);
            HeadlessSession.Pump();

            var after = services.Messages.ListBySession(sessionId);
            Assert.Equal(5, after.Count);
            var newAssistant = Assert.Single(after, node => node.Role == MessageRole.Assistant
                && node.ParentId == nodes[0].Id && node.Id != nodes[1].Id);
            Assert.Equal(nodes[0].Id, newAssistant.ParentId);

            // 当前指针切到新兄弟，界面只显示 q1 → 新回答。
            Assert.Equal(newAssistant.Id, vm.SelectedSession!.CurrentMessageId);
            Assert.Equal(2, vm.Messages.Count);
            Assert.Equal(nodes[0].Id, vm.Messages[0].Id);
            Assert.Equal(newAssistant.Id, vm.Messages[1].Id);
            Assert.Equal("a1b", vm.Messages[1].Text);
            Assert.Equal("2/2", vm.BranchText);
            Assert.Equal(3, fake.Requests.Count);
        }, [fake]);
    }

    [Fact]
    public Task ProviderCancellation_MarksTimeoutWithError()
    {
        // provider 自己超时（宿主没有点停止）不能被当成「已停止」，应显示超时并暴露错误。
        var fake = new FakeChatProvider().UpstreamTimeout("上游 30s 超时");
        return RunWindowAsync(async (services, vm, _) =>
        {
            await SendAsync(vm, "hi").ConfigureAwait(true);

            Assert.False(vm.IsStreaming);
            Assert.Equal(MessageStatus.Timeout, vm.Messages[1].Status);
            Assert.Equal("超时", vm.Messages[1].StatusText);
            Assert.True(vm.Messages[1].CanRetry);
            Assert.Equal("请求超时：上游 30s 超时", vm.ErrorText);
            Assert.True(vm.HasError);

            var persisted = services.Messages.ListBySession(vm.SelectedSession!.Id)[1];
            Assert.Equal(MessageStatus.Timeout, persisted.Status);
        }, [fake]);
    }

    [Fact]
    public Task SendDuringStreaming_IsIgnored()
    {
        var fake = new FakeChatProvider().Blocking("生成中");
        return RunWindowAsync(async (services, vm, _) =>
        {
            vm.Composer = "第一条";
            var running = vm.SendCommand.ExecuteAsync(null);
            HeadlessSession.Pump();
            Assert.True(vm.IsStreaming);

            vm.Composer = "第二条";
            await vm.SendCommand.ExecuteAsync(null).ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.Equal("第二条", vm.Composer);
            Assert.Single(services.Sessions.List());
            Assert.Equal(2, services.Messages.ListBySession(vm.SelectedSession!.Id).Count);
            Assert.Single(fake.Requests);

            vm.StopCommand.Execute(null);
            await running.ConfigureAwait(true);
            HeadlessSession.Pump();
            Assert.False(vm.IsStreaming);
        }, [fake]);
    }

    [Fact]
    public Task ImeProcessedEnter_DoesNotSend()
    {
        // Avalonia 12 没有公开的 IME composition 标志：平台把组合期间的按键报告为
        // Key.ImeProcessed 时不能触发发送。其他平台若把组合提交键报告为 Key.Enter，
        // 当前无法区分（见 tests/Chat.App.Tests/README.md 的已知限制）。
        var fake = new FakeChatProvider().Text("不应发出");
        return RunWindowAsync((services, vm, window) =>
        {
            var composer = window.FindControl<TextBox>("Composer");
            Assert.NotNull(composer);
            composer.Text = "候选词";
            composer.Focus();
            HeadlessSession.Pump();
            Assert.True(composer.IsFocused);

            window.KeyPress(Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            HeadlessSession.Pump();
            HeadlessSession.Pump();

            Assert.Empty(services.Sessions.List());
            Assert.Empty(vm.Messages);
            Assert.Equal("候选词", vm.Composer);
            Assert.Empty(fake.Requests);
            return Task.CompletedTask;
        }, [fake]);
    }

    [Fact]
    public Task Stop_CancelsStreamAndMarksIncomplete()
    {
        var fake = new FakeChatProvider().BlockingThinking("先想一想", "开始生成");
        return RunWindowAsync(async (_, vm, window) =>
        {
            vm.Composer = "长回答";
            var running = vm.SendCommand.ExecuteAsync(null);
            HeadlessSession.Pump();

            Assert.True(vm.IsStreaming);
            Assert.True(vm.ShowStop);
            Assert.False(vm.ShowSend);
            Assert.Equal("思考中…", vm.Messages[1].ThinkingHeader);
            var streaming = Assert.Single(window.GetVisualDescendants().OfType<Expander>(),
                expander => (expander.Header as string) == "思考中…");
            Assert.False(streaming.IsExpanded);

            vm.StopCommand.Execute(null);
            await running.ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.False(vm.IsStreaming);
            Assert.True(vm.ShowSend);
            Assert.False(vm.ShowStop);
            Assert.Equal(MessageStatus.Incomplete, vm.Messages[1].Status);
            Assert.Equal("已停止", vm.Messages[1].StatusText);
            Assert.Equal("已停止", vm.StatusText);
            Assert.Equal("思考过程", vm.Messages[1].ThinkingHeader);
            Assert.True(vm.Messages[1].HasThinking);
        }, [fake]);
    }

    [Fact]
    public Task RenamePinSearchAndDelete_WorkThroughCommands() => RunWindowAsync((services, vm, window) =>
    {
        vm.SearchDebounce = TimeSpan.Zero;

        vm.NewSessionCommand.Execute(null);
        HeadlessSession.Pump();
        vm.BeginRenameCommand.Execute(null);
        Assert.True(vm.IsRenaming);
        vm.RenameText = "Alpha";
        vm.CommitRenameCommand.Execute(null);
        HeadlessSession.Pump();

        Assert.False(vm.IsRenaming);
        Assert.Equal("Alpha", Assert.Single(services.Sessions.List()).Title);
        Assert.Equal("Alpha", vm.Sessions[0].Title);
        var title = window.FindControl<TextBlock>("SessionTitle");
        Assert.NotNull(title);
        Assert.Equal("Alpha", title.Text);

        vm.TogglePinCommand.Execute(null);
        HeadlessSession.Pump();
        Assert.True(vm.IsPinned);
        Assert.True(vm.Sessions[0].Pinned);
        Assert.Equal("已置顶", vm.StatusText);

        vm.NewSessionCommand.Execute(null);
        HeadlessSession.Pump();
        vm.BeginRenameCommand.Execute(null);
        vm.RenameText = "Beta";
        vm.CommitRenameCommand.Execute(null);
        HeadlessSession.Pump();
        Assert.Equal(2, vm.Sessions.Count);

        vm.SearchText = "Beta";
        HeadlessSession.Pump();
        Assert.Single(vm.Sessions);
        Assert.Equal("Beta", vm.Sessions[0].Title);
        Assert.Same(vm.Sessions[0], vm.SelectedSession);

        vm.SearchText = "";
        HeadlessSession.Pump();
        Assert.Equal(2, vm.Sessions.Count);
        Assert.Equal("Alpha", vm.Sessions[0].Title);

        vm.SelectedSession = vm.Sessions.Single(session => session.Title == "Alpha");
        HeadlessSession.Pump();
        vm.RequestDeleteSessionCommand.Execute(null);
        Assert.True(vm.IsDeleteConfirmVisible);
        Assert.Equal(2, services.Sessions.List().Count);

        vm.ConfirmDeleteSessionCommand.Execute(null);
        HeadlessSession.Pump();
        Assert.False(vm.IsDeleteConfirmVisible);
        Assert.Single(services.Sessions.List());
        Assert.Equal(2, services.Sessions.List(includeDeleted: true).Count);
        Assert.Single(vm.Sessions);
        Assert.False(vm.ShowEmptyState);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EnterKey_SendsAndShiftEnterDoesNot()
    {
        var fake = new FakeChatProvider().Text("收到").Text("收到2");
        return RunWindowAsync((services, vm, window) =>
        {
            var composer = window.FindControl<TextBox>("Composer");
            Assert.NotNull(composer);
            composer.Text = "回车发送";
            composer.Focus();
            HeadlessSession.Pump();
            Assert.True(composer.IsFocused);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            HeadlessSession.Pump();
            HeadlessSession.Pump();

            Assert.Single(services.Sessions.List());
            Assert.Equal(2, vm.Messages.Count);
            Assert.Equal("", vm.Composer);

            composer.Text = "换行文本";
            HeadlessSession.Pump();
            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, "\r");
            HeadlessSession.Pump();

            Assert.Single(services.Sessions.List());
            Assert.Equal(2, vm.Messages.Count);
            Assert.StartsWith("换行文本", vm.Composer);
            return Task.CompletedTask;
        }, [fake]);
    }

    [Fact]
    public Task CopyMessage_UsesInjectedClipboard()
    {
        var fake = new FakeChatProvider().Text("复制我");
        var clipboard = new FakeClipboard();
        return RunWindowAsync(async (_, vm, window) =>
        {
            await SendAsync(vm, "hi").ConfigureAwait(true);
            await vm.CopyMessageCommand.ExecuteAsync(vm.Messages[1]).ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.Equal("复制我", vm.LastCopiedText);
            Assert.Equal(["复制我"], clipboard.Texts);
            Assert.Equal("已复制", vm.StatusText);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => (button.Content as string) == "复制");
        }, [fake], clipboard);
    }

    [Fact]
    public Task Failure_ShowsRetryAndDismissesError()
    {
        var fake = new FakeChatProvider().Failure("boom", "网络炸了").Text("重试成功");
        return RunWindowAsync(async (services, vm, window) =>
        {
            await SendAsync(vm, "hi").ConfigureAwait(true);
            var sessionId = vm.SelectedSession!.Id;
            Assert.Equal(MessageStatus.Failed, vm.Messages[1].Status);
            Assert.Equal("网络炸了", vm.ErrorText);
            Assert.True(vm.HasError);
            Assert.True(vm.Messages[1].CanRetry);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(),
                button => (button.Content as string) == "重试" && button.IsVisible);

            await vm.RetryMessageCommand.ExecuteAsync(vm.Messages[1]).ConfigureAwait(true);
            HeadlessSession.Pump();

            Assert.Equal(3, services.Messages.ListBySession(sessionId).Count);
            Assert.Equal("重试成功", vm.Messages[1].Text);
            Assert.Equal(MessageStatus.Finished, vm.Messages[1].Status);
            Assert.Equal("", vm.ErrorText);
            Assert.False(vm.HasError);

            vm.ErrorText = "手动错误";
            HeadlessSession.Pump();
            var errorBar = window.FindControl<Border>("ErrorBar");
            Assert.NotNull(errorBar);
            Assert.True(errorBar.IsVisible);

            vm.DismissErrorCommand.Execute(null);
            HeadlessSession.Pump();
            Assert.False(vm.HasError);
            Assert.False(errorBar.IsVisible);
        }, [fake]);
    }

    [Fact]
    public Task EmptyState_ShowsGuidanceUntilSessionExists() => RunWindowAsync((_, vm, window) =>
    {
        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.ShowNoPlugins);
        var empty = window.FindControl<Border>("EmptyState");
        Assert.NotNull(empty);
        Assert.True(empty.IsVisible);

        vm.NewSessionCommand.Execute(null);
        HeadlessSession.Pump();
        Assert.False(vm.ShowEmptyState);
        Assert.False(empty.IsVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NoPlugins_ShowsGuidance() => RunWindowAsync((_, vm, window) =>
    {
        Assert.False(vm.HasPlugins);
        Assert.True(vm.ShowNoPlugins);
        Assert.Equal("插件 0 个已加载", vm.StatusText);
        Assert.True(vm.ShowEmptyState);
        var empty = window.FindControl<Border>("EmptyState");
        Assert.NotNull(empty);
        Assert.True(empty.IsVisible);
        return Task.CompletedTask;
    }, Array.Empty<IChatProvider>());

    [Fact]
    public Task MessageViewModel_CoalescesStreamingRenders() => HeadlessSession.RunAsync(() =>
    {
        var node = new MessageNode
        {
            Id = 1,
            ParentId = null,
            Role = MessageRole.Assistant,
            Status = MessageStatus.Wip,
            Parts = [ContentPart.OfText("a")],
        };
        var vm = new MessageViewModel(node);
        Assert.Equal(1, vm.RenderCount);
        vm.RenderThrottle = TimeSpan.FromHours(1);

        vm.Text = "b";
        vm.Text = "c";
        Assert.Equal(1, vm.RenderCount);

        vm.FlushRender();
        Assert.Equal(2, vm.RenderCount);
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(vm.Blocks));
        Assert.Equal("c", Assert.Single(paragraph.Inlines).Text);
        return Task.CompletedTask;
    });
}