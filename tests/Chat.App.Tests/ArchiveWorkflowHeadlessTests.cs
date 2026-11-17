using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Chat.App.Services;
using Chat.App.ViewModels;
using Chat.App.Views;
using Chat.Core;
using Chat.Storage.ChatDemo;
using Xunit;

namespace Chat.App.Tests;

/// <summary>App 层导入/导出：真实 headless 窗口里的确认、进度收尾、列表刷新与错误提示。</summary>
public sealed class ArchiveWorkflowHeadlessTests
{
    private static string WriteArchive(string dataDirectory, string sessionId = "s1", string title = "导入的会话") =>
        TestArchives.WriteV3(Path.Combine(dataDirectory, "backup-" + Guid.NewGuid().ToString("N") + ".zip"), sessionId, title);

    /// <summary>用真实鼠标事件点击按钮，验证 Command 绑定/可见性，而不是直接调用 VM。</summary>
    private static void Click(MainWindow window, Control control)
    {
        HeadlessSession.Pump();
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point.Value, MouseButton.Left, RawInputModifiers.None);
        HeadlessSession.Pump();
    }

    [Fact]
    public Task Window_ArchivePanelStartsHiddenAndHasEntryPoints() =>
        MainWindowHeadlessTests.RunWindowAsync((_, _, window) =>
        {
            var panel = window.FindControl<Border>("ImportPanel");
            Assert.NotNull(panel);
            Assert.False(panel.IsVisible);
            Assert.NotNull(window.FindControl<Button>("ImportButton"));
            Assert.NotNull(window.FindControl<Button>("ExportCurrentButton"));
            Assert.NotNull(window.FindControl<Button>("ExportAllButton"));
            return Task.CompletedTask;
        });

    [Fact]
    public Task Import_ConfirmRefreshesListSelectsImportedAndKeepsLocalSessions() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, window) =>
        {
            services.Sessions.Upsert(new Session { Id = "local", Title = "本地会话" });
            vm.RefreshSessions("local");
            Assert.Single(vm.Sessions);

            var zip = WriteArchive(services.DataDirectory);
            var preflight = await vm.PrepareImportAsync(zip);
            Assert.NotNull(preflight);
            Assert.True(vm.IsArchiveConfirmVisible);
            Assert.Contains("1 个会话", vm.ArchiveConfirmationText);
            Assert.Contains("0 个附件", vm.ArchiveConfirmationText);

            var panel = window.FindControl<Border>("ImportPanel");
            Assert.NotNull(panel);
            Assert.True(panel.IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == vm.ArchiveConfirmationText);

            await vm.ConfirmImportCommand.ExecuteAsync(null);
            HeadlessSession.Pump();

            Assert.False(vm.IsArchiveBusy);
            Assert.False(vm.IsArchiveConfirmVisible);
            Assert.True(vm.HasArchiveSummary);
            Assert.Contains("新增 1", vm.ArchiveSummaryText);
            Assert.Contains(";", vm.ArchiveSummaryText);
            Assert.Contains(vm.Sessions, session => session.Id == "s1");
            Assert.Contains(vm.Sessions, session => session.Id == "local");
            Assert.Equal("s1", vm.SelectedSession?.Id);
            Assert.Equal(2, vm.Messages.Count);

            // 消息树：id/parent/role/parts 都要在导入后保持。
            var nodes = services.Messages.ListBySession("s1");
            Assert.Equal(new long[] { 1, 2 }, nodes.Select(node => node.Id).ToArray());
            var user = nodes.Single(node => node.Id == 1);
            Assert.Null(user.ParentId);
            Assert.Equal(MessageRole.User, user.Role);
            Assert.Equal(PartKind.Text, Assert.Single(user.Parts).Kind);
            Assert.Equal("你好", Assert.Single(user.Parts).Text);
            var assistant = nodes.Single(node => node.Id == 2);
            Assert.Equal(1L, assistant.ParentId);
            Assert.Equal(MessageRole.Assistant, assistant.Role);
            Assert.Equal(new[] { PartKind.Thinking, PartKind.Text }, assistant.Parts.Select(part => part.Kind).ToArray());
            Assert.Equal(new[] { "想一下", "收到" }, assistant.Parts.Select(part => part.Text).ToArray());

            Assert.True(panel.IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text?.Contains("导入完成") == true);

            // F1：摘要/关闭按钮绑定 HasArchiveSummary；关闭按钮必须真的可见且点击后隐藏面板。
            var summary = window.FindControl<TextBlock>("ArchiveSummaryBlock");
            Assert.NotNull(summary);
            Assert.True(summary.IsVisible);
            Assert.Equal(vm.ArchiveSummaryText, summary.Text);
            var close = window.FindControl<Button>("CloseArchiveSummaryButton");
            Assert.NotNull(close);
            Assert.True(close.IsVisible);
            Click(window, close);
            Assert.False(vm.HasArchiveSummary);
            Assert.False(panel.IsVisible);
        });

    [Fact]
    public Task Import_BadArchiveShowsReadableErrorAndNoConfirmation() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, _) =>
        {
            // 先放一条本地数据，确认失败路径不会清库/半写。
            services.Sessions.Upsert(new Session { Id = "local", Title = "本地会话", CurrentMessageId = 1 });
            services.Messages.Insert("local", new MessageNode
            {
                Id = 1, ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("保留我")],
            });
            vm.RefreshSessions("local");
            var sessionsBefore = services.Sessions.List().Count;
            var messagesBefore = services.Messages.ListBySession("local").Count;

            var broken = Path.Combine(services.DataDirectory, "broken.zip");
            await File.WriteAllTextAsync(broken, "not a zip", TestContext.Current.CancellationToken);

            var preflight = await vm.PrepareImportAsync(broken);

            Assert.Null(preflight);
            Assert.False(vm.IsArchiveConfirmVisible);
            Assert.False(vm.HasArchiveSummary);
            Assert.StartsWith("无法读取备份文件：", vm.ErrorText);
            Assert.Equal("读取失败", vm.ArchiveStatusText);
            Assert.Equal(sessionsBefore, services.Sessions.List().Count);
            Assert.Equal(messagesBefore, services.Messages.ListBySession("local").Count);
            Assert.Equal("local", vm.SelectedSession?.Id);
        });

    [Fact]
    public Task Import_CancelPendingConfirmationHidesPanelWithoutWriting() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, window) =>
        {
            var zip = WriteArchive(services.DataDirectory);
            await vm.PrepareImportAsync(zip);
            Assert.True(vm.IsArchiveConfirmVisible);

            vm.CancelImportCommand.Execute(null);
            HeadlessSession.Pump();

            Assert.False(vm.IsArchiveConfirmVisible);
            Assert.False(vm.IsArchiveBusy);
            Assert.Equal("已取消导入", vm.ArchiveStatusText);
            Assert.Empty(vm.Sessions);
            Assert.Empty(services.Sessions.List());
            var panel = window.FindControl<Border>("ImportPanel");
            Assert.NotNull(panel);
            Assert.False(panel.IsVisible);
        });

    [Fact]
    public Task Import_CancelOperationDuringPreflightShowsCancelledState() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, window) =>
        {
            var zip = WriteArchive(services.DataDirectory);
            // IsArchiveBusy 在 Task.Run 之前同步置位，事件回调里点“取消操作”可稳定取消预检。
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsArchiveBusy) && vm.IsArchiveBusy)
                    vm.CancelArchiveOperationCommand.Execute(null);
            };

            var preflight = await vm.PrepareImportAsync(zip);

            Assert.Null(preflight);
            Assert.False(vm.IsArchiveBusy);
            Assert.False(vm.IsArchiveConfirmVisible);
            Assert.False(vm.HasArchiveSummary);
            Assert.Equal("读取已取消", vm.ArchiveStatusText);
            Assert.Empty(services.Sessions.List());
            var panel = window.FindControl<Border>("ImportPanel");
            Assert.NotNull(panel);
            Assert.False(panel.IsVisible);
        });

    [Fact]
    public Task Import_PreflightUsesJsonIdWhenFileNameDiffersAndSelectsIt() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, _) =>
        {
            services.Sessions.Upsert(new Session { Id = "local", Title = "本地会话" });
            vm.RefreshSessions("local");

            var zip = TestArchives.WriteV3(
                Path.Combine(services.DataDirectory, "mismatch.zip"),
                sessionId: "real-session",
                title: "真实会话",
                fileNameSessionId: "file-name");

            var preflight = await vm.PrepareImportAsync(zip);

            Assert.NotNull(preflight);
            Assert.Equal(new[] { "real-session" }, preflight.SessionIds);
            Assert.Contains(preflight.Warnings, warning => warning.Contains("与文件名不一致"));

            await vm.ConfirmImportCommand.ExecuteAsync(null);
            HeadlessSession.Pump();

            Assert.Equal("real-session", vm.SelectedSession?.Id);
            Assert.Contains(vm.Sessions, session => session.Id == "real-session");
            Assert.DoesNotContain(vm.Sessions, session => session.Id == "file-name");
            Assert.Contains(vm.Sessions, session => session.Id == "local");
            Assert.Equal(2, vm.Messages.Count);
        });

    [Fact]
    public Task Import_PreflightAndImportShareIndexFallbackWhenSessionJsonHasNoId() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, _) =>
        {
            services.Sessions.Upsert(new Session { Id = "local", Title = "本地会话" });
            vm.RefreshSessions("local");

            var zip = TestArchives.WriteV3(
                Path.Combine(services.DataDirectory, "no-json-id.zip"),
                sessionId: "index-session",
                title: "索引会话",
                fileNameSessionId: "file-name",
                sessionJson: """
                {
                  "title": "索引会话",
                  "created_at": "2024-05-01T12:00:00Z",
                  "updated_at": "2024-05-01T12:05:00Z",
                  "messages": [
                    {
                      "id": 1,
                      "parent_id": null,
                      "role": "USER",
                      "status": "FINISHED",
                      "fragments": [{ "type": "REQUEST", "content": "来自索引" }]
                    }
                  ]
                }
                """);

            var preflight = await vm.PrepareImportAsync(zip);

            Assert.NotNull(preflight);
            Assert.Equal(new[] { "index-session" }, preflight.SessionIds);

            await vm.ConfirmImportCommand.ExecuteAsync(null);
            HeadlessSession.Pump();

            Assert.Equal("index-session", vm.SelectedSession?.Id);
            Assert.Contains(vm.Sessions, session => session.Id == "index-session");
            Assert.DoesNotContain(vm.Sessions, session => session.Id == "file-name");
            Assert.Contains(vm.Sessions, session => session.Id == "local");
            Assert.Single(vm.Messages);
        });

    [Fact]
    public Task Import_FailureLeavesDatabaseRowsUnchanged() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, window) =>
        {
            services.Sessions.Upsert(new Session { Id = "local", Title = "本地会话", CurrentMessageId = 1 });
            services.Messages.Insert("local", new MessageNode
            {
                Id = 1, ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("保留我")],
            });
            vm.RefreshSessions("local");
            var sessionsBefore = services.Sessions.List().Count;
            var messagesBefore = services.Messages.ListBySession("local").Count;

            var zip = TestArchives.WriteV3(
                Path.Combine(services.DataDirectory, "truncated.zip"),
                sessionId: "s1",
                title: "损坏的备份");
            var preflight = await vm.PrepareImportAsync(zip);
            Assert.NotNull(preflight);

            // 预检成功后破坏 ZIP（去掉 EOCD），让真正导入进入异常路径；本地库已有数据。
            var bytes = await File.ReadAllBytesAsync(zip, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(zip, bytes[..^22], TestContext.Current.CancellationToken);
            await vm.ConfirmImportCommand.ExecuteAsync(null);
            HeadlessSession.Pump();

            Assert.False(vm.IsArchiveBusy);
            Assert.Equal("导入失败", vm.ArchiveStatusText);
            Assert.StartsWith("导入失败：", vm.ErrorText);
            Assert.Equal(sessionsBefore, services.Sessions.List().Count);
            Assert.Equal(messagesBefore, services.Messages.ListBySession("local").Count);
            Assert.Empty(services.Messages.ListBySession("s1"));
            var panel = window.FindControl<Border>("ImportPanel");
            Assert.NotNull(panel);
            Assert.False(panel.IsVisible);
        });

    [Fact]
    public Task Export_AllWritesV3ArchiveAndShowsSummary() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, _) =>
        {
            services.Sessions.Upsert(new Session { Id = "local", Title = "导出会话", CurrentMessageId = 1 });
            services.Messages.Insert("local", new MessageNode { Id = 1, ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("hello")] });
            vm.RefreshSessions("local");

            var zip = Path.Combine(services.DataDirectory, "all.zip");
            await vm.ExportAllAsync(zip);
            HeadlessSession.Pump();

            Assert.True(File.Exists(zip));
            Assert.False(vm.IsArchiveBusy);
            Assert.True(vm.HasArchiveSummary);
            Assert.Contains("导出完成：1 个会话、1 条消息", vm.ArchiveSummaryText);
            Assert.Equal("已导出 1 个会话", vm.StatusText);
            using var reader = ChatDemoZipReader.Open(zip);
            Assert.NotNull(reader.Manifest);
            Assert.Single(reader.Sessions);
            Assert.Equal("local", reader.Sessions[0].InferredSessionId);
        });

    [Fact]
    public Task Export_CurrentWithoutSelectionReportsReadableError() =>
        MainWindowHeadlessTests.RunWindowAsync(async (services, vm, _) =>
        {
            Assert.Null(vm.SelectedSession);
            await vm.ExportCurrentAsync(Path.Combine(services.DataDirectory, "current.zip"));
            Assert.Equal("没有选中的会话可导出。", vm.ErrorText);
            Assert.Equal("导入/导出失败", vm.StatusText);
        });
}