using System.Text;
using Chat.Abstractions;

namespace Chat.Console;

/// <summary>把统一事件流映射为终端输出：正文进 stdout，其余摘要进 stderr。</summary>
internal sealed class StreamPrinter
{
    private readonly TextWriter _out = System.Console.Out;
    private readonly TextWriter _err = System.Console.Error;
    private readonly StringBuilder _assistant = new();
    private bool _textOpen;
    private bool _thinkingOpen;

    public string AssistantText => _assistant.ToString();

    public void Text(string text)
    {
        EndThinking();
        _textOpen = true;
        _out.Write(text);
        _out.Flush();
        _assistant.Append(text);
    }

    public void Thinking(string text)
    {
        EndText();
        if (!_thinkingOpen)
        {
            _err.Write("[thinking] ");
            _thinkingOpen = true;
        }

        _err.Write(text);
        _err.Flush();
    }

    public void ToolStart(string callId, string name) => Meta($"[tool] {name}（{callId}）");

    public void ToolCompleted(string callId, string argumentsJson) =>
        Meta($"[tool] {callId} 参数：{Truncate(argumentsJson, 300)}");

    public void ToolResult(string callId, string content, bool isError) =>
        Meta($"[tool:{callId}] {(isError ? "失败" : "完成")}：{Truncate(content, 300)}");

    public void Usage(TokenUsage usage) =>
        Meta($"[usage] input={Show(usage.InputTokens)} output={Show(usage.OutputTokens)} cached={Show(usage.CachedInputTokens)} reasoning={Show(usage.ReasoningTokens)}");

    public void Raw(string json) => Meta($"[raw] {Truncate(json, 500)}");

    public void Done(StopReason reason) => Meta($"[done] {reason}");

    public void Failed(ChatEvent.Failed failed) =>
        Meta($"[failed] {failed.Code}：{failed.Message}{(failed.Retryable ? "（可重试）" : string.Empty)}");

    public void Error(string message) => Meta(message);

    public void End()
    {
        EndText();
        EndThinking();
    }

    private void EndText()
    {
        if (!_textOpen) return;
        _out.WriteLine();
        _textOpen = false;
    }

    private void EndThinking()
    {
        if (!_thinkingOpen) return;
        _err.WriteLine();
        _thinkingOpen = false;
    }

    private void Meta(string line)
    {
        EndText();
        EndThinking();
        _err.WriteLine(line);
    }

    private static string Show(long? value) => value?.ToString() ?? "-";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
