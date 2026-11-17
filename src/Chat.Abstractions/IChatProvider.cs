namespace Chat.Abstractions;

public enum ProviderLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 宿主提供给插件的能力。插件不得自己 new HttpClient，也不得直接读环境变量/密钥文件。
/// </summary>
public interface IProviderContext
{
    /// <summary>当前会话 id（同时作为 OpenCode Go 的 x-opencode-session 等路由亲和标识）。</summary>
    string SessionId { get; }

    string AppVersion { get; }

    /// <summary>宿主共享的 HTTP 管道（已配置 UA、超时与重试策略）。</summary>
    HttpMessageInvoker Http { get; }

    /// <summary>按 key 取密钥（例如 <c>deepseek.apiKey</c>）。没有返回 null。</summary>
    ValueTask<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);

    void Log(ProviderLogLevel level, string message);

    /// <summary>插件可写的临时目录，随会话清理。</summary>
    string TempDirectory { get; }

    /// <summary>用户为该 provider 配置的附加设置（基址、自定义头等）。</summary>
    IReadOnlyDictionary<string, string> Settings { get; }
}

/// <summary>
/// provider 插件入口。实现类必须有公共无参构造函数——加载器用反射创建。
/// </summary>
public interface IChatProvider
{
    ProviderDescriptor Descriptor { get; }

    /// <summary>流式对话。取消时抛 <see cref="OperationCanceledException"/> 即可，宿主负责收尾。</summary>
    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, IProviderContext context, CancellationToken cancellationToken = default);

    /// <summary>模型清单。默认返回 <see cref="ProviderDescriptor.DefaultModel"/> 单条。</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(IProviderContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ModelInfo>>(
            Descriptor.DefaultModel is { Length: > 0 } id ? [new ModelInfo(id, id)] : []);
}

/// <summary>插件主动报错（可带厂商错误码）。</summary>
public sealed class ProviderException : Exception
{
    public ProviderException(string code, string message, bool retryable = false, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}
