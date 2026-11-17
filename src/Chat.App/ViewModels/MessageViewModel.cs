using Avalonia.Threading;
using Chat.App.Rendering;
using Chat.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chat.App.ViewModels;

/// <summary>
/// 一条消息的展示模型。流式期间只改 Text/Thinking，Markdown 重渲染按 <see cref="RenderThrottle"/>
/// 合并（首个增量立即渲染，之后每个节流窗口最多一次），避免每 token 全量重建 Inlines。
/// </summary>
public sealed partial class MessageViewModel : ObservableObject
{
    private DispatcherTimer? _renderTimer;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _thinking = "";
    [ObservableProperty] private MessageStatus _status = MessageStatus.Finished;
    [ObservableProperty] private bool _isThinkingExpanded;
    [ObservableProperty] private IReadOnlyList<MarkdownBlock> _blocks = [];

    public MessageViewModel(MessageNode node)
    {
        Id = node.Id;
        ParentId = node.ParentId;
        Role = node.Role;
        ProviderId = node.ProviderId;
        Model = node.Model;
        CreatedAt = node.CreatedAt;
        _status = node.Status;
        _text = node.Parts.FirstOrDefault(p => p.Kind == PartKind.Text)?.Text ?? "";
        _thinking = node.Parts.FirstOrDefault(p => p.Kind == PartKind.Thinking)?.Text ?? "";
        if (IsAssistant) FlushRender();
    }

    public long Id { get; }

    public long? ParentId { get; }

    public MessageRole Role { get; }

    public string? ProviderId { get; }

    public string? Model { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool IsUser => Role == MessageRole.User;

    public bool IsAssistant => Role == MessageRole.Assistant;

    public string RoleText => IsUser ? "你" : "助手";

    public bool HasThinking => !string.IsNullOrWhiteSpace(Thinking);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public bool IsStreaming => Status == MessageStatus.Wip;

    public bool ShowStatus => Status != MessageStatus.Finished;

    public bool CanRetry => IsAssistant &&
        Status is MessageStatus.Failed or MessageStatus.ContentFilter or MessageStatus.Incomplete
            or MessageStatus.Timeout or MessageStatus.ContextLengthExceeded;

    public string ThinkingHeader => IsStreaming ? "思考中…" : "思考过程";

    public bool HasBlocks => Blocks.Count > 0;

    /// <summary>已完成的 Markdown 重渲染次数。测试用它验证节流而不是“每个 token 都重建”。</summary>
    public int RenderCount { get; private set; }

    /// <summary>重渲染节流窗口。测试可调大以避免 DispatcherTimer 在测试期间触发。</summary>
    public TimeSpan RenderThrottle { get; set; } = TimeSpan.FromMilliseconds(50);

    public string StatusText => Status switch
    {
        MessageStatus.Wip => "生成中…",
        MessageStatus.Finished => "",
        MessageStatus.Incomplete => "已停止",
        MessageStatus.ContentFilter => "内容被拒绝",
        MessageStatus.Failed => "出错了",
        MessageStatus.Timeout => "超时",
        MessageStatus.ContextLengthExceeded => "上下文超长",
        _ => Status.ToString(),
    };

    public MessageNode ToNode(long? parentId) => new()
    {
        Id = Id,
        ParentId = parentId,
        Role = Role,
        Status = Status,
        ProviderId = ProviderId,
        Model = Model,
        Parts = BuildParts(),
        CreatedAt = CreatedAt,
    };

    public IReadOnlyList<ContentPart> BuildParts()
    {
        var parts = new List<ContentPart>();
        if (!string.IsNullOrEmpty(Thinking)) parts.Add(ContentPart.OfThinking(Thinking));
        if (!string.IsNullOrEmpty(Text)) parts.Add(ContentPart.OfText(Text));
        return parts;
    }

    /// <summary>立即把当前 Text 解析成 Markdown 块（流结束时兜底调用，保证不丢最后一段）。</summary>
    public void FlushRender()
    {
        if (!IsAssistant) return;
        _renderTimer?.Stop();
        RenderCount++;
        Blocks = MarkdownRenderer.Render(Text);
    }

    private void ScheduleRender()
    {
        if (!IsAssistant) return;
        if (RenderCount == 0)
        {
            FlushRender();
            return;
        }

        if (_renderTimer is null)
        {
            _renderTimer = new DispatcherTimer { Interval = RenderThrottle };
            _renderTimer.Tick += (_, _) =>
            {
                _renderTimer?.Stop();
                FlushRender();
            };
        }

        _renderTimer.Interval = RenderThrottle;
        if (!_renderTimer.IsEnabled) _renderTimer.Start();
    }

    partial void OnThinkingChanged(string value)
    {
        OnPropertyChanged(nameof(HasThinking));
    }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasText));
        ScheduleRender();
    }

    partial void OnStatusChanged(MessageStatus value)
    {
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ThinkingHeader));
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(CanRetry));
        if (IsAssistant) FlushRender();
    }

    partial void OnBlocksChanged(IReadOnlyList<MarkdownBlock> value) => OnPropertyChanged(nameof(HasBlocks));
}
