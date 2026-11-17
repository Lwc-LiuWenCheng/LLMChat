# LLMChat

Avalonia 12 桌面聊天客户端：多 provider 插件、会话树/分支、流式 Markdown 渲染，数据落在本地 SQLite。

> **状态：M0。** 已具备主窗口 + 绑定、内置插件加载（DeepSeek / OpenAI 兼容 / OpenCode Go）、
> 会话与消息树持久化、Markdown 渲染、思考折叠、分支导航/编辑/重新生成、停止生成，
> 以及一套在无显示器环境可运行的 headless UI 冒烟测试。
> chat-demo v3 ZIP 导入/导出已接入界面与 CLI：合并导入（导入前自动快照）、预检/进度/取消/结果摘要。

## 定位

官方网页版/客户端的会话只保留**七天**，过期后无法回看，也无法带走。LLMChat 把会话、消息树（含分支）、
思考过程与附件全部存在**本地 SQLite**（`chat.db` + 附件 sha256 内容寻址），不依赖云端保留期：
七天前的对话照样能打开、搜索、继续追问，数据可以整库备份或导出成 chat-demo v3 ZIP 迁移到别的机器。

## 功能

- 多 provider 插件：DeepSeek（Anthropic Messages）、OpenAI 兼容（GLM / MiniMax）、OpenCode Go；插件独立 ALC 加载，只共享 `Chat.Abstractions`。
- 会话树与分支：编辑用户消息创建兄弟分支，`‹ ›` 切换并显示 `n/m`；助手消息可重新生成。
- 流式 Markdown：标题/列表/表格/引用/代码块，代码块等宽可横向滚动；思考过程默认折叠、结束后可展开。
- 生成控制：Enter 发送 / Shift+Enter 换行，发送中可停止；错误条与内容过滤状态显示在气泡上并可重试。
- 会话管理：重命名、置顶、删除确认、按更新时间排序、搜索即时过滤。
- 迁移与备份：界面与 CLI 均支持 chat-demo v3 ZIP 合并导入/导出（导入前自动快照）。
- 无显示器 CI：Avalonia Headless 真正加载 XAML 并驱动绑定/命令，不需要 xvfb。

## 架构

```
                 ┌────────────────────────────────────────────┐
                 │ Chat.App（Avalonia 12 / MVVM）              │
                 │ MainWindow · MainWindowViewModel · 渲染     │
                 └──────┬──────────────────┬──────────────────┘
                        │                  │
          ┌─────────────▼───────┐   ┌──────▼───────────────┐
          │ Chat.Kernel         │   │ Chat.Storage         │
          │ 插件目录 / ALC 加载  │   │ SQLite · 快照 · 附件 │
          │ ProviderHost/SelfTest│  │ chat-demo ZIP        │
          └─────────┬───────────┘   └──────┬───────────────┘
                    │                      │
          ┌─────────▼───────────┐   ┌──────▼───────────────┐
          │ Chat.Abstractions   │   │ Chat.Core            │
          │ IChatProvider       │   │ SessionTree          │
          │ ChatEvent/ChatPart  │   │ PromptAssembler      │
          └─────────▲───────────┘   └──────────────────────┘
                    │ 插件只依赖契约
   ┌────────────────┴───────────────────────────────────────┐
   │ plugins/（每个厂商一个插件，独立 AssemblyLoadContext）  │
   │ chat.provider.deepseek · .openai · .opencodego          │
   └────────────────────────────────────────────────────────┘
```

| 工程 | 职责 |
|---|---|
| `src/Chat.Abstractions` | 唯一 ABI：`IChatProvider`、`ChatEvent`/`ChatPart`、`PluginApi`（零依赖） |
| `src/Chat.Core` | `Session`/`SessionTree`/`MessageNode`：消息树与分支；`PromptAssembler` 组装请求 |
| `src/Chat.Kernel` | `PluginCatalog`、`PluginManifest`、`PluginLoadContext`（独立 ALC）、`ProviderHost`、`SelfTest` |
| `src/Chat.Storage` | `chat.db`（SQLite/WAL）、会话/消息/设置仓储、快照、附件（sha256）、chat-demo ZIP |
| `src/Chat.Providers.*` | DeepSeek（Anthropic Messages）、OpenAI 兼容、OpenCode Go；仅引用 Abstractions |
| `src/Chat.App` | Avalonia 桌面端：主窗口、设置、流式渲染、分支交互 |
| `tests/*` | 六套 xunit.v3 测试（含 Avalonia.Headless UI 测试） |

