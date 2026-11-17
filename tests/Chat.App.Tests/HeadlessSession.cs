using Avalonia.Headless;
using Avalonia.Threading;

namespace Chat.App.Tests;

/// <summary>
/// 所有 UI 冒烟测试都在 Avalonia headless 会话里跑：没有 X server/DISPLAY 也能创建窗口、
/// 执行 XAML、跑布局与绑定。这里直接驱动 <see cref="HeadlessUnitTestSession"/>，把测试主体
/// 调度到真实的 UI 线程上——不引入 Avalonia.Headless.XUnit 对 xunit 扩展点的版本耦合，
/// 同时能显式控制 PerTest 隔离级别。
/// </summary>
internal static class HeadlessSession
{
    // PerTest：每个测试拿一份全新的 Application + Dispatcher，测试之间不会互相污染。
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(
        () => HeadlessUnitTestSession.StartNew(typeof(App), AvaloniaTestIsolationLevel.PerTest),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action body) => Shared.Value.Dispatch(body, default);

    public static Task RunAsync(Func<Task> body) =>
        Shared.Value.Dispatch(async () =>
        {
            await body().ConfigureAwait(true);
            return true;
        }, default);

    /// <summary>把排队中的布局/绑定/模板任务跑完，等价于推进一帧。</summary>
    public static void Pump() => Dispatcher.UIThread.RunJobs();
}
