# 模型接入层插件化设计：内置插件 + GitHub Release 自动更新

> 结论先说：provider 适配层做成**随安装包内置的插件**，每个插件独立版本；更新走 **GitHub Releases**
> 的索引资产自动下载，用户目录覆盖内置版本（不动安装目录），失败自动回退到内置版本——回退零下载。
> 厂商改接口时只发一个插件包，主程序、数据库、其他厂商完全不受影响。

参考实现：DSH（`@deepseek-ai/dsh`）的 cordis 插件 + bundle/profile 分层 + manifest 版本协商 + 安装回滚。
本机结论来自 `/usr/lib/node_modules/@deepseek-ai/dsh/node_modules/@deepseek-ai/dsh-plugin-manager/README.md`、
`.../dsh-plugin-market-index/lib/types/types.d.ts`、`.../dsh-package-manifest/README.md`。

---

## 0. 与上一版的差异（用户已定）

| 维度 | 上一版 | 本版 |
|---|---|---|
| 形态 | 市场可下载安装 | **内置随包**，用户目录可覆盖升级 |
| 分发 | 自建插件市场索引 | **GitHub Releases**：索引 JSON 作为 release 资产，插件 zip 作为另一组 release 资产 |
| 发现页 | 浏览索引 | 不做发现页；只做「已安装 / 可更新 / 版本历史 / 回滚」 |
| 回滚 | 下载旧版本 | **直接回退到内置版本**（零下载、零等待） |

---

## 1. 为什么「内置 + 独立更新」而不是二选一

- 纯内置：离线可用、首次启动即能聊天；但厂商改接口就得发整个应用，用户被迫全量升级。
- 纯可下载：更新灵活；但首次使用必须联网装插件，安装器/网络/镜像任一环节出问题 = 应用不可用。
- **内置基线 + Release 增量**：两者共用同一套 ALC 加载与版本解析逻辑，内置版本天然是回滚目标。
  这正是 DSH 的做法（bundle 随 profile 内置，插件再按需安装/更新），只是把「市场」换成「release 资产」。

---

## 2. 从 DSH 借的机制

| DSH 机制 | LLMChat 等价物 | 解决的问题 |
|---|---|---|
| 契约包独立、零运行时依赖 | `Chat.Abstractions`（只有 interface + record） | 插件与宿主唯一 ABI |
| `engines.dsh` 兼容区间 | `apiVersion: "^1.0"` + `minAppVersion` | 不兼容**拒绝加载**，而非运行时崩 |
| `dsh.manifestVersion` 与包版本解耦 | `manifestVersion: 1` 与插件 `version` 解耦 | 包格式演进不影响语义版本 |
| profile 分层 patch | 内置 `bundles/builtin.json` ← 用户 `plugin-state.json`（只记偏离项） | 升级不丢配置 |
| `dsh.bundle.platforms` | `platforms: ["any" \| "linux-x64" …]` | 错平台不加载 |
| `listedByDefault` | 实验性 provider（OpenCode Go）默认折叠 | 半成品不干扰用户 |
| 市场索引 format 1（单 JSON / 多版本 / 固定下载源） | `stable.json` release 资产 | 一个静态文件即可分发，版本可复现 |
| 安装器状态流 + 失败回滚文件 | `IProgress<InstallEvent>` + 版本目录 + 指针 | 不留半个插件 |
| 安装脚本需审批 | **直接禁止**任何安装/构建脚本 | 供应链攻击面归零 |
| HMR / `restart-required` 区分 | 默认 `restart-required`，空闲 provider 可热换 | 不做「假生效」 |

不照搬：cordis 服务容器（只给最小 `IProviderContext`）、pnpm/NuGet 用户侧依赖链（ZIP 直发）、YAML patch（用 JSON）。

---

## 3. 分层与依赖方向

```
Chat.Abstractions          ← 契约（net10.0，零依赖，独立 SemVer）
   ↑            ↑
Chat.Kernel   Chat.Provider.{DeepSeek,OpenCodeGo,OpenAiCompatible,Anthropic}
(会话树/存储)    ↑ 每个厂商一个插件工程
   ↑            ↑
Chat.Host  ← PluginLoader / PluginStore / ReleaseUpdater / SelfTest
   ↑
Chat.App (Avalonia 12)
```

**硬规则**：`Chat.Provider.*` 只能引用 `Chat.Abstractions`。
用 `Directory.Build.props` + CI 里的程序集引用检查强制（`dotnet list reference`），防止有人图省事直接引用 Kernel。

插件若要带第三方库（SSE 解析、分词器），必须 `PrivateAssets=all` 并在独立 ALC 内私有加载，不提升为共享程序集。

---