内置插件（`src/Chat.App/Chat.App.csproj` 的 `CopyBuiltinPlugins` 目标在构建后复制到
`src/Chat.App/bin/Debug/net10.0/plugins/<id>/<version>/`）：

| id | 显示名 | 类型 | 默认显示 |
|---|---|---|---|
| `chat.provider.deepseek` | DeepSeek | AnthropicMessages | 是 |
| `chat.provider.openai` | OpenAI 兼容 | OpenAiCompatible | 是 |
| `chat.provider.opencodego` | OpenCode Go | OpenAiCompatible | 否（实验性，默认折叠） |

## 环境要求

- .NET SDK **10.0.403**（`global.json` 已钉版本）
- Linux / macOS / Windows 桌面环境；**测试不需要显示器**（Avalonia.Headless），不要装 xvfb
- 首次还原需要网络（NuGet）；包版本集中在 `Directory.Packages.props`（CPM）

## 构建与运行

所有命令从仓库根目录执行：

```bash
cd /srv/LLMChat

# 构建（0 warning / 0 error 为基线）
dotnet build Chat.slnx -v q

# 启动应用（Debug 构建，等价于 dotnet src/Chat.App/bin/Debug/net10.0/LLMChat.dll）
bash scripts/run.sh
```

也可以直接用 SDK 命令启动：

```bash
dotnet run --project src/Chat.App
```

> 没有 DISPLAY 的机器只能跑测试与 `--selftest`；直接启动窗口会以 `XOpenDisplay failed` 退出。

## 插件自检 --selftest

`--selftest` 不启动窗口，只扫描插件目录并逐个实例化/握手，输出每个插件的结果：

```bash
bash scripts/run.sh --selftest
```

```console
OK   chat.provider.deepseek 1.0.0 (AnthropicMessages, Builtin)
OK   chat.provider.openai 1.0.0 (OpenAiCompatible, Builtin)
OK   chat.provider.opencodego 1.0.0 (OpenAiCompatible, Builtin)
plugin-selftest: 3 loaded, 0 failed
```

- 默认扫描 `AppContext.BaseDirectory/plugins`（即 `src/Chat.App/bin/Debug/net10.0/plugins`）。
- 可指定目录：`bash scripts/run.sh --selftest /path/to/plugins`。
- 退出码：**0 当且仅当加载数 > 0 且失败数 = 0**；目录不存在会得到 `0 loaded, 0 failed` 且退出码 1。

## 测试

本仓库使用 .NET 10 + xunit.v3，VSTest 已不支持 xunit.v3，因此 **不能用 `dotnet test`**；
跑法是直接执行测试工程的 dll。全量脚本已封装：

```bash
bash scripts/test.sh
```

脚本做三件事：`dotnet build Chat.slnx -v q` → 依次运行六套测试 dll（统一
`env -u DISPLAY -u DEEPSEEK_APIKEY`）→ 打印每套件 Total/Failed 与汇总；任一失败退出码为 1。
当前基线（6/6 套件通过）：

| 套件 | Total |
|---|---|
| `Chat.Core.Tests` | 11 |
| `Chat.Storage.Tests` | 22 |
| `Chat.Kernel.Tests` | 5 |
| `Chat.App.Tests` | 54 |
| `Chat.Providers.OpenAiCompatible.Tests` | 35 |
| `Chat.Providers.OpenCodeGo.Tests` | 32 |

单独跑某一套（示例：UI 冒烟）：

```bash
env -u DISPLAY -u DEEPSEEK_APIKEY dotnet tests/Chat.App.Tests/bin/Debug/net10.0/Chat.App.Tests.dll
```

