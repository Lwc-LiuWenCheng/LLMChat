using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Chat.App.Services;

/// <summary>复制到剪贴板的最小抽象：测试注入假实现，窗口打开后注入真实实现。</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public sealed class AvaloniaClipboardService(TopLevel topLevel) : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        var clipboard = topLevel.Clipboard;
        return clipboard is null ? Task.CompletedTask : clipboard.SetTextAsync(text);
    }
}
