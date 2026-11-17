namespace Chat.Abstractions;

/// <summary>协议族。主程序据此决定是否复用请求/响应转换器，插件仍可自行处理。</summary>
public enum ProtocolFamily
{
    /// <summary>Anthropic Messages（DeepSeek 官方 /anthropic/v1、OpenCode Go 的 anthropic 族）。</summary>
    AnthropicMessages,

    /// <summary>OpenAI Chat Completions 兼容。</summary>
    OpenAiCompatible,

    /// <summary>OpenAI Responses。</summary>
    OpenAiResponses,

    /// <summary>Google Gemini generateContent。</summary>
    GoogleGemini,

    /// <summary>厂商私有协议。</summary>
    Custom,
}

/// <summary>能力位。界面按位关闭对应控件（例如没有 Search 就不显示联网开关）。</summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Thinking = 1 << 0,
    ToolCall = 1 << 1,
    Search = 1 << 2,
    ReadUrl = 1 << 3,
    FileUpload = 1 << 4,
    Citation = 1 << 5,
    Usage = 1 << 6,
    PromptCache = 1 << 7,
    ImageInput = 1 << 8,
}

/// <summary>插件自述。Id 全局唯一且稳定（写入数据库的 provider_id 就是它）。</summary>
/// <param name="Id">例如 <c>chat.provider.deepseek</c>。</param>
/// <param name="DisplayName">界面上显示的名字。</param>
/// <param name="Family">协议族。</param>
/// <param name="Version">插件自身版本（SemVer）。</param>
/// <param name="Capabilities">能力位。</param>
/// <param name="DefaultModel">默认模型 id。</param>
/// <param name="Description">一句话说明。</param>
public sealed record ProviderDescriptor(
    string Id,
    string DisplayName,
    ProtocolFamily Family,
    string Version,
    ProviderCapabilities Capabilities,
    string? DefaultModel = null,
    string? Description = null);

/// <summary>模型条目。上下文窗口等元数据缺失时给 null，不要猜。</summary>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    int? ContextWindow = null,
    ProviderCapabilities? Capabilities = null);
