using System.Reflection;
using System.Text;
using Chat.Abstractions;
using Chat.Kernel;

namespace Chat.Console;

internal static class CliRunner
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var builtinRoot = Path.Combine(AppContext.BaseDirectory, "plugins");
        var dataDirectory = options.DataDirectory ?? DefaultDataDirectory();
        var sessionId = "cli-" + Guid.NewGuid().ToString("N")[..12];
        var tempDirectory = Path.Combine(Path.GetTempPath(), "llmchat-cli", sessionId);
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var host = new ProviderHost(new PluginCatalog(builtinRoot, Path.Combine(dataDirectory, "plugins")));
            var result = host.LoadAll(AppVersion);
            foreach (var failure in result.Failures)
            {
                System.Console.Error.WriteLine(
                    $"[plugin] 跳过 {failure.Candidate.Manifest.Id}：{failure.Reason}");
            }

            var provider = result.Loaded
                .Select(loaded => loaded.Provider)
                .FirstOrDefault(p => string.Equals(p.Descriptor.Id, options.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                var available = result.Loaded.Count == 0
                    ? "（无）"
                    : string.Join(", ", result.Loaded.Select(loaded => loaded.Provider.Descriptor.Id));
                System.Console.Error.WriteLine($"[error] 找不到 provider“{options.ProviderId}”。已加载：{available}");
                System.Console.Error.WriteLine($"[error] 内置插件目录：{builtinRoot}");
                return 2;
            }

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in options.Settings)
                settings[pair.Key] = pair.Value;

            if (options.BaseUrl is { Length: > 0 } baseUrl)
            {
                var key = CliOptions.BaseUrlSettingKey(provider.Descriptor.Id);
                if (key is null)
                {
                    System.Console.Error.WriteLine(
                        $"[error] provider {provider.Descriptor.Id} 没有已知的 baseUrl 设置键，请改用 --set <key>=<url>。");
                    return 2;
                }

                settings[key] = baseUrl;
            }

            using var context = new CliProviderContext(
                sessionId, AppVersion, options.ApiKey, settings, tempDirectory, options.Verbose);

            if (options.ListModels)
                return await ListModelsAsync(provider, context, cancellationToken).ConfigureAwait(false);

            // 不要把空 model 发给 provider：DeepSeek 等插件会原样写进请求体。
            var model = ResolveModel(provider, options, settings);
            if (model.Model is null)
            {
                System.Console.Error.WriteLine($"[failed] missing_model：{model.Error}");
                return 1;
            }

            if (options.Message is { } message)
                return await SendOnceAsync(provider, context, options, model.Model, message, cancellationToken).ConfigureAwait(false);

            return await ReplAsync(provider, context, options, model.Model, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            System.Console.Error.WriteLine("[cancelled] 已中止。");
            return 130;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static async Task<int> ListModelsAsync(
        IChatProvider provider,
        IProviderContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var models = await provider.ListModelsAsync(context, cancellationToken).ConfigureAwait(false);
            foreach (var model in models)
            {
                System.Console.Out.WriteLine(
                    $"{model.Id}\t{model.DisplayName}\t{model.ContextWindow?.ToString() ?? "-"}");
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            System.Console.Error.WriteLine("[cancelled] 已中止。");
            return 130;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[error] 列出模型失败：{Describe(ex)}");
            return 1;
        }
    }

    private static async Task<int> SendOnceAsync(
        IChatProvider provider,
        IProviderContext context,
        CliOptions options,
        string model,
        string message,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, message) };
        var request = BuildRequest(options, model, messages, options.SystemPrompt);
        var printer = new StreamPrinter();
        var exitCode = await StreamTurnAsync(provider, context, request, printer, cancellationToken).ConfigureAwait(false);
        printer.End();
        return exitCode;
    }

    private static async Task<int> ReplAsync(
        IChatProvider provider,
        IProviderContext context,
        CliOptions options,
        string model,
        CancellationToken cancellationToken)
    {
        if (System.Console.IsInputRedirected)
        {
            var piped = await System.Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(piped))
            {
                System.Console.Error.WriteLine("[error] 标准输入为空；请用 --message 或在终端中交互运行。");
                return 2;
            }

            return await SendOnceAsync(provider, context, options, model, piped.TrimEnd(), cancellationToken)
                .ConfigureAwait(false);
        }

        var history = new List<ChatMessage>();
        var system = options.SystemPrompt;
        PrintBanner(provider, model);

        while (true)
        {
            System.Console.Out.Write("you> ");
            System.Console.Out.Flush();

            string? line;
            try
            {
                line = await System.Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                System.Console.Out.WriteLine();
                return 130;
            }

            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line is "/exit" or "/quit") break;

            if (line == "/help")
            {
                PrintReplHelp();
                continue;
            }

            if (line == "/clear")
            {
                history.Clear();
                System.Console.Out.WriteLine("（历史已清空）");
                continue;
            }

            if (line.StartsWith("/system", StringComparison.Ordinal))
            {
                var value = line.Length > "/system".Length ? line["/system".Length..].Trim() : string.Empty;
                system = value.Length == 0 ? null : value;
                System.Console.Out.WriteLine(system is null ? "（system 已清空）" : "（system 已更新）");
                continue;
            }

            if (line.StartsWith('/'))
            {
                System.Console.Out.WriteLine($"未知命令：{line}；用 /help 查看。");
                continue;
            }

            var message = await ReadMultilineAsync(line, cancellationToken).ConfigureAwait(false);
            if (message is null) return 130;

            var messages = new List<ChatMessage>(history) { new(ChatRole.User, message) };
            var request = BuildRequest(options, model, messages, system);
            var printer = new StreamPrinter();
            var exitCode = await StreamTurnAsync(provider, context, request, printer, cancellationToken)
                .ConfigureAwait(false);
            printer.End();
            if (exitCode == 130) return 130;

            System.Console.Out.WriteLine();
            if (exitCode == 0)
            {
                history.Add(new ChatMessage(ChatRole.User, message));
                var answer = printer.AssistantText;
                if (answer.Length > 0)
                    history.Add(new ChatMessage(ChatRole.Assistant, answer));
            }
        }

        return 0;
    }

    private static async Task<string?> ReadMultilineAsync(string firstLine, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(firstLine);
        var current = firstLine;
        while (current.EndsWith('\\'))
        {
            builder.Length--;
            builder.Append('\n');
            System.Console.Out.Write("...> ");
            System.Console.Out.Flush();

            string? next;
            try
            {
                next = await System.Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                System.Console.Out.WriteLine();
                return null;
            }

            if (next is null) break;
            builder.Append(next);
            current = next;
        }

        return builder.ToString();
    }

    private static async Task<int> StreamTurnAsync(
        IChatProvider provider,
        IProviderContext context,
        ChatRequest request,
        StreamPrinter printer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chatEvent in provider.StreamAsync(request, context, cancellationToken).ConfigureAwait(false))
            {
                switch (chatEvent)
                {
                    case ChatEvent.TextDelta delta:
                        printer.Text(delta.Text);
                        break;
                    case ChatEvent.ThinkingDelta delta:
                        printer.Thinking(delta.Text);
                        break;
                    case ChatEvent.ToolCallStart start:
                        printer.ToolStart(start.CallId, start.Name);
                        break;
                    case ChatEvent.ToolCallArgumentsDelta:
                        break;
                    case ChatEvent.ToolCallCompleted completed:
                        printer.ToolCompleted(completed.CallId, completed.ArgumentsJson);
                        break;
                    case ChatEvent.ToolResultEvent toolResult:
                        printer.ToolResult(toolResult.CallId, toolResult.Content, toolResult.IsError);
                        break;
                    case ChatEvent.UsageEvent usage:
                        printer.Usage(usage.Usage);
                        break;
                    case ChatEvent.RawEvent raw:
                        printer.Raw(raw.Json);
                        break;
                    case ChatEvent.Failed failed:
                        printer.Failed(failed);
                        return 1;
                    case ChatEvent.Done done:
                        printer.Done(done.Reason);
                        return 0;
                    default:
                        printer.Error($"[warn] 未识别事件：{chatEvent.GetType().Name}");
                        break;
                }
            }

            printer.Error("[failed] stream_ended：provider 未发送 Done 事件。");
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            printer.Error("[cancelled] 已中止。");
            return 130;
        }
        catch (ProviderException ex)
        {
            printer.Error($"[failed] {ex.Code}：{ex.Message}{(ex.Retryable ? "（可重试）" : string.Empty)}");
            return 1;
        }
        catch (Exception ex)
        {
            printer.Error($"[error] 请求失败：{Describe(ex)}");
            return 1;
        }
    }

    /// <summary>解析本次请求的 model；Model 为 null 表示无法确定，Error 直接展示给用户。</summary>
    internal static ModelResolution ResolveModel(
        IChatProvider provider,
        CliOptions options,
        IReadOnlyDictionary<string, string> settings)
    {
        if (!string.IsNullOrWhiteSpace(options.Model))
            return new ModelResolution(options.Model.Trim(), null);

        var settingKey = CliOptions.ModelSettingKey(provider.Descriptor.Id);
        if (settingKey is not null &&
            settings.TryGetValue(settingKey, out var configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            // 留空让 provider 自己读设置；若这里填 DefaultModel 会覆盖 --set 的模型配置。
            return new ModelResolution(string.Empty, null);
        }

        if (!string.IsNullOrWhiteSpace(provider.Descriptor.DefaultModel))
            return new ModelResolution(provider.Descriptor.DefaultModel.Trim(), null);

        return new ModelResolution(
            null,
            $"provider {provider.Descriptor.Id} 没有默认模型，请用 --model 指定。");
    }

    private static ChatRequest BuildRequest(
        CliOptions options,
        string model,
        IReadOnlyList<ChatMessage> messages,
        string? system) => new()
    {
        Model = model,
        Messages = messages,
        System = system,
        Extra = options.Thinking
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["thinking"] = "enabled" }
            : null,
    };

    private static void PrintBanner(IChatProvider provider, string? model)
    {
        var descriptor = provider.Descriptor;
        System.Console.Out.WriteLine(
            $"llmchat-cli {AppVersion} | {descriptor.DisplayName} ({descriptor.Id}) | 模型：{model ?? "默认"}");
        System.Console.Out.WriteLine("输入 /help 查看命令，/exit 退出；行尾反斜杠续行；Ctrl+C 中止。");
    }

    private static void PrintReplHelp()
    {
        System.Console.Out.WriteLine(
            """
            /help            显示本帮助
            /clear           清空对话历史
            /system <文本>   设置 system 提示（不带参数则清空）
            /exit, /quit     退出
            """);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string AppVersion
    {
        get
        {
            var informational = typeof(CliRunner).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational)) return "0.1.0";
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
    }

    // 与 Chat.Storage.StoragePaths.DefaultDataDirectory 保持一致；CLI 不引用 Storage，避免把 SQLite 带进命令行工具。
    private static string DefaultDataDirectory()
    {
        const string appFolderName = "LLMChat";
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appFolderName);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", appFolderName);

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", appFolderName)
            : Path.Combine(xdg, appFolderName);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响命令结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}

/// <summary>ResolveModel 的结果；Model 非 null 时 Error 必为 null。</summary>
internal sealed record ModelResolution(string? Model, string? Error);