## 4. 目录布局与版本解析

```
<安装目录>/                                    # 只读：Windows Program Files / macOS .app / Linux /opt
├── LLMChat(.exe)
├── Chat.Abstractions.dll
└── plugins/
    ├── provider.deepseek/2.3.0/{plugin.json, *.dll, models.json}
    ├── provider.opencodego/1.1.0/…
    └── bundles/builtin.json                   # 内置清单：id + version + 默认启用

<用户数据目录>/                                 # 可写，升级只写这里
~/.local/share/LLMChat/   (%APPDATA%\LLMChat\   ~/Library/Application Support/LLMChat/)
├── plugins/provider.deepseek/2.3.3/…          # 覆盖内置的最新版
├── plugin-state.json                          # activeVersion 指针 + 启用状态 + 失败计数
├── updates/                                   # 下载临时区（成功后清空）
└── logs/plugin-host.log
```

解析顺序（`PluginStore.Resolve`）：

1. 候选 = 内置目录 ∪ 用户目录（同 id 同版本只算一次）。
2. 过滤：`apiVersion` 满足宿主 `1.x`、`platforms` 含本机 RID 或 `any`、`minAppVersion` ≤ 当前应用版本。
3. 若 `plugin-state.json` 指定了 `activeVersion` 且该版本在候选内 → 用它（用户可手动钉住）。
4. 否则取满足条件的**最高 SemVer**（预发布版仅在 beta 通道允许）。
5. 全不满足 → 该 provider 标记 `Unsupported` 并在 UI 说明原因；应用照常启动。

---

## 5. 契约 v1（Chat.Abstractions）

```csharp
namespace Chat.Abstractions;

public static class PluginApi
{
    public const int Major = 1;
    public const string Version = "1.0";
}

/// manifest.entry.type 必须实现此接口，并提供公共无参构造。
public interface IChatProvider
{
    ProviderDescriptor Descriptor { get; }

    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, IProviderContext ctx, CancellationToken ct);

    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(IProviderContext ctx, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
}

public sealed record ProviderDescriptor(
    string Id, string DisplayName, string ProtocolFamily, string Version, ProviderCapabilities Capabilities);

[Flags]
public enum ProviderCapabilities
{
    None = 0, Thinking = 1, ToolCall = 2, Search = 4, ReadUrl = 8,
    FileUpload = 16, Citation = 32, Usage = 64, PromptCache = 128,
}

public abstract record ChatEvent
{
    public sealed record TextDelta(string Text) : ChatEvent;
    public sealed record ThinkingDelta(string Text) : ChatEvent;
    public sealed record ToolCall(string CallId, string Name, string ArgumentsJson) : ChatEvent;
    public sealed record ToolResult(string CallId, string Content) : ChatEvent;
    public sealed record Usage(int InputTokens, int OutputTokens, int? CacheReadTokens = null,
                               int? ReasoningTokens = null, decimal? CostUsd = null) : ChatEvent;
    public sealed record Raw(string Json) : ChatEvent;      // 不透明，原样落库
    public sealed record Failed(string Code, string Message, bool Retryable) : ChatEvent;
    public sealed record Done(StopReason Reason, string? Error = null) : ChatEvent;
}

public enum StopReason { EndTurn, MaxTokens, ToolUse, ContentFilter, Cancelled, Error, Unknown }

public interface IProviderContext
{
    string SessionId { get; }
    string AppVersion { get; }
    HttpMessageInvoker Http { get; }                        // 宿主托管连接池/超时/代理
    Task<string?> GetSecretAsync(string key, CancellationToken ct);
    void Log(LogLevel level, string message, Exception? ex = null);
    string TempDirectory { get; }
    IReadOnlyDictionary<string, string> Settings { get; }    // baseUrl / beta 头 / max_completion_tokens 开关
}
```

### 5.1 稳定性规则（写进 CONTRIBUTING，并用快照测试守住）

1. **只增不改**：v1 内不改签名、不删字段、不改语义；新能力用新接口 `IChatProviderV2`。
2. **枚举容错**：宿主遇到未知 `StopReason`/未知事件 → 忽略并计数；插件遇到未知请求字段同理。
3. **不透明载荷**：`ChatEvent.Raw` 与库里 `messages.provider_raw` 一律不解析——**协议变化永不触发数据库迁移**。
4. **不泄漏宿主类型**：契约里不出现 `HttpClient`、`ILogger`、`IServiceProvider`。
5. 契约 public API 快照（`PublicApiGenerator` 生成并提交）变更需显式批准，CI 拦截意外破坏。

---

## 6. 更新通道：GitHub Releases

### 6.1 tag 与资产约定

