using Chat.Kernel;
using Chat.Providers.DeepSeek;
using Xunit;

namespace Chat.Kernel.Tests;

public sealed class PluginLoadingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmchat-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginLoadingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // 某些平台/时序下 DLL 仍被加载器短暂占用，删除可能抛 IOException 或权限异常；
        // 临时目录清理失败不应让测试本身失败。
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string InstallPlugin(string apiVersion = "1.0", string version = "1.0.0", string? platforms = null)
    {
        var dir = Path.Combine(_root, "chat.provider.deepseek", version);
        Directory.CreateDirectory(dir);
        var source = typeof(DeepSeekProvider).Assembly.Location;
        File.Copy(source, Path.Combine(dir, "chat.provider.deepseek.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $$"""
            {
              "id": "chat.provider.deepseek",
              "displayName": "DeepSeek",
              "version": "{{version}}",
              "apiVersion": "{{apiVersion}}",
              "entryAssembly": "chat.provider.deepseek.dll",
              "providerTypes": ["Chat.Providers.DeepSeek.DeepSeekProvider"]{{(platforms is null ? "" : $",\n  \"platforms\": [{platforms}]")}}
            }
            """);
        return dir;
    }

    [Fact]
    public void Catalog_FindsCompatiblePlugin()
    {
        InstallPlugin();
        var catalog = new PluginCatalog(_root, Path.Combine(_root, "user"));

        var candidates = catalog.Scan("0.1.0");

        var candidate = Assert.Single(candidates);
        Assert.Equal("chat.provider.deepseek", candidate.Manifest.Id);
        Assert.Equal(PluginSource.Builtin, candidate.Source);
    }

    [Fact]
    public void Catalog_RejectsWrongApiMajor()
    {
        InstallPlugin(apiVersion: "2.0");
        var catalog = new PluginCatalog(_root, Path.Combine(_root, "user"));

        Assert.Empty(catalog.Scan("0.1.0"));
    }

    [Fact]
    public void Catalog_RejectsUnsupportedPlatform()
    {
        InstallPlugin(platforms: "\"plan9-x64\"");
        var catalog = new PluginCatalog(_root, Path.Combine(_root, "user"));

        Assert.Empty(catalog.Scan("0.1.0"));
    }

}
