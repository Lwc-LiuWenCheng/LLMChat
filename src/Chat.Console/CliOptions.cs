namespace Chat.Console;

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _settings = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowHelp { get; private set; }

    public string ProviderId { get; private set; } = "chat.provider.deepseek";

    public string? Model { get; private set; }

    public string? ApiKey { get; private set; }

    public string? BaseUrl { get; private set; }

    public string? SystemPrompt { get; private set; }

    public string? Message { get; private set; }

    public bool Thinking { get; private set; }

    public bool ListModels { get; private set; }

    public bool Verbose { get; private set; }

    public string? DataDirectory { get; private set; }

    public IReadOnlyDictionary<string, string> Settings => _settings;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "--provider":
                    options.ProviderId = RequireValue(args, ref i, arg);
                    break;
                case "--model":
                    options.Model = RequireValue(args, ref i, arg);
                    break;
                case "--api-key":
                    options.ApiKey = RequireValue(args, ref i, arg);
                    break;
                case "--base-url":
                    options.BaseUrl = RequireValue(args, ref i, arg);
                    break;
                case "--system":
                    options.SystemPrompt = RequireValue(args, ref i, arg);
                    break;
                case "--message":
                    options.Message = RequireValue(args, ref i, arg);
                    break;
                case "--thinking":
                    options.Thinking = true;
                    break;
                case "--list-models":
                    options.ListModels = true;
                    break;
                case "--data-dir":
                    options.DataDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                case "--set":
                    var pair = RequireValue(args, ref i, arg);
                    var separator = pair.IndexOf('=');
                    if (separator <= 0)
                        throw new CliUsageException($"--set 需要 key=value 形式，实际收到“{pair}”。");
                    options._settings[pair[..separator]] = pair[(separator + 1)..];
                    break;
                default:
                    throw new CliUsageException($"未知参数：{arg}。");
            }
        }

        if (options.ShowHelp) return options;
        if (string.IsNullOrWhiteSpace(options.ProviderId))
            throw new CliUsageException("--provider 不能为空。");
        if (options.Message is { } message && string.IsNullOrWhiteSpace(message))
            throw new CliUsageException("--message 不能为空。");
        if (options.BaseUrl is { Length: > 0 } baseUrl &&
            (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new CliUsageException("--base-url 必须是 http/https 绝对地址。");
        }

        return options;
    }

    /// <summary>已知 provider 的 base URL 设置键；未知插件请用 --set 直接指定。</summary>
    public static string? BaseUrlSettingKey(string providerId) => providerId.ToLowerInvariant() switch
    {
        "chat.provider.deepseek" => "deepseek.baseUrl",
        "chat.provider.openai" => "openai.baseUrl",
        "chat.provider.opencodego" => "opencode.baseUrl",
        _ => null,
    };

    /// <summary>
    /// 已知 provider 的模型设置键。这些插件会在请求模型为空时自己读取该设置，
    /// 因此 CLI 不能再用 DefaultModel 覆盖它。
    /// </summary>
    public static string? ModelSettingKey(string providerId) => providerId.ToLowerInvariant() switch
    {
        "chat.provider.openai" => "openai.model",
        "chat.provider.opencodego" => "opencode.model",
        _ => null,
    };

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new CliUsageException($"{name} 缺少值。");
        return args[++index];
    }
}

internal sealed class CliUsageException(string message) : Exception(message);
