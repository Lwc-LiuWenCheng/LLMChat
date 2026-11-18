using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Chat.App.ViewModels;

namespace Chat.App.Views;

/// <summary>对话区：承载消息滚动列表与贴底输入框，消息变化时自动滚到底部。</summary>
public partial class ConversationView : UserControl
{
    private MainWindowViewModel? _vm;

    public ConversationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.Messages.CollectionChanged -= OnMessagesChanged;

        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var scroll = this.FindControl<ScrollViewer>("MessageScroll");
        if (scroll is null) return;
        Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
