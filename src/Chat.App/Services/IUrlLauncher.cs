using System.Diagnostics;

namespace Chat.App.Services;

/// <summary>
/// 打开系统浏览器的最小抽象：headless 测试注入假实现，绝不真开浏览器。
/// </summary>
public interface IUrlLauncher
{
    void Launch(Uri uri);
}

/// <summary>
/// 生产实现：shell execute 打开系统默认浏览器。URL 作为 ProcessStartInfo.FileName
/// 单独传入（不使用 Arguments / 不拼命令行），避免 URL 内容被 shell 解释。
/// </summary>
public sealed class SystemUrlLauncher : IUrlLauncher
{
    public void Launch(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var startInfo = new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        };
        _ = Process.Start(startInfo);
    }
}
