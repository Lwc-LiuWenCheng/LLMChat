using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Chat.App.ViewModels;

namespace Chat.App.Views;

/// <summary>输入区：多行输入、深度思考/智能搜索开关、附件与发送/停止按钮。</summary>
public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();
        // 用隧道事件先于 TextBox 处理 Enter，避免 AcceptsReturn 把回车当换行吞掉。
        AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanEditComposer) return;
        // IME 组合期间的按键不能触发发送。
        if (e.Key == Key.ImeProcessed) return;
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focus is not TextBox { Name: "ComposerBox" }) return;

        vm.SendCommand.Execute(null);
        e.Handled = true;
    }
}
