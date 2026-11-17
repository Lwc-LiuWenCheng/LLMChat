using Chat.Abstractions;

namespace Chat.Kernel;

/// <summary>
/// 离线自检：只验证"能加载 + 契约描述合法 + 默认模型可用"，不联网。
/// 升级流程里由子进程调用（<c>LLMChat --selftest &lt;pluginDir&gt;</c>），失败则不切换版本。
/// </summary>
public static class SelfTest
{
    public static int Run(string pluginRoot, string appVersion, TextWriter output)
    {
        // 自检只扫描被测目录：user 根指向一个不存在的路径，避免把同一目录当作用户插件覆盖。
        var catalog = new PluginCatalog(pluginRoot, Path.Combine(Path.GetTempPath(), "llmchat-selftest-empty"));
        var host = new ProviderHost(catalog);
        var result = host.LoadAll(appVersion);

        foreach (var failure in result.Failures)
            output.WriteLine($"FAIL {failure.Candidate.Manifest.Id}: {failure.Reason}");

        foreach (var plugin in result.Loaded)
        {
            var d = plugin.Provider.Descriptor;
            var problems = Validate(d);
            output.WriteLine(problems.Count == 0
                ? $"OK   {d.Id} {d.Version} ({d.Family}, {plugin.Candidate.Source})"
                : $"FAIL {d.Id}: {string.Join("; ", problems)}");
            if (problems.Count > 0) result = new PluginLoadResult(result.Loaded, result.Failures.Append(new PluginLoadFailure(plugin.Candidate, string.Join("; ", problems))).ToList());
        }

        output.WriteLine($"plugin-selftest: {result.Loaded.Count} loaded, {result.Failures.Count} failed");
        return result.Failures.Count == 0 && result.Loaded.Count > 0 ? 0 : 1;
    }

    private static List<string> Validate(ProviderDescriptor d)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Id)) problems.Add("Descriptor.Id 为空");
        if (string.IsNullOrWhiteSpace(d.DisplayName)) problems.Add("Descriptor.DisplayName 为空");
        if (!Version.TryParse(d.Version.Split('-', '+')[0], out _)) problems.Add($"Descriptor.Version 不是 SemVer：{d.Version}");
        if (string.IsNullOrWhiteSpace(d.DefaultModel)) problems.Add("Descriptor.DefaultModel 为空");
        return problems;
    }
}