| Release tag | 内容 | 可变性 |
|---|---|---|
| `v0.1.0` | 应用安装包（win-x64 / osx-arm64 / linux-x64）+ `app-stable.json` | 不可变 |
| `provider.deepseek-v2.3.1` | `provider.deepseek-2.3.1.zip` + `.sha256` | 不可变（一个版本一个 tag） |
| `plugins-index` | `stable.json` / `beta.json` | **可变**（CI `gh release upload --clobber`） |

索引固定 URL，不需要 GitHub API、不需要 token、不受 60 次/小时限流：

```
https://github.com/<owner>/<repo>/releases/download/plugins-index/stable.json
https://github.com/<owner>/<repo>/releases/download/plugins-index/beta.json
```

`stable.json`：

```json
{
  "format": 1,
  "channel": "stable",
  "generatedAt": "2026-11-16T11:00:00Z",
  "minAppVersion": "0.1.0",
  "plugins": [{
    "id": "provider.deepseek",
    "displayName": { "zh": "DeepSeek", "en": "DeepSeek" },
    "versions": [{
      "version": "2.3.1",
      "publishTimestamp": 1767225600,
      "apiVersion": "^1.0",
      "minAppVersion": "0.1.0",
      "platforms": ["any"],
      "download": {
        "url": "https://github.com/<owner>/<repo>/releases/download/provider.deepseek-v2.3.1/provider.deepseek-2.3.1.zip",
        "sha256": "<64 hex>",
        "size": 184320
      },
      "signature": "<base64 ed25519 over sha256，可选>",
      "changelog": { "zh": "修复 reasoning_content 字段解析" }
    }]
  }]
}
```

CI 生成规则（`tools/release-index`）：读各插件 `plugin.json` 版本 → 校验 tag 与版本一致 → 计算 zip 的 sha256/size → 生成 JSON → 上传。**索引是产物，不手写。**

### 6.2 客户端行为

- 启动后延迟 5s 检查（不阻塞 UI），之后每 24h 一次；`设置 → 插件 → 检查更新` 手动触发。
- 请求带 `If-None-Match`（缓存 ETag）、超时 10s、最多 3 次指数退避重试；失败静默写日志，应用完全离线可用。
- 只下载**比本地高**且满足 `apiVersion`/`platforms`/`minAppVersion` 的版本；beta 通道需用户显式开启。
- 默认「静默下载 + 提示重启」，不强制更新；「跳过此版本」记入 `plugin-state.json`。

---

## 7. 升级 / 回滚流程

```
检查索引 → 选版本 → 下载到 <user>/updates/<guid>.zip（进度上报）
  → 校验 sha256（+ 签名）
  → 解压到 <user>/plugins/<id>/<version>/ （与旧版并存，不覆盖）
  → 子进程 selftest：LLMChat --selftest <pluginDir>
       · 加载程序集 + 契约版本匹配
       · 录制的 SSE 夹具回放（流式解析 / reasoning / usage / tool_call）
  → 原子写 plugin-state.json 的 activeVersion（tmp → rename）
  → 若该 provider 无活跃流 → 热切换；否则标记 restart-required 并提示
任一步失败：删除新目录、指针不动、内置版本继续工作
```

回滚：

- **手动**：插件页「回滚」→ 指针指向内置版本（或上一用户版本），**零下载**。
- **自动**：同一版本连续 2 次加载/自检失败 → 自动回退到内置版本，记 `failureCount`，UI 提示「已回退到内置 2.3.0」。
- 保留策略：用户目录保留最近 2 个版本，其余自动清理。

安全：

- 安装目录永不写入（macOS 签名包、Program Files 权限问题一并绕开）。
- 凭据存 OS 安全存储（DPAPI / Keychain / libsecret），`plugin-state.json` 只存引用；升级不触碰凭据。
- 插件包内**不得**含安装/构建脚本（`.sh`/`.bat`/`.targets`），原生库必须预编译随包。

---

## 8. 加载与故障边界

```csharp
var alc = new PluginLoadContext(pluginDir);
// Load(): "Chat.Abstractions" → 返回 null（走 Default ALC 共享契约）
//         其他 assembly → 从 pluginDir 解析（私有依赖互不污染）
var asm = alc.LoadFromAssemblyPath(Path.Combine(pluginDir, entry.Assembly));
var provider = (IChatProvider)Activator.CreateInstance(asm.GetType(entry.Type, true)!)!;
```

| 场景 | 处置 |
|---|---|
| manifest 非法 / 缺字段 | 跳过该插件，其余正常启动 |
| 依赖缺失（`FileNotFoundException`） | 标记 `Broken`，UI 给「回滚」入口 |
| 构造/初始化抛异常（超时 5s） | 熔断该插件，其他 provider 可用 |
| 流中途异常 / HTTP 5xx | 转成 `ChatEvent.Failed` 写进该条消息，会话可继续 |
| 插件死循环 / StackOverflow / OOM | 进程内无法隔离 → 见下 |

