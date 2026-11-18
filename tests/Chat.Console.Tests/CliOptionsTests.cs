using Chat.Console;
using Xunit;

namespace Chat.Console.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_UsesDeepSeekDefaults()
    {
        var options = CliOptions.Parse([]);

        Assert.Equal("chat.provider.deepseek", options.ProviderId);
        Assert.Null(options.Model);
        Assert.Null(options.ApiKey);
        Assert.False(options.ShowHelp);
        Assert.False(options.Thinking);
        Assert.Empty(options.Settings);
    }

    [Fact]
    public void Parse_CollectsSettingsCaseInsensitively()
    {
        var options = CliOptions.Parse(["--set", "openai.model=glm-4.6", "--set", "OPENAI.MODEL=minimax"]);

        Assert.Equal("minimax", options.Settings["OpenAI.Model"]);
    }

    [Fact]
    public void Parse_HelpSkipsValidation()
    {
        var options = CliOptions.Parse(["--help", "--base-url", "not-a-url"]);

        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void Parse_RejectsUnknownOption()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--bogus"]));

        Assert.Contains("未知参数", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--model")]
    [InlineData("--provider")]
    [InlineData("--set")]
    public void Parse_RejectsMissingValue(string option)
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse([option]));

        Assert.Contains("缺少值", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsOptionConsumedAsValue()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--model", "--thinking"]));

        Assert.Contains("缺少值", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSetWithoutEquals()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--set", "broken"]));

        Assert.Contains("key=value", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not-a-url")]
    public void Parse_RejectsInvalidBaseUrl(string value)
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--base-url", value]));

        Assert.Contains("http/https", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsBlankMessage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--message", "  "]));

        Assert.Contains("--message 不能为空", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chat.provider.openai", "openai.model")]
    [InlineData("CHAT.PROVIDER.OPENCODEGO", "opencode.model")]
    [InlineData("chat.provider.deepseek", null)]
    [InlineData("chat.provider.unknown", null)]
    public void ModelSettingKey_MapsKnownProviders(string providerId, string? expected)
    {
        Assert.Equal(expected, CliOptions.ModelSettingKey(providerId));
    }
}
