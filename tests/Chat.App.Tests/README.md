# Chat.App.Tests — Avalonia Headless 界面冒烟测试

在**无显示器**环境（CI / 容器 / 无 X server）里真实创建 `MainWindow`，加载真实 XAML，
执行布局与数据绑定，覆盖消息流、Markdown 渲染、思考折叠、分支、会话管理、输入交互，
以及 chat-demo v3 ZIP 的导入/导出面板与 CLI 摘要。
不需要 `DISPLAY`，也不依赖 Skia/GPU：

- 平台：`Avalonia.Headless`（`AvaloniaHeadlessPlatformOptions.UseHeadlessDrawing = true`）。
- 宿主：直接驱动 `HeadlessUnitTestSession.StartNew(typeof(App), AvaloniaTestIsolationLevel.PerTest)`，
  把测试主体调度到真实 UI 线程。`Avalonia.Headless.XUnit` 12.1.6 声明依赖 xunit.v3 扩展点
  （`xunit.v3.extensibility.core` 3.2.2）理论上可用，但这里不用它：少一层 xunit 扩展点的
  版本耦合，且 PerTest 隔离与 UI 线程调度都由测试自己显式控制。
- 每个测试一份临时数据目录（`TempDataDirectory`），结束后清空 SQLite 连接池并删除，
  不会写入真实的 `~/.local/share/LLMChat`。
- 测试输出目录里的 `plugins/` 由 csproj 按各内置插件 `manifest.json` 自动复制，
  当前会加载 deepseek / openai / opencodego 三个插件；provider 行为可用 `FakeChatProvider` 注入。

## 运行方式

```bash
# 1) 先构建（测试工程会顺带构建 Chat.App 并把全部内置插件复制到输出目录 plugins/）
dotnet build Chat.slnx -v q

# 2) 直接运行测试程序（.NET 10 上 xunit.v3 不能走 dotnet test / VSTest）
dotnet tests/Chat.App.Tests/bin/Debug/net10.0/Chat.App.Tests.dll
```

CI 里建议同时清掉 `DISPLAY` 与宿主可能存在的 `DEEPSEEK_APIKEY`：后者是
`ProviderContext.GetSecretAsync` 的环境变量兜底，若宿主已设置，"未配置 Key" 用例会真的尝试联网。

```bash
env -u DISPLAY -u DEEPSEEK_APIKEY dotnet tests/Chat.App.Tests/bin/Debug/net10.0/Chat.App.Tests.dll
```

退出码 0 = 全部通过；失败时控制台输出每个失败用例的断言信息。

## 覆盖范围

### 窗口与绑定

| 用例 | 验证内容 |
| --- | --- |
| `Window_LoadsXamlAndBindsProvidersAndSessions` | 窗口实例化 + XAML 加载、Providers/Sessions 绑定、三个内置插件真实加载与启动状态文案 |
| `SettingsExpander_ExpandsAndApiKeyRoundTrips` | 设置 Expander 展开（VM→视图）、API Key 密码框回写（视图→VM） |
| `Composer_TextBoxRoundTripsToViewModel` | Composer 输入框双向绑定 |
| `EmptyState_ShowsGuidanceUntilSessionExists` | 无会话时显示空状态引导，新建会话后隐藏 |
| `NoPlugins_ShowsGuidance` | 无插件时显示对应引导与"插件 0 个已加载" |

### 消息渲染与流式

| 用例 | 验证内容 |
| --- | --- |
| `AssistantMarkdown_RendersIntoRichBlocksAndPresenters` | 标题/段落（粗体·行内代码·链接）/列表/代码块/表格/引用进入块模型并实例化 Rich Presenter |
| `Thinking_IsCollapsedAndSeparatedFromAnswer` | 思考默认折叠、文案"思考过程"、正文不含思考内容 |
| `Stop_CancelsStreamAndMarksIncomplete` | 生成中显示停止、思考中…、停止后 Incomplete/"已停止" 且恢复发送按钮 |
| `Failure_ShowsRetryAndDismissesError` | 失败状态徽标、错误条、重试成功后 Finished、关闭错误条 |
| `ProviderCancellation_MarksTimeoutWithError` | provider 自身超时（宿主未点停止）标为 Timeout/"超时"、错误条可见且可重试，不误显示"已停止" |
| `CopyMessage_UsesInjectedClipboard` | 复制命令写入注入的剪贴板服务 |
| `MessageViewModel_CoalescesStreamingRenders` | 流式增量按 `RenderThrottle` 合并，不会每个 token 全量重渲染 |

