using System.Collections.ObjectModel;
using Avalonia.Threading;
using Chat.Abstractions;
using Chat.App.Services;
using Chat.Core;
using Chat.Storage.ChatDemo;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chat.App.ViewModels;

/// <summary>
/// 主窗口视图模型。负责"会话树 + provider 流"的编排，所有持久化都走 Chat.Storage 仓储，
/// 请求组装走 Chat.Core.PromptAssembler，provider 调用走 Chat.Abstractions 契约。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _suppressReload;
    private DispatcherTimer? _searchTimer;
    private CancellationTokenSource? _archiveCts;
    private ArchivePreflight? _pendingImport;

    [ObservableProperty] private Session? _selectedSession;
    [ObservableProperty] private ProviderDescriptor? _selectedProvider;
    [ObservableProperty] private string _model = "deepseek-flash";
    [ObservableProperty] private string _composer = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _apiKeyInput = "";
    partial void OnApiKeyInputChanged(string value) => OnPropertyChanged(nameof(CanVerifyAccount));
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private string _branchText = "";
    [ObservableProperty] private bool _canNavigateBranch;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _isDeleteConfirmVisible;
    [ObservableProperty] private MessageViewModel? _editingMessage;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isArchiveConfirmVisible;
    [ObservableProperty] private bool _isArchiveBusy;
    [ObservableProperty] private string _archiveConfirmationText = "";
    [ObservableProperty] private string _archiveStatusText = "";
    [ObservableProperty] private string _archiveSummaryText = "";
    [ObservableProperty] private string _wizardStatusText = "";
    [ObservableProperty] private bool _isAccountBusy;
    [ObservableProperty] private bool _isAccountAvailable;
    [ObservableProperty] private string _balanceText = "";

    public MainWindowViewModel(AppServices services, IEnumerable<IChatProvider>? providers = null,
        IClipboardService? clipboard = null, IDeepSeekAccountService? accountService = null)
    {
        _services = services;
        Clipboard = clipboard;
        AccountService = accountService ?? services.DeepSeekAccount;
        Sessions = new ObservableCollection<Session>();
        Messages = new ObservableCollection<MessageViewModel>();
        Providers = new ObservableCollection<ProviderDescriptor>();
        Models = new ObservableCollection<string>();

        foreach (var provider in providers ?? services.Plugins.Select(plugin => plugin.Provider))
        {
            _providers[provider.Descriptor.Id] = provider;
            Providers.Add(provider.Descriptor);
        }

        SelectedProvider = Providers.FirstOrDefault();
        ApiKeyInput = services.Settings.Get(SecretKey) ?? "";
        StatusText = BuildBootStatus();

        RefreshSessions();
        if (Sessions.Count > 0) SelectedSession = Sessions[0];
    }

    /// <summary>凭据设置名与账户向导服务共用同一常量，绝不重复定义。</summary>
    public const string SecretKey = DeepSeekAccountService.SecretKey;

    /// <summary>账户向导服务；生产由 AppServices 提供，headless 测试可替换。</summary>
    public IDeepSeekAccountService AccountService { get; }

    public ObservableCollection<Session> Sessions { get; }

    public ObservableCollection<MessageViewModel> Messages { get; }

    public ObservableCollection<ProviderDescriptor> Providers { get; }

    public ObservableCollection<string> Models { get; }

    /// <summary>窗口打开后注入真实剪贴板；测试可注入假实现。</summary>
    public IClipboardService? Clipboard { get; set; }

    public string? LastCopiedText { get; private set; }

    /// <summary>搜索防抖窗口。测试设为 <see cref="TimeSpan.Zero"/> 可同步过滤。</summary>
    public TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(200);

    public bool HasPlugins => Providers.Count > 0;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool ShowEmptyState => SelectedSession is null;

    public bool ShowNoPlugins => !HasPlugins;

    public bool IsPinned => SelectedSession?.Pinned == true;

    public bool CanEditComposer => !IsStreaming;

    public bool ShowSend => !IsStreaming;

    public bool ShowStop => IsStreaming;

    /// <summary>导入确认/进度/摘要共用左侧面板；确认框关闭后摘要仍保留。</summary>
    public bool IsArchivePanelVisible => IsArchiveConfirmVisible || IsArchiveBusy || HasArchiveSummary;

    public bool HasArchiveSummary => !string.IsNullOrWhiteSpace(ArchiveSummaryText);

    /// <summary>当前等待确认或正在处理的 ZIP 路径；成功后清空。</summary>
    public string? ArchivePath { get; private set; }

    private string BuildBootStatus()
    {
        var text = $"插件 {Providers.Count} 个已加载";
        if (_services.Failures.Count > 0) text += $"，{_services.Failures.Count} 个失败";
        return text;
    }

    partial void OnSelectedSessionChanged(Session? value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsPinned));
        if (value is null) return;

        if (!_suppressReload)
        {
            IsRenaming = false;
            IsDeleteConfirmVisible = false;
            CancelEditInternal();
        }

        if (_suppressReload) return;
        LoadMessages(value);
        if (value.ProviderId is { Length: > 0 } id && _providers.TryGetValue(id, out var provider))
            SelectedProvider = Providers.FirstOrDefault(p => p.Id == provider.Descriptor.Id);
        if (!string.IsNullOrWhiteSpace(value.Model)) Model = value.Model!;
    }

    partial void OnSelectedProviderChanged(ProviderDescriptor? value)
    {
        if (value?.DefaultModel is { Length: > 0 } m) Model = m;
        _ = LoadModelsAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (SearchDebounce <= TimeSpan.Zero)
        {
            RefreshSessions();
            return;
        }

        if (_searchTimer is null)
        {
            _searchTimer = new DispatcherTimer { Interval = SearchDebounce };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer?.Stop();
                RefreshSessions();
            };
        }

        _searchTimer.Interval = SearchDebounce;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditComposer));
        OnPropertyChanged(nameof(ShowSend));
        OnPropertyChanged(nameof(ShowStop));
    }

    partial void OnIsArchiveConfirmVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsArchivePanelVisible));

    partial void OnIsArchiveBusyChanged(bool value) =>
        OnPropertyChanged(nameof(IsArchivePanelVisible));

    partial void OnArchiveSummaryTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasArchiveSummary));
        OnPropertyChanged(nameof(IsArchivePanelVisible));
    }

    private void LoadMessages(Session session)
    {
        var nodes = _services.Messages.ListBySession(session.Id);
        Messages.Clear();
        foreach (var node in SessionTree.PathTo(nodes, session.CurrentMessageId))
            Messages.Add(new MessageViewModel(node));

        UpdateBranch(nodes, session);
    }

    private void ClearMessages()
    {
        Messages.Clear();
        BranchText = "";
        CanNavigateBranch = false;
    }

    private void UpdateBranch(IReadOnlyList<MessageNode> nodes, Session session)
    {
        var branchNode = BranchNavigator.FindBranchNode(nodes, session.CurrentMessageId);
        if (branchNode is null)
        {
            BranchText = "";
            CanNavigateBranch = false;
            return;
        }

        var (index, count) = SessionTree.BranchPosition(nodes, branchNode);
        BranchText = count > 1 ? $"{index}/{count}" : "";
        CanNavigateBranch = count > 1;
    }

    /// <summary>重新读取会话列表；selectId 用于导入/重命名后定位会话。</summary>
    public void RefreshSessions(string? selectId = null)
    {
        var current = selectId ?? SelectedSession?.Id;
        var list = _services.Sessions.List(search: SearchText);
        Sessions.Clear();
        foreach (var session in list) Sessions.Add(session);

        if (current is { Length: > 0 })
        {
            var match = Sessions.FirstOrDefault(s => s.Id == current) ?? Sessions.FirstOrDefault();
            _suppressReload = true;
            SelectedSession = match;
            _suppressReload = false;
            if (match is not null) LoadMessages(match);
            else ClearMessages();
        }
        else if (Sessions.Count > 0 && SelectedSession is null)
        {
            SelectedSession = Sessions[0];
        }
        else if (Sessions.Count == 0)
        {
            ClearMessages();
            _suppressReload = true;
            SelectedSession = null;
            _suppressReload = false;
        }

        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsPinned));
    }

    private IChatProvider? CurrentProvider()
    {
        if (SelectedProvider is { } descriptor && _providers.TryGetValue(descriptor.Id, out var provider))
            return provider;
        return _providers.Values.FirstOrDefault();
    }

    private Session EnsureSession()
    {
        if (SelectedSession is { } existing) return existing;

        var session = new Session
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "",
            ProviderId = SelectedProvider?.Id,
            Model = Model,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _services.Sessions.Upsert(session);
        Sessions.Insert(0, session);
        _suppressReload = true;
        SelectedSession = session;
        _suppressReload = false;
        return session;
    }

    [RelayCommand]
    private void NewSession()
    {
        var session = new Session
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "",
            ProviderId = SelectedProvider?.Id,
            Model = Model,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _services.Sessions.Upsert(session);
        Sessions.Insert(0, session);
        _suppressReload = true;
        SelectedSession = session;
        _suppressReload = false;
        Messages.Clear();
        BranchText = "";
        ErrorText = "";
        IsEditing = false;
        EditingMessage = null;
        IsRenaming = false;
        IsDeleteConfirmVisible = false;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsStreaming) return;
        var text = Composer.Trim();
        if (text.Length == 0) return;

        var provider = CurrentProvider();
        if (provider is null)
        {
            ErrorText = "没有可用的模型插件。";
            return;
        }

        var session = EnsureSession();
        var now = DateTimeOffset.UtcNow;
        var parent = IsEditing && EditingMessage is { } editing ? editing.ParentId : session.CurrentMessageId;
        var userId = _services.Messages.NextId(session.Id);
        var user = new MessageNode
        {
            Id = userId,
            ParentId = parent,
            Role = MessageRole.User,
            Status = MessageStatus.Finished,
            Parts = [ContentPart.OfText(text)],
            CreatedAt = now,
        };
        _services.Messages.Insert(session.Id, user);
        Messages.Add(new MessageViewModel(user));
        Composer = "";
        ErrorText = "";
        IsEditing = false;
        EditingMessage = null;

        var title = string.IsNullOrWhiteSpace(session.Title) ? SessionTree.DeriveTitle([user], userId) : session.Title;
        session = session with
        {
            Title = title,
            CurrentMessageId = userId,
            ProviderId = provider.Descriptor.Id,
            Model = Model,
            UpdatedAt = now,
        };
        _services.Sessions.Upsert(session);
        _suppressReload = true;
        SelectedSession = session;
        _suppressReload = false;

        await RunAssistantAsync(session.Id, userId, provider).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private async Task RegenerateAsync()
    {
        if (IsStreaming || SelectedSession is null) return;

        var session = SelectedSession;
        var nodes = _services.Messages.ListBySession(session.Id);
        var path = SessionTree.PathTo(nodes, session.CurrentMessageId);
        if (path.Count == 0) return;

        var parent = SessionTree.RegenerateParent(path);
        var provider = CurrentProvider();
        if (provider is null) return;

        ErrorText = "";
        await RunAssistantAsync(session.Id, parent, provider).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RegenerateMessageAsync(MessageViewModel? message)
    {
        if (IsStreaming || SelectedSession is not { } session || message is null || !message.IsAssistant) return;

        var provider = CurrentProvider();
        if (provider is null) return;

        ErrorText = "";
        await RunAssistantAsync(session.Id, message.ParentId, provider).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RetryMessageAsync(MessageViewModel? message)
    {
        if (IsStreaming || SelectedSession is not { } session || message is null || !message.IsAssistant) return;

        var provider = CurrentProvider();
        if (provider is null) return;

        ErrorText = "";
        await RunAssistantAsync(session.Id, message.ParentId, provider).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyMessageAsync(MessageViewModel? message)
    {
        if (message is null) return;
        LastCopiedText = message.Text;
        OnPropertyChanged(nameof(LastCopiedText));
        if (Clipboard is not null) await Clipboard.SetTextAsync(message.Text).ConfigureAwait(true);
        StatusText = "已复制";
    }

    [RelayCommand]
    private void BeginEdit(MessageViewModel? message)
    {
        if (IsStreaming || message is null || !message.IsUser) return;
        EditingMessage = message;
        IsEditing = true;
        Composer = message.Text;
        StatusText = "正在编辑消息：发送后创建新分支";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        CancelEditInternal();
        Composer = "";
        StatusText = BuildBootStatus();
    }

    private void CancelEditInternal()
    {
        IsEditing = false;
        EditingMessage = null;
    }

    [RelayCommand]
    private void BeginRename()
    {
        if (SelectedSession is not { } session) return;
        RenameText = session.Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (SelectedSession is not { } session) return;

        var updated = session with
        {
            Title = RenameText.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _services.Sessions.Upsert(updated);
        IsRenaming = false;
        RefreshSessions(updated.Id);
        StatusText = "已重命名";
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameText = "";
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (SelectedSession is not { } session) return;

        var updated = session with
        {
            Pinned = !session.Pinned,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _services.Sessions.Upsert(updated);
        RefreshSessions(updated.Id);
        StatusText = updated.Pinned ? "已置顶" : "已取消置顶";
    }

    [RelayCommand]
    private void RequestDeleteSession()
    {
        if (SelectedSession is not null) IsDeleteConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelDeleteSession() => IsDeleteConfirmVisible = false;

    [RelayCommand]
    private void ConfirmDeleteSession()
    {
        IsDeleteConfirmVisible = false;
        if (SelectedSession is not { } session) return;

        _services.Sessions.SoftDelete(session.Id);
        StatusText = "已删除会话（可恢复）";
        _suppressReload = true;
        SelectedSession = null;
        _suppressReload = false;
        RefreshSessions();
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorText = "";
        StatusText = BuildBootStatus();
    }

    /// <summary>文件选择器不可用或导出无当前会话时，给 UI 一个可读错误。</summary>
    public void ReportArchiveError(string message)
    {
        _pendingImport = null;
        ArchivePath = null;
        IsArchiveConfirmVisible = false;
        ArchiveStatusText = "操作未开始";
        ErrorText = message;
        StatusText = "导入/导出失败";
    }

    /// <summary>
    /// 预检 ZIP：只读取目录结构与 manifest（不解压落盘），统计将合并的会话/附件数，
    /// 然后由界面显示确认框。数据目录与 Storage 共用，预览阶段不会写库。
    /// </summary>
    public async Task<ArchivePreflight?> PrepareImportAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        if (IsArchiveBusy) return null;
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            ReportArchiveError("未选择要导入的 ZIP 文件。");
            return null;
        }

        ErrorText = "";
        _pendingImport = null;
        ArchivePath = null;
        ArchiveSummaryText = "";
        ArchiveStatusText = "正在读取备份…";
        ArchiveConfirmationText = "";
        IsArchiveConfirmVisible = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _archiveCts = cts;
        IsArchiveBusy = true;
        try
        {
            var preflight = await ArchiveWorkflow.PreflightAsync(zipPath, cts.Token).ConfigureAwait(true);
            _pendingImport = preflight;
            ArchivePath = preflight.ZipPath;
            ArchiveConfirmationText = preflight.ConfirmationText;
            ArchiveStatusText = preflight.Warnings.Count > 0
                ? $"备份已读取，有 {preflight.Warnings.Count} 条警告"
                : "备份已读取，等待确认";
            IsArchiveConfirmVisible = true;
            StatusText = $"准备导入 {preflight.Sessions} 个会话";
            return preflight;
        }
        catch (OperationCanceledException)
        {
            ArchiveStatusText = "读取已取消";
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ArchiveStatusText = "读取失败";
            ErrorText = "无法读取备份文件：" + exception.Message;
            return null;
        }
        finally
        {
            if (ReferenceEquals(_archiveCts, cts)) _archiveCts = null;
            IsArchiveBusy = false;
            OnPropertyChanged(nameof(IsArchivePanelVisible));
        }
    }

    /// <summary>确认后后台导入；Storage 在写入前自动快照，失败/取消不会清空原库。</summary>
    [RelayCommand]
    private async Task ConfirmImportAsync()
    {
        if (IsArchiveBusy || !IsArchiveConfirmVisible || _pendingImport is not { } preflight) return;

        IsArchiveConfirmVisible = false;
        IsArchiveBusy = true;
        ArchiveSummaryText = "";
        ArchiveStatusText = "正在导入…";
        ErrorText = "";

        using var cts = new CancellationTokenSource();
        _archiveCts = cts;
        var progress = new Progress<ImportReport>(UpdateArchiveImportProgress);
        try
        {
            var report = await ArchiveWorkflow.ImportAsync(
                preflight.ZipPath, _services.DataDirectory, progress, cts.Token).ConfigureAwait(true);

            _pendingImport = null;
            ArchivePath = null;
            ArchiveSummaryText = ArchiveWorkflow.DescribeImport(report);
            ArchiveStatusText = report.Warnings.Count > 0
                ? $"导入完成，{report.Warnings.Count} 条警告"
                : "导入完成";
            StatusText = $"已导入 {report.Added + report.Updated} 个会话";
            ErrorText = "";

            // 清掉搜索过滤，确保导入的会话一定能出现在列表里；选中 ZIP 里的第一个。
            if (!string.IsNullOrWhiteSpace(SearchText)) SearchText = "";
            var first = preflight.SessionIds.FirstOrDefault(id => _services.Sessions.Get(id) is not null);
            RefreshSessions(first);
        }
        catch (OperationCanceledException)
        {
            ArchiveStatusText = "导入已取消";
            ErrorText = "导入已取消；数据库未被清空，导入前快照仍可用于恢复。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ArchiveStatusText = "导入失败";
            ErrorText = "导入失败：" + exception.Message;
        }
        finally
        {
            _archiveCts = null;
            IsArchiveBusy = false;
            OnPropertyChanged(nameof(IsArchivePanelVisible));
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        if (IsArchiveBusy)
        {
            _archiveCts?.Cancel();
            return;
        }

        _pendingImport = null;
        ArchivePath = null;
        IsArchiveConfirmVisible = false;
        ArchiveConfirmationText = "";
        ArchiveStatusText = "已取消导入";
        StatusText = BuildBootStatus();
    }

    [RelayCommand]
    private void CancelArchiveOperation() => _archiveCts?.Cancel();

    [RelayCommand]
    private void ClearArchiveSummary()
    {
        if (IsArchiveBusy) return;
        ArchiveSummaryText = "";
        ArchiveStatusText = "";
        StatusText = BuildBootStatus();
    }

    /// <summary>导出当前会话；由文件选择器拿到路径后调用。</summary>
    public async Task ExportCurrentAsync(string zipPath)
    {
        if (SelectedSession is not { } session)
        {
            ReportArchiveError("没有选中的会话可导出。");
            return;
        }

        await ExportArchiveAsync(zipPath, [session.Id]).ConfigureAwait(true);
    }

    /// <summary>导出全部未删除会话。</summary>
    public Task ExportAllAsync(string zipPath) => ExportArchiveAsync(zipPath, null);

    private async Task ExportArchiveAsync(string zipPath, IReadOnlyCollection<string>? sessionIds)
    {
        if (IsArchiveBusy) return;
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            ReportArchiveError("未选择导出文件路径。");
            return;
        }

        _pendingImport = null;
        ArchivePath = null;
        IsArchiveConfirmVisible = false;
        IsArchiveBusy = true;
        ArchiveSummaryText = "";
        ArchiveStatusText = "正在导出…";
        ErrorText = "";

        using var cts = new CancellationTokenSource();
        _archiveCts = cts;
        try
        {
            var report = sessionIds is null
                ? await ArchiveWorkflow.ExportAllAsync(
                    zipPath, _services.DataDirectory, null, cts.Token).ConfigureAwait(true)
                : await ArchiveWorkflow.ExportSessionsAsync(
                    zipPath, _services.DataDirectory, sessionIds, null, cts.Token).ConfigureAwait(true);

            ArchiveSummaryText = ArchiveWorkflow.DescribeExport(report);
            ArchiveStatusText = report.Warnings.Count > 0
                ? $"导出完成，{report.Warnings.Count} 条警告"
                : "导出完成";
            StatusText = $"已导出 {report.Sessions} 个会话";
        }
        catch (OperationCanceledException)
        {
            ArchiveStatusText = "导出已取消";
            ErrorText = "导出已取消，未完成的目标文件已清理。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ArchiveStatusText = "导出失败";
            ErrorText = "导出失败：" + exception.Message;
        }
        finally
        {
            _archiveCts = null;
            IsArchiveBusy = false;
            OnPropertyChanged(nameof(IsArchivePanelVisible));
        }
    }

    private void UpdateArchiveImportProgress(ImportReport report)
    {
        var text = $"正在导入：已处理 {report.Added + report.Updated + report.Skipped} 个会话…";
        if (Dispatcher.UIThread.CheckAccess()) SetIfBusy();
        else Dispatcher.UIThread.Post(SetIfBusy);

        void SetIfBusy()
        {
            if (IsArchiveBusy) ArchiveStatusText = text;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _services.Settings.Set(SecretKey, ApiKeyInput.Trim());
        StatusText = string.IsNullOrWhiteSpace(ApiKeyInput) ? "已清除 API Key" : "已保存 API Key";
        IsSettingsOpen = false;
        _ = LoadModelsAsync();
    }

    [RelayCommand]
    private void OpenApiKeysPage() => AccountService.OpenApiKeysPage();

    [RelayCommand]
    private void OpenTopUpPage() => AccountService.OpenTopUpPage();

    [RelayCommand]
    private async Task VerifyAndSaveAccountAsync()
    {
        if (IsAccountBusy) return;
        var key = ApiKeyInput.Trim();
        IsAccountBusy = true;
        WizardStatusText = "正在验证 API Key 与余额…";
        OnPropertyChanged(nameof(CanVerifyAccount));
        try
        {
            var result = await AccountService.VerifyAndSaveAsync(key).ConfigureAwait(true);
            WizardStatusText = result.Message;
            if (result.Saved)
            {
                ApiKeyInput = key;
                IsAccountAvailable = result.Status == DeepSeekAccountStatus.Verified;
                BalanceText = IsAccountAvailable
                    ? string.Join(Environment.NewLine,
                        result.Balances.Select(b => $"{b.Currency}｜总额 {b.TotalBalance}｜赠金 {b.GrantedBalance}｜充值 {b.ToppedUpBalance}"))
                    : "";
                StatusText = IsAccountAvailable ? "DeepSeek 账户已验证" : "DeepSeek 账户余额不可用";
                if (IsAccountAvailable) _ = LoadModelsAsync();
            }
            else
            {
                IsAccountAvailable = false;
                BalanceText = "";
                ErrorText = result.Message;
            }
        }
        catch (Exception)
        {
            // 兜底：UI 绝不显示完整 key，也不让向导把主窗口带崩。
            WizardStatusText = "验证失败，请稍后重试。";
            IsAccountAvailable = false;
            BalanceText = "";
            ErrorText = WizardStatusText;
        }
        finally
        {
            IsAccountBusy = false;
            OnPropertyChanged(nameof(CanVerifyAccount));
        }
    }

    public bool CanVerifyAccount => !IsAccountBusy && !string.IsNullOrWhiteSpace(ApiKeyInput);

    [RelayCommand]
    private void PrevBranch() => MoveBranch(-1);

    [RelayCommand]
    private void NextBranch() => MoveBranch(1);

    private void MoveBranch(int delta)
    {
        if (SelectedSession is not { } session) return;
        var nodes = _services.Messages.ListBySession(session.Id);
        var branchNode = BranchNavigator.FindBranchNode(nodes, session.CurrentMessageId);
        if (branchNode is null) return;

        var siblings = SessionTree.ChildrenOf(nodes, branchNode.ParentId);
        if (siblings.Count < 2) return;
        var index = siblings.ToList().FindIndex(node => node.Id == branchNode.Id);
        if (index < 0) return;

        var next = siblings[(index + delta + siblings.Count) % siblings.Count];
        var target = BranchNavigator.DescendToLatest(nodes, next);
        var updated = session with { CurrentMessageId = target, UpdatedAt = DateTimeOffset.UtcNow };
        _services.Sessions.Upsert(updated);

        // 必须整体刷新会话列表：ListBox 的 SelectedItem 是双向绑定，如果直接把
        // 一个不在 Sessions 里的 Session 实例赋给 SelectedSession，控件同步时会把
        // 它回写成 null，随后的分支切换就失去当前会话了。
        RefreshSessions(session.Id);
    }

    private async Task LoadModelsAsync()
    {
        var provider = CurrentProvider();
        if (provider is null) return;

        try
        {
            var sessionId = SelectedSession?.Id ?? "settings";
            var context = _services.CreateContext(sessionId);
            var models = await provider.ListModelsAsync(context).ConfigureAwait(true);
            Models.Clear();
            foreach (var model in models) Models.Add(model.Id);
            if (Models.Count > 0 && !Models.Contains(Model)) Model = Models[0];
        }
        catch (Exception ex)
        {
            _services.CreateContext(SelectedSession?.Id ?? "settings")
                .Log(ProviderLogLevel.Warning, "加载模型列表失败：" + ex.Message);
        }
    }

    private async Task RunAssistantAsync(string sessionId, long? parentId, IChatProvider provider)
    {
        var session = _services.Sessions.Get(sessionId);
        if (session is null) return;

        var nodes = _services.Messages.ListBySession(sessionId).ToList();
        var path = SessionTree.PathTo(nodes, parentId);
        var request = PromptAssembler.Build(
            session,
            path,
            attachmentReader: _services.CreateAttachmentReader(),
            modelOverride: string.IsNullOrWhiteSpace(Model) ? null : Model,
            extra: provider.Descriptor.Capabilities.HasFlag(ProviderCapabilities.Thinking)
                ? new Dictionary<string, string> { ["thinking"] = "enabled" }
                : null);

        var now = DateTimeOffset.UtcNow;
        var assistantId = _services.Messages.NextId(sessionId);
        var assistant = new MessageNode
        {
            Id = assistantId,
            ParentId = parentId,
            Role = MessageRole.Assistant,
            Status = MessageStatus.Wip,
            ProviderId = provider.Descriptor.Id,
            Model = request.Model,
            CreatedAt = now,
        };
        _services.Messages.Insert(sessionId, assistant);

        session = session with { CurrentMessageId = assistantId, UpdatedAt = now };
        _services.Sessions.Upsert(session);
        _suppressReload = true;
        SelectedSession = session;
        _suppressReload = false;

        var vm = new MessageViewModel(assistant) { Status = MessageStatus.Wip };
        Messages.Add(vm);
        UpdateBranch(_services.Messages.ListBySession(sessionId), session);

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsStreaming = true;
        StatusText = "生成中…";
        var context = _services.CreateContext(sessionId);

        try
        {
            await foreach (var evt in provider.StreamAsync(request, context, cts.Token).ConfigureAwait(true))
            {
                switch (evt)
                {
                    case ChatEvent.TextDelta delta:
                        vm.Text += delta.Text;
                        break;
                    case ChatEvent.ThinkingDelta thinking:
                        vm.Thinking += thinking.Text;
                        break;
                    case ChatEvent.UsageEvent usage:
                        StatusText = UsageText(usage.Usage);
                        break;
                    case ChatEvent.Failed failed:
                        vm.Status = MessageStatus.Failed;
                        ErrorText = failed.Message;
                        break;
                    case ChatEvent.Done done:
                        vm.Status = MapStatus(done.Reason);
                        break;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            // 只有宿主取消（用户点了“停止”）才是 Incomplete；provider 自己的超时/取消
            // 不是用户意图，必须标成 Timeout 并给出错误，避免被误显示为「已停止」。
            if (cts.Token.IsCancellationRequested)
            {
                vm.Status = MessageStatus.Incomplete;
            }
            else
            {
                vm.Status = MessageStatus.Timeout;
                ErrorText = string.IsNullOrWhiteSpace(ex.Message) ? "请求超时。" : "请求超时：" + ex.Message;
            }
        }
        catch (Exception ex)
        {
            vm.Status = MessageStatus.Failed;
            ErrorText = ex.Message;
        }
        finally
        {
            if (vm.Status == MessageStatus.Wip) vm.Status = MessageStatus.Incomplete;
            IsStreaming = false;
            _cts = null;
            vm.FlushRender();
            StatusText = vm.StatusText is { Length: > 0 } text ? text : BuildBootStatus();

            var finalNode = assistant with
            {
                Status = vm.Status,
                Parts = vm.BuildParts(),
            };
            _services.Messages.Update(sessionId, finalNode);

            var latest = _services.Sessions.Get(sessionId);
            if (latest is not null)
            {
                var updated = latest with { CurrentMessageId = assistantId, UpdatedAt = DateTimeOffset.UtcNow };
                _services.Sessions.Upsert(updated);
                RefreshSessions(sessionId);
            }
        }
    }

    private static MessageStatus MapStatus(StopReason reason) => reason switch
    {
        StopReason.EndTurn or StopReason.StopSequence or StopReason.ToolUse => MessageStatus.Finished,
        StopReason.MaxTokens => MessageStatus.ContextLengthExceeded,
        StopReason.ContentFilter => MessageStatus.ContentFilter,
        StopReason.Cancelled => MessageStatus.Incomplete,
        _ => MessageStatus.Failed,
    };

    private static string UsageText(TokenUsage usage)
    {
        var parts = new List<string>();
        if (usage.InputTokens is { } input) parts.Add($"输入 {input}");
        if (usage.OutputTokens is { } output) parts.Add($"输出 {output}");
        if (usage.CachedInputTokens is { } cached) parts.Add($"缓存 {cached}");
        return parts.Count == 0 ? "生成中…" : "用量 " + string.Join(" / ", parts);
    }
}