**诚实边界**：ALC 是*依赖隔离*，不是*故障隔离*。三层应对：

1. **准入**：默认只加载内置插件 + 已签名更新包；未签名更新需开发者模式。
2. **装前自检**：子进程跑 selftest，崩溃只影响自检。
3. **可外置**：契约是「纯数据 + 异步流」，将来可整体改为子进程 + JSON-RPC，宿主与插件代码几乎不动。

---

## 9. 为什么单个接口变更不会炸整体

| 风险 | 阻断点 |
|---|---|
| 厂商改 SSE 字段（`reasoning`→`reasoning_content`） | 只改该插件解析；`Raw` 让历史消息照常渲染 |
| 新增模型 | 改 `models.json`（或插件内嵌清单），**不发版** |
| 改鉴权/路径 | 插件 `defaultBaseUrl` + 用户可覆盖设置（应急） |
| 新插件有 bug | selftest 拦截；运行期连续失败自动回退内置版本 |
| 插件崩溃 | 该 provider 标记 Broken，其他 provider 与会话不受影响 |
| 契约演进 | 独立 SemVer + 区间协商；不兼容拒绝加载 |
| 数据库结构 | 不涉及：`provider_raw` 为 opaque JSON |
| 应用要升级 | 插件与 app 分开发版；app 升级不动用户插件目录 |

---

## 10. 工程结构与构建环境

```
src/Chat.Abstractions/     # 契约 + public API 快照测试
src/Chat.Kernel/           # 会话树、消息、提示词组装
src/Chat.Storage/          # SQLite + v3 ZIP 导入/导出 + 快照
src/Chat.Host/             # PluginLoader / PluginStore / ReleaseUpdater / Installer / SelfTest
src/Chat.App/              # Avalonia 12
plugins/Chat.Provider.DeepSeek/
plugins/Chat.Provider.OpenCodeGo/
plugins/Chat.Provider.OpenAiCompatible/   # GLM / MiniMax / 任意 OpenAI 兼容
plugins/Chat.Provider.Anthropic/
tests/…                    # 契约快照 + 各插件夹具回放 + 加载器/回滚
tools/release-index/       # CI：生成 stable.json / beta.json
```

构建环境已就绪（详见 [dev-environment.md](../dev-environment.md)）：复用 `/srv/workspace/env/dotnet` 的 **.NET 10.0.403**，
`/usr/local/bin/dotnet` 已软链，`global.json` 钉版本，`NuGet.config` 复用 `/srv/workspace/env/cache/nuget`。
实测：Avalonia **12.1.6** + CommunityToolkit.Mvvm **8.4.2** + Microsoft.Data.Sqlite **10.0.0**（11.0.0 的 native 依赖 GLIBC ≥ 2.34，宿主只有 2.31，勿升级） + Markdig **1.6.0** 在 net10.0 上还原并编译通过。

---

## 11. 里程碑

- **M0**：`Chat.slnx` + `Chat.Abstractions`（契约 + 快照测试）+ Kernel 树模型 + `PluginLoader`（加载一个内置 HelloProvider 并跑通 selftest 子进程）。
- **M1**：SQLite + v3 ZIP 导入（merge）+ 导出 + 快照 —— 先解决「七天」问题。
- **M2**：`Chat.Provider.DeepSeek` 打成插件 + Avalonia 聊天界面 + 插件页（版本、更新、回滚）。
- **M3**：`OpenAiCompatible`（GLM / MiniMax）+ `OpenCodeGo` 薄封装（`x-opencode-session`、`max_completion_tokens` 开关）。
- **M4**：`ReleaseUpdater` 自动更新 + CI 生成索引 + 应用自更新 + 附件/Markdown/Mermaid/KaTeX。

> 注意：**M2 就把 DeepSeek 写成插件**，不要「先内联后抽」——抽的那次重构会把契约设计错一遍。

---

## 12. 待确认

1. **GitHub 仓库归属**：`<owner>/<repo>` 是哪个？（决定索引 URL 与 CI 配置；仓库还没 `git init`）
2. **签名**：先只校验 sha256，还是从第一版就 Ed25519 签名（需你保管私钥）？
3. **beta 通道**：是否现在就开（Go 的 provider 迭代快，beta 通道能先上实验版）？
4. **Avalonia 12**：确认用 12.1.6（当前 feed 最新）而不是 11.x？12 的 XAML/模板与网上 11.x 示例有差异，M0 会先跑一遍官方模板核对。