`Chat.App.Tests` 用 `Avalonia.Headless` 的 `HeadlessUnitTestSession`（PerTest 隔离）真正加载 XAML、
挂载 `MainWindow` + `MainWindowViewModel` 并驱动绑定/命令：窗口实例化、设置展开、Composer 往返、
空消息不发送、无 Key 时的错误路径、分支导航、编辑产生兄弟分支、停止/重新生成、Markdown 渲染，
以及导入预检/确认/摘要、导出、CLI 参数解析与 `--import`/`--export` JSON/退出码等。
测试使用临时数据目录，不会污染用户数据；没有 `DEEPSEEK_APIKEY` 时不会发出真实网络请求。

## 数据目录

默认数据目录（`Chat.Storage.StoragePaths.DefaultDataDirectory`）：

| 系统 | 路径 |
|---|---|
| Linux | `$XDG_DATA_HOME/LLMChat`，未设置时为 `~/.local/share/LLMChat` |
| macOS | `~/Library/Application Support/LLMChat` |
| Windows | `%APPDATA%\LLMChat` |

```
<数据目录>/
├── chat.db                 # SQLite（WAL）
├── snapshots/              # 导入前自动快照
├── files/                  # 附件，按 sha256 命名
├── plugins/                # 用户侧插件（可覆盖内置版本）
└── logs/provider.log       # provider 日志
```

测试/嵌入场景可用 `AppServices.Create(dataDirectory)` 指定其他目录。

## 密钥与模型配置

三种方式配置 provider 密钥，**设置优先，环境变量兜底**：

| provider | 设置键（app_settings） | 环境变量 |
|---|---|---|
| DeepSeek | `deepseek.apiKey` | `DEEPSEEK_APIKEY` |
| OpenAI 兼容 | `openai.apiKey` | `OPENAI_APIKEY` |
| OpenCode Go | `opencode.apiKey` | `OPENCODE_APIKEY` |

- 界面：右侧 **Settings** 展开后填 API Key，点「保存」即写入当前 provider 的设置。
- 环境变量名由设置键转大写、`.`→`_` 得到（如 `deepseek.apiKey` → `DEEPSEEK_APIKEY`）。
- 没有密钥时不会发网络请求，气泡上显示可关闭的错误条（如「未配置 DeepSeek API Key。」）。
- 因为环境变量会兜底，**跑测试前要清掉**（`scripts/test.sh` 已统一 `-u DEEPSEEK_APIKEY`）。

Base URL 设置键：`deepseek.baseUrl`（默认 `https://api.deepseek.com/anthropic/v1`）、
`openai.baseUrl`、`opencode.baseUrl`（默认 `https://opencode.ai/inference/go/openai/v1`）。
默认模型：DeepSeek `deepseek-flash`、OpenAI 兼容 `glm-4.6`、OpenCode Go `deepseek-v4-pro`。

## chat-demo v3 ZIP 导入 / 导出

界面与 CLI 均已接入，共用 `Chat.Storage.ChatDemo` 的同一套实现。

### 界面

左栏顶部三个按钮：

- **导入备份…**：选择 ZIP 后先预检（会话数、附件数、导入前快照提示），点「确认导入」才写库；
  导入中显示不确定进度条，可点「取消操作」；完成后显示摘要（会话/消息/附件的新增、更新、跳过，
  快照文件名，警告数），点「关闭摘要」收起；读取失败时显示可读错误且不写库。
- **导出当前…**：导出当前选中会话；没有选中会话时按钮禁用。
- **导出全部…**：导出所有未删除会话。

导入按会话 id 合并：同名覆盖、保留本地独有会话，重复导入幂等；导入前自动快照到 `snapshots/`。

### 命令行

```bash
cd /srv/LLMChat

# 合并导入
bash scripts/run.sh --import /path/to/chat-demo-v3.zip

# 导出全部未删除会话
bash scripts/run.sh --export /tmp/llmchat-backup.zip

# 只导出指定会话（可重复 --session）
bash scripts/run.sh --export /tmp/one-session.zip --session <session-id>

# 用法帮助
bash scripts/run.sh --help
```

