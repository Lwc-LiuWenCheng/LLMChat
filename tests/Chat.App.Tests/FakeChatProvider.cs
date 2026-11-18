using System.Runtime.CompilerServices;
using Chat.Abstractions;
using Chat.App.Services;

namespace Chat.App.Tests;

/// <summary>
/// 可编排的假 provider：按入队顺序消费脚本，让 headless 测试能确定性地覆盖
/// 流式渲染、编辑分支、重新生成、停止与失败重试，不依赖网络和真实 API Key。
/// </summary>
internal sealed class FakeChatProvider : IChatProvider
{
    private readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatEvent>>> _scripts = new();

    public ProviderDescriptor Descriptor { get; } = new(
        "test.fake",
        "Fake Provider",
        ProtocolFamily.Custom,
        "1.0.0",
        ProviderCapabilities.Thinking,
        "fake-model",
        "测试用假 provider");

    public List<ChatRequest> Requests { get; } = [];

    /// <summary>测试钩子：每次请求到达时调用，便于断言最终送给 provider 的完整请求。</summary>
    public Action<ChatRequest>? OnStream { get; set; }

    public FakeChatProvider Text(string text)
    {
        _scripts.Enqueue(_ => Emit(new ChatEvent.TextDelta(text), new ChatEvent.Done(StopReason.EndTurn)));
        return this;
    }

    public FakeChatProvider Thinking(string thinking, string text)
    {
        _scripts.Enqueue(_ => Emit(
            new ChatEvent.ThinkingDelta(thinking),
            new ChatEvent.TextDelta(text),
            new ChatEvent.UsageEvent(new TokenUsage(12, 7)),
            new ChatEvent.Done(StopReason.EndTurn)));
        return this;
    }

    public FakeChatProvider Failure(string code, string message)
    {
        _scripts.Enqueue(_ => Emit(new ChatEvent.Failed(code, message)));
        return this;
    }

    /// <summary>
    /// 模拟 provider 自身的超时：抛出不带宿主取消的 <see cref="OperationCanceledException"/>，
    /// 用于区分「用户停止」与「上游超时」。
    /// </summary>
    public FakeChatProvider UpstreamTimeout(string message = "模拟上游超时")
    {
        _scripts.Enqueue(_ => Throw(new OperationCanceledException(message)));
        return this;
    }

    /// <summary>先产出增量，再无限等待直到宿主取消——用于测试「停止」。</summary>
    public FakeChatProvider Blocking(string text)
    {
        _scripts.Enqueue(ct => BlockingScript(text, null, ct));
        return this;
    }

    /// <summary>先产出思考增量再阻塞，用于测试「思考中…」的流式头部。</summary>
    public FakeChatProvider BlockingThinking(string thinking, string text)
    {
        _scripts.Enqueue(ct => BlockingScript(text, thinking, ct));
        return this;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        IProviderContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        OnStream?.Invoke(request);
        var script = _scripts.Count > 0
            ? _scripts.Dequeue()
            : _ => Emit(new ChatEvent.Done(StopReason.EndTurn));

        await foreach (var evt in script(cancellationToken).ConfigureAwait(false))
            yield return evt;
    }

    private static IAsyncEnumerable<ChatEvent> Throw(Exception exception) => throw exception;

    private static async IAsyncEnumerable<ChatEvent> Emit(params ChatEvent[] events)
    {
        foreach (var evt in events) yield return evt;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatEvent> BlockingScript(
        string text,
        string? thinking,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (thinking is not null) yield return new ChatEvent.ThinkingDelta(thinking);
        yield return new ChatEvent.TextDelta(text);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public List<string> Texts { get; } = [];

    public Task SetTextAsync(string text)
    {
        Texts.Add(text);
        return Task.CompletedTask;
    }
}
