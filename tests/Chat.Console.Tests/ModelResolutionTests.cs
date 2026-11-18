using System.Runtime.CompilerServices;
using Chat.Abstractions;
using Chat.Console;
using Xunit;

namespace Chat.Console.Tests;

public sealed class ModelResolutionTests
{
    [Fact]
    public void ExplicitModelWins()
    {
        var options = CliOptions.Parse(["--model", "chosen"]);

        var resolved = CliRunner.ResolveModel(Provider("chat.provider.deepseek", "deepseek-flash"), options, Settings());

        Assert.Equal("chosen", resolved.Model);
        Assert.Null(resolved.Error);
    }

    [Fact]
    public void OmittedModelUsesDescriptorDefault()
    {
        var options = CliOptions.Parse([]);

        var resolved = CliRunner.ResolveModel(Provider("chat.provider.deepseek", "deepseek-flash"), options, Settings());

        Assert.Equal("deepseek-flash", resolved.Model);
        Assert.Null(resolved.Error);
    }

    [Fact]
    public void ProviderModelSettingKeepsProviderResolution()
    {
        // OpenAI/OpenCode 的 provider 会自己读 openai.model/opencode.model；
        // 这里必须留空，否则 DefaultModel 会覆盖 --set 的配置。
        var options = CliOptions.Parse([]);

        var resolved = CliRunner.ResolveModel(
            Provider("chat.provider.openai", "glm-4.6"),
            options,
            Settings(("openai.model", "minimax")));

        Assert.Equal(string.Empty, resolved.Model);
        Assert.Null(resolved.Error);
    }

    [Fact]
    public void MissingDefaultModelReturnsClearError()
    {
        var options = CliOptions.Parse([]);

        var resolved = CliRunner.ResolveModel(Provider("chat.provider.unknown", null), options, Settings());

        Assert.Null(resolved.Model);
        Assert.NotNull(resolved.Error);
        Assert.Contains("没有默认模型", resolved.Error, StringComparison.Ordinal);
        Assert.Contains("chat.provider.unknown", resolved.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsForOtherProvidersDoNotOverrideDeepSeekDefault()
    {
        var options = CliOptions.Parse([]);

        var resolved = CliRunner.ResolveModel(
            Provider("chat.provider.deepseek", "deepseek-flash"),
            options,
            Settings(("openai.model", "minimax")));

        Assert.Equal("deepseek-flash", resolved.Model);
    }

    private static IChatProvider Provider(string id, string? defaultModel) =>
        new StubProvider(new ProviderDescriptor(
            id, "Test Provider", ProtocolFamily.AnthropicMessages, "1.0.0", ProviderCapabilities.None, defaultModel));

    private static IReadOnlyDictionary<string, string> Settings(params (string Key, string Value)[] pairs)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            settings[key] = value;
        return settings;
    }

    private sealed class StubProvider(ProviderDescriptor descriptor) : IChatProvider
    {
        public ProviderDescriptor Descriptor { get; } = descriptor;

        public async IAsyncEnumerable<ChatEvent> StreamAsync(
            ChatRequest request,
            IProviderContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
