using Xunit;

namespace Chat.Console.Tests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task DeepSeek_OmittedModel_SendsDescriptorDefault()
    {
        await using var server = new MockHttpServer();
        using var data = new TempDataDirectory();

        var result = await CliProcess.RunAsync([
            "--provider", "chat.provider.deepseek",
            "--base-url", server.BaseUrl + "/anthropic/v1",
            "--api-key", "test-key",
            "--message", "hi",
            "--data-dir", data.Path,
        ]);
        var request = await server.Request;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ok", result.StdOut, StringComparison.Ordinal);
        Assert.Equal("/anthropic/v1/messages", request.Path);
        Assert.Contains("\"model\":\"deepseek-flash\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSeek_ExplicitModel_IsSentVerbatim()
    {
        await using var server = new MockHttpServer();
        using var data = new TempDataDirectory();

        var result = await CliProcess.RunAsync([
            "--provider", "chat.provider.deepseek",
            "--base-url", server.BaseUrl + "/anthropic/v1",
            "--api-key", "test-key",
            "--model", "custom-model",
            "--message", "hi",
            "--data-dir", data.Path,
        ]);
        var request = await server.Request;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"model\":\"custom-model\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\":\"deepseek-flash\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingApiKey_FailsWithMissingApiKey()
    {
        using var data = new TempDataDirectory();

        var result = await CliProcess.RunAsync([
            "--provider", "chat.provider.deepseek",
            "--message", "hi",
            "--data-dir", data.Path,
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing_api_key", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListModels_WithoutApiKey_UsesDescriptorDefault()
    {
        using var data = new TempDataDirectory();

        var result = await CliProcess.RunAsync(["--list-models", "--data-dir", data.Path]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("deepseek-flash", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ExitsZero()
    {
        var result = await CliProcess.RunAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("用法：llmchat-cli", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOption_ExitsTwo()
    {
        var result = await CliProcess.RunAsync(["--bogus"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("未知参数", result.StdErr, StringComparison.Ordinal);
    }
}
