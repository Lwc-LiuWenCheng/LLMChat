using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Chat.App.Services;
using Chat.App.ViewModels;

namespace Chat.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.Messages.CollectionChanged -= OnMessagesChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.Clipboard ??= TryCreateClipboard();
    }

    private IClipboardService? TryCreateClipboard()
    {
        try
        {
            return new AvaloniaClipboardService(this);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || !_vm.CanEditComposer) return;
        // IME 组合输入：Avalonia 12 没有公开的 composition 标志，平台会把组合期间的按键
        // 标记成 Key.ImeProcessed（如 IME 提交键），这种按键不能触发发送。
        if (e.Key == Key.ImeProcessed) return;
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focus is not TextBox { Name: "Composer" }) return;

        _vm.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e) =>
        _vm?.BeginRenameCommand.Execute(null);

    private async void OnImportArchiveClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 chat-demo 导出的 ZIP 备份",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ZIP 备份") { Patterns = ["*.zip"] }],
            });
            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _vm.ReportArchiveError("无法读取所选备份文件的本地路径。");
                return;
            }

            await _vm.PrepareImportAsync(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _vm.ReportArchiveError("打开文件选择器失败：" + exception.Message);
        }
    }

    private async void OnExportCurrentClicked(object? sender, RoutedEventArgs e) =>
        await PickAndExportAsync(exportCurrent: true);

    private async void OnExportAllClicked(object? sender, RoutedEventArgs e) =>
        await PickAndExportAsync(exportCurrent: false);

    private async Task PickAndExportAsync(bool exportCurrent)
    {
        if (_vm is null) return;
        try
        {
            var suggested = exportCurrent && _vm.SelectedSession is { } session
                ? SafeArchiveName(session.DisplayTitle) + ".zip"
                : "llmchat-export.zip";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = exportCurrent ? "导出当前会话为 v3 ZIP" : "导出全部会话为 v3 ZIP",
                SuggestedFileName = suggested,
                DefaultExtension = "zip",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("ZIP 备份") { Patterns = ["*.zip"] }],
            });
            if (file is null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _vm.ReportArchiveError("无法读取所选导出目标的本地路径。");
                return;
            }

            if (exportCurrent) await _vm.ExportCurrentAsync(path);
            else await _vm.ExportAllAsync(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _vm.ReportArchiveError("打开文件选择器失败：" + exception.Message);
        }
    }

    private static string SafeArchiveName(string? title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((title ?? "").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "session";
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private void ScrollToEnd()
    {
        var scroll = this.FindControl<ScrollViewer>("MessageScroll");
        if (scroll is null) return;
        Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
