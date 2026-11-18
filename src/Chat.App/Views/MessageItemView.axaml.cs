using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Chat.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Chat.App.Views;

/// <summary>单条消息气泡：区分用户/助手、渲染 Markdown 块并提供操作栏。</summary>
public partial class MessageItemView : UserControl
{
    public MessageItemView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCopyClicked(object? sender, RoutedEventArgs e) => Execute(vm => vm.CopyMessageCommand);

    private void OnEditClicked(object? sender, RoutedEventArgs e) => Execute(vm => vm.BeginEditCommand);

    private void OnRegenerateClicked(object? sender, RoutedEventArgs e) => Execute(vm => vm.RegenerateMessageCommand);

    private void OnRetryClicked(object? sender, RoutedEventArgs e) => Execute(vm => vm.RetryMessageCommand);

    private void Execute(Func<MainWindowViewModel, IRelayCommand<MessageViewModel?>> selector)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel vm) return;
        selector(vm).Execute(DataContext as MessageViewModel);
    }
}