- 成功与失败都输出**一行 JSON**；退出码 **0 = 成功**、**1 = 参数错误 / 执行失败 / 被取消**。
- Ctrl+C 取消时输出 `{"command":"...","status":"error","error":"操作已取消。"}` 并退出 1。
- `--import` 输出字段：`added/updated/skipped`（会话）、`addedMessages/updatedMessages/skippedMessages`、
  `files/reusedFiles/skippedFiles`（附件）、`snapshot`、`warnings`。
- `--export` 输出字段：`path/sessions/messages/files/bytes/warnings`。
- `--import` 与 `--export` 互斥；`--session` 只能与 `--export` 一起用；不带 `--session` 的
  `--export` 导出全部未删除会话。CLI 与界面共用默认数据目录。

### ZIP 布局

```text
manifest.json
chat_session_index.json
chat_sessions/{session_id}.json
chat_sessions/{session_id}_files/{file_id}_{name}
```

库入口是 `Chat.Storage.ChatDemo.ChatDemoArchive`（自动建库/迁移/快照）：

```csharp
using Chat.Storage.ChatDemo;

// 导入：默认数据目录；也可传第二个参数指定数据目录
var imported = await ChatDemoArchive.ImportAsync("/tmp/chat-demo-v3.zip");
Console.WriteLine(
    $"会话 +{imported.Added} ~{imported.Updated} ={imported.Skipped}；" +
    $"消息 +{imported.AddedMessages} ~{imported.UpdatedMessages}；" +
    $"附件 {imported.Files}（复用 {imported.ReusedFiles}）");

// 导出全部未删除会话
var exported = await ChatDemoArchive.ExportAsync("/tmp/backup.zip");
Console.WriteLine($"{exported.Sessions} 会话 / {exported.Messages} 消息 → {exported.Path}");

// 只导出指定会话
await ChatDemoArchive.ExportSessionsAsync(
    "/tmp/pick.zip", Chat.Storage.StoragePaths.DefaultDataDirectory, ["<session-id>"]);
```

行为要点：

- 导入前若有可导入会话，自动创建快照（`snapshots/`）；会话按 id upsert，消息按
  `(session_id, id)` upsert，保留本地新增消息与分支，重复导入幂等。
- 附件按 sha256 落 `files/`，内容相同只存一份（`ReusedFiles`）。
- 解压前统一校验 entry 名（防 zip slip），不解压原始路径；警告写入 `Warnings`（上限 200 条）。
- 报告：`ImportReport`（Added/Updated/Skipped、消息与附件计数、SnapshotPath、Warnings）、
  `ExportReport`（Path/Sessions/Messages/Files/Bytes/Warnings）。

## 已知限制

1. **工具调用**：provider 层能发送/解析 `ToolCall` 事件，但界面没有工具执行器，也不渲染工具卡片；
   当前只持久化并渲染文本与思考（`MessageViewModel.BuildParts`）。
2. **插件打包**：构建只复制插件的 `entryAssembly` + `manifest.json`，不带插件的私有依赖/deps.json；
   当前三个插件只共享 `Chat.Abstractions`，因此无影响，引入第三方库前需改打包逻辑。
3. **插件更新**：内置插件随包分发；用户目录覆盖、GitHub Release 自动更新的设计见
   [docs/architecture/provider-plugin-design.md](docs/architecture/provider-plugin-design.md)，尚未实现。
4. **ZIP 导入导出**：界面与 CLI 均可用；导入期间只显示不确定进度条（没有逐会话百分比），
   暂不支持拖拽 ZIP 或导入历史记录。
5. **SQLite 版本**：固定 `Microsoft.Data.Sqlite 10.0.0`；11.0.0 的 native 依赖 GLIBC ≥ 2.34，
   在 GLIBC 2.31 的宿主上会加载失败。
6. **无显示器环境**：测试与 `--selftest` 可用；真实窗口仍需桌面/远程显示，不要用 xvfb 兜底。

## 相关文档

- [docs/dev-environment.md](docs/dev-environment.md)：SDK、包版本、日常命令、无 DISPLAY 的 headless 说明
- [docs/architecture/provider-plugin-design.md](docs/architecture/provider-plugin-design.md)：插件分层、版本解析、Release 更新与回滚
