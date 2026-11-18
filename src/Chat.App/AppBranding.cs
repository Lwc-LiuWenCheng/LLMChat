namespace Chat.App;

/// <summary>
/// 全局界面文案：产品名及所有面向用户的固定文本集中在此，XAML 通过 x:Static 引用。
/// 改产品名或欢迎语只需改这里一处，界面各处同步更新。
/// </summary>
public static class AppBranding
{
    /// <summary>产品名称（窗口标题、侧栏标识、输入框占位符等共用）。</summary>
    public const string ProductName = "DeepSeek";

    /// <summary>窗口标题栏文本；设为 null 时不显示，避免与自定义顶栏重复。</summary>
    public const string WindowTitle = "";

    /// <summary>侧栏顶部产品标识。</summary>
    public const string SidebarBrand = "✦  " + ProductName;

    /// <summary>欢迎页问候语。</summary>
    public const string Greeting = "下午好，我能帮什么忙吗？";

    /// <summary>欢迎页副标题。</summary>
    public const string GreetingSubtitle = "输入问题开始一段新对话。";

    /// <summary>输入框占位符。</summary>
    public const string ComposerPlaceholder = "给 " + ProductName + " 发送消息…（Enter 发送，Shift+Enter 换行）";

    /// <summary>设置面板中的 API Key 标题。</summary>
    public const string ApiKeyLabel = ProductName + " API Key";

    /// <summary>免责声明。</summary>
    public const string Disclaimer = "内容由 AI 生成，请自行核实重要信息。";
}