`MarkdownRendererTests` 是不依赖 Avalonia 的纯单测，覆盖标题层级、行内样式、链接、硬换行、
有序/无序列表、围栏/缩进代码块、引用、表格、分割线、原始 HTML 与纯文本拼接。

### 会话与输入

| 用例 | 验证内容 |
| --- | --- |
| `Send_EmptyComposer_DoesNothing` | 空消息不建会话、不发请求 |
| `Send_WithoutApiKey_PersistsUserMessageAndReportsError` | 发送路径端到端：用户消息落库、助手占位失败（missing_api_key）、错误文案展示，全程无网络 |
| `Edit_CreatesSiblingBranchAndNavigates` | 编辑用户消息 → 新兄弟分支，分支 2/2，‹ › 来回切换 |
| `Regenerate_CreatesSiblingAssistantAndSwitches` | 顶部"重新生成" → 在当前路径末尾建兄弟回答，2/2 与切回 1/2 |
| `RegenerateMessage_UsesClickedMessageParentAndSwitches` | 点某条助手消息的"重新生成" → 以该消息 ParentId 建兄弟（不是重生成最后一条），当前指针切到新兄弟 |
| `SendDuringStreaming_IsIgnored` | 流式生成期间再次发送被忽略：不建新会话/消息、不重复请求，停止后恢复 |
| `ImeProcessedEnter_DoesNotSend` | IME 组合键（`Key.ImeProcessed`）不触发发送 |
| `SessionCommands_ExecuteWithoutStreaming` | 新建会话、停止、重新生成、分支导航命令可执行且无会话时无害 |
| `RenamePinSearchAndDelete_WorkThroughCommands` | 重命名、置顶排序、搜索即时过滤、删除确认后软删除 |
| `EnterKey_SendsAndShiftEnterDoesNot` | Enter 发送、Shift+Enter 换行 |

> 输入约定：Enter 发送，Shift+Enter 换行（Ctrl+Enter 快捷键已移除，界面提示同步更新）。
>
> IME 已知限制：Avalonia 12 没有公开的输入法 composition 标志。平台把组合输入期间的按键
> 报告为 `Key.ImeProcessed` 时不会触发发送（有用例覆盖）；若某平台把"确认候选词"报告为
> 普通 `Key.Enter`，应用无法与真正发送区分。此时请用 Shift+Enter 换行或点发送按钮。

新增界面/绑定时建议同步加一条 headless 用例：失败会直接指出是哪条绑定/模板在运行期抛错。
### 导入 / 导出（chat-demo v3）

| 用例 | 验证内容 |
| --- | --- |
| `Window_ArchivePanelStartsHiddenAndHasEntryPoints` | 左侧"导入备份…/导出当前…/导出全部…"入口存在，面板初始隐藏 |
| `Import_ConfirmRefreshesListSelectsImportedAndKeepsLocalSessions` | ZIP 预检确认文案含会话/附件数；确认后后台导入、摘要含新增计数、列表刷新并选中导入会话、本地会话保留、消息树显示 |
| `Import_BadArchiveShowsReadableErrorAndNoConfirmation` | 损坏 ZIP 提示"无法读取备份文件…"，不显示确认框、不产生摘要 |
| `Import_CancelPendingConfirmationHidesPanelWithoutWriting` | 取消确认隐藏面板、不写库 |
| `Export_AllWritesV3ArchiveAndShowsSummary` | 导出全部生成 v3 ZIP（manifest 可读、会话正确）并显示摘要 |
| `Export_CurrentWithoutSelectionReportsReadableError` | 无选中会话时导出当前给出可读错误 |

`CommandLineTests` 覆盖 CLI 参数解析（--import/--export/--session/--help 与错误文案）、
导入 JSON 摘要与退出码、损坏包错误路径且保留原库、重复导入幂等、导出指定会话→再导入的
round-trip、空库导出，以及实际 `CliRunner` 的用法输出。

> 文件选择器本身是 Avalonia `StorageProvider` 的薄封装，headless 下不弹系统对话框；
> 测试直接调用 ViewModel 的 `PrepareImportAsync`/`ExportAllAsync`，与真实按钮回调共用同一路径。
