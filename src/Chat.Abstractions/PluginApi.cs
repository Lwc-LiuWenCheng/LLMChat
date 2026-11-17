namespace Chat.Abstractions;

/// <summary>
/// 插件契约版本。插件在 manifest 里声明 <c>apiVersion</c>，主程序按 <see cref="Major"/>
/// 做兼容判断：Major 相同的插件才允许加载。契约只增不改——新增成员放在次要版本里，
/// 移除/重命名一律通过 Major 升级，由加载器拒绝而不是运行期崩溃。
/// </summary>
public static class PluginApi
{
    /// <summary>不兼容变更时递增。</summary>
    public const int Major = 1;

    /// <summary>向后兼容的新增递增（记录用，加载器只比较 Major）。</summary>
    public const string Version = "1.0";
}
