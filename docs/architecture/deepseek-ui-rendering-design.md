# 对话内容渲染设计 —— 历史加载链路 / 实时流式 / Markdown 呈现

> 任务：在 `Chat.App`（Avalonia 12.1.6 + SukiUI 7.0.1）复刻 DeepSeek 网页端界面之前，先厘清「消息内容怎么渲染」。
> 本文是**只读探索 + 单文档交付**：所有结论均来自当前仓库源码，每条都标注文件路径；不臆造字段/API，不改动任何 `.cs` / `.axaml`。
> 背景设计见 `docs/architecture/deepseek-ui-design.md`（7 个 UserControl 方案）；本文聚焦其中的 `ConversationView` / `MessageItemView` 的数据来源与渲染机制。

---

## 1. 现状事实表（每条带路径证据）

| # | 事实 | 证据（文件路径 / 位置） | 对 UI 复刻的影响 |
|---|---|---|---|
| 1 | 持久化是本地 SQLite 单库，消息存储为「消息树」：`messages.id/parent_id` + `parts` 子表 | `src/Chat.Storage/ChatDatabase.cs`（`Schema`：sessions / messages / parts / app_settings；`CurrentSchemaVersion=2`）；`src/Chat.Storage/MessageRepository.cs`（`ListBySession` / `Insert` / `Update`） | 显示层拿到的是**全量节点列表**，必须自己按树算「当前可见路径」 |
| 2 | `Session.CurrentMessageId` 指向当前分支末端；它决定显示哪条路径 | `src/Chat.Core/Session.cs`（`CurrentMessageId` 注释）；`src/Chat.Core/SessionTree.cs`（`PathTo`） | 切换分支 = 改 `CurrentMessageId` 后重新 LoadMessages |
| 3 | 树→线性转换是纯函数 `SessionTree.PathTo(nodes, currentId)`：沿 `ParentId` 回溯到根并 `Reverse` | `src/Chat.Core/SessionTree.cs::PathTo` | 线性列表 = 根 → 当前节点；其他分支节点不显示 |
| 4 | 切换会话由 `OnSelectedSessionChanged` 触发 `LoadMessages`（受 `_suppressReload` 抑制） | `src/Chat.App/ViewModels/MainWindowViewModel.cs::OnSelectedSessionChanged` / `LoadMessages` | 新界面必须保留 `_suppressReload` 机制，避免 ListBox 双向绑定回写导致重复加载/丢选中 |
| 5 | `Messages` 是 `ObservableCollection<MessageViewModel>`，加载时先 `Clear()` 再逐条 `Add()` | `MainWindowViewModel.cs::LoadMessages`、`Messages` 属性 | 每次切换会话是「整列表重建」，不是增量打补丁 |
| 6 | 每条 `MessageViewModel` 只从 `Parts` 取两类文本：`PartKind.Text` → `Text`、`PartKind.Thinking` → `Thinking`（各取 `FirstOrDefault`） | `src/Chat.App/ViewModels/MessageViewModel.cs` 构造函数；`src/Chat.Core/ContentPart.cs`（`PartKind` 枚举） | `ToolCall/ToolResult/Image/File/Risk/Raw` 等 Part **当前不参与消息显示** |
| 7 | 助手消息用 `Blocks`（Markdown 块模型）显示，用户消息用原始 `Text` 纯文本显示 | `src/Chat.App/Views/MainWindow.axaml` 消息模板：`ItemsControl ItemsSource="{Binding Blocks}" IsVisible="{Binding IsAssistant}"` 与 `SelectableTextBlock Text="{Binding Text}" IsVisible="{Binding IsUser}"` | `MessageItemView` 必须按角色分流两种呈现 |
| 8 | 流式事件是**逐 token 增量**（`TextDelta` / `ThinkingDelta`），结束发 `Done`，失败发 `Failed` | `src/Chat.Abstractions/ChatEvent.cs`（`TextDelta` / `ThinkingDelta` / `Done` / `Failed`）；`src/Chat.Providers.DeepSeek/DeepSeekProvider.cs::StreamAsync`（`content_block_delta` → `yield return new ChatEvent.TextDelta(...)`） | 不是整段返回，UI 必须支持高频局部更新 |
| 9 | 流式期间**只追加一次新消息**：开头建一条 `Status=Wip` 的助手节点并 `Messages.Add(vm)`；之后每帧只改这同一条 VM 的 `Text`/`Thinking` | `MainWindowViewModel.cs::RunAssistantAsync`（`Messages.Add(vm)` 位于 `await foreach` 之前；循环内为 `vm.Text += delta.Text`） | 每帧不 Add/Remove 集合项 → ItemsControl 不重建项容器 |
| 10 | Markdown 重渲染有 50ms 节流：首个增量立即 `FlushRender`，其后 `DispatcherTimer` 每窗口最多一次；流结束 `OnStatusChanged` + `finally` 再兜底一次 | `MessageViewModel.cs`（`RenderThrottle = 50ms`、`ScheduleRender`、`FlushRender`、`OnTextChanged`、`OnStatusChanged`；`MainWindowViewModel.cs::RunAssistantAsync` finally 中 `vm.FlushRender()`） | 现状已是「节流版 Markdown」，不是每 token 全量解析 |
| 11 | VM 不直接 `Dispatcher.Post` 更新消息内容，靠 `await foreach(...).ConfigureAwait(true)` 恢复 UI 上下文；显式 Dispatcher 用在滚动与防抖 | `MainWindowViewModel.cs::RunAssistantAsync`（`.ConfigureAwait(true)`）、`OnSearchTextChanged`、`UpdateArchiveImportProgress`；`src/Chat.App/Views/MainWindow.axaml.cs::ScrollToEnd`（`Dispatcher.UIThread.Post(..., DispatcherPriority.Background)`） | 新 View 沿用同一 VM 即可，UI 线程上下文由现有调用链保证 |
| 12 | 自动滚底只订阅 `Messages.CollectionChanged`，**仅在集合 Add/Clear 时滚底**；文本 token 增长不触发滚动 | `MainWindow.axaml.cs::OnDataContextChanged` / `OnMessagesChanged` / `ScrollToEnd` | 用户上滚不会被 token 拉回（现状），但新消息 Add 仍无条件拉底（需增强） |
| 13 | Markdown 解析是纯函数 `MarkdownRenderer.Render`，Markdig + `UseAdvancedExtensions()`，产出 8 类 Block；不依赖 Avalonia，已被单测覆盖 | `src/Chat.App/Rendering/MarkdownRenderer.cs`；`tests/Chat.App.Tests/MarkdownRendererTests.cs` | 可原样迁移 DataTemplate 到新控件，不重写解析器 |
| 14 | Presenter 把 Inlines 重建为 Avalonia `Run/Span/LineBreak`（`SelectableTextBlock`）；表格用 `Grid` 逐格生成 | `src/Chat.App/Rendering/MarkdownPresenters.cs`（`MarkdownInlineBuilder` / `MarkdownParagraphPresenter` / `MarkdownHeadingPresenter` / `MarkdownTablePresenter`） | 渲染是「VM 产出模型 → Presenter 映射控件」两层，视图不解析 Markdown |
| 15 | 8 种 Block 全部已在 `MainWindow.axaml` 配好 DataTemplate | `src/Chat.App/Views/MainWindow.axaml::<Window.DataTemplates>`（Heading / Paragraph / CodeBlock / List / Quote / Table / ThematicBreak / Html） | 迁移资源即可复用 |
| 16 | 停止生成 = `StopCommand` → `_cts.Cancel()`；用户取消→`Incomplete`（「已停止」），provider 自身超时→`Timeout` | `MainWindowViewModel.cs::Stop` / `RunAssistantAsync` catch/finally；`MessageViewModel.StatusText` | 错误/状态展示走 `StatusText` + `ErrorText`/`HasError` |
| 17 | 分支导航 `PrevBranch/NextBranch` 是**会话级**（基于 `CurrentMessageId` 路径上最深的有兄弟节点），不是消息级 | `src/Chat.App/Services/BranchNavigator.cs::FindBranchNode` / `DescendToLatest`；`MainWindowViewModel.cs::MoveBranch` | 操作栏可绑定会话级分支命令；单条消息级切分支需增强 |
| 18 | 7 个新 UserControl **尚未创建**：`Views/` 目录当前只有 `MainWindow.axaml(.cs)` 与 `HomePage.axaml(.cs)` | `src/Chat.App/Views/` 目录清单 | 本文是实施前设计；渲染资源、命令绑定需在新控件中接好 |

---

## 2. 历史对话记录加载链路

### 2.1 流程（文字）

1. **触发**：启动时构造函数 `RefreshSessions()` 并选中 `Sessions[0]`；用户点击侧边栏会话执行 `SelectSessionCommand` → `SelectedSession = value`（`MainWindowViewModel.cs` 构造函数 / `SelectSession`）。
2. **属性钩子**：`OnSelectedSessionChanged` 更新 `ShowEmptyState` / `IsPinned`；若 `_suppressReload == true`（导入/重命名/分支切换/新建会话等场景），**跳过** `LoadMessages`，避免 ListBox `SelectedItem` 双向绑定回写造成重复加载或选中丢失（`MainWindowViewModel.cs::OnSelectedSessionChanged`、`MoveBranch` 注释）。
3. **读取**：`LoadMessages(session)` 调 `_services.Messages.ListBySession(session.Id)` → `MessageRepository.ListBySession`：先 `SELECT messages ... ORDER BY id` 读节点本体，再 `SELECT parts ... ORDER BY message_id, seq` 读片段并按 `id` 填回 `Parts`（`MessageRepository.cs`）。
4. **树→线性**：`SessionTree.PathTo(nodes, session.CurrentMessageId)` 从当前节点沿 `ParentId` 回溯到根、`Reverse()`，得到根 → 当前节点的线性路径；**只有这条路径上的节点可见**，其他分支节点留在库里但不显示（`SessionTree.cs::PathTo`）。
5. **填充**：`Messages.Clear()` 后 `foreach (node in path) Messages.Add(new MessageViewModel(node))`。构造 `MessageViewModel` 时若为助手，立即 `FlushRender()` → `MarkdownRenderer.Render(Text)` 生成 `Blocks`（`MessageViewModel.cs` 构造函数 / `FlushRender`）。
6. **分支状态**：`UpdateBranch` 用 `BranchNavigator.FindBranchNode` 找到路径上最深的有兄弟节点，再用 `SessionTree.BranchPosition` 算出 `BranchText = "i/n"`、`CanNavigateBranch = count > 1`（`MainWindowViewModel.cs::UpdateBranch`）。
7. **渲染**：`Messages`（`ObservableCollection`）绑定 `ItemsControl.ItemsSource`；模板按 `IsUser` 分流——用户用 `SelectableTextBlock(Text)`，助手用 `ItemsControl(Blocks)`；`Blocks` 由 `<Window.DataTemplates>` 按 Block 类型选 Presenter（`MainWindow.axaml` 消息模板 + DataTemplates 段）。
8. **滚动**：`Messages.CollectionChanged` → `OnMessagesChanged` → `ScrollToEnd()`（`Dispatcher.UIThread.Post(..., Background)` 延迟到布局后执行，`MainWindow.axaml.cs`）。

### 2.2 ASCII 图

```
用户点击会话 / 启动选中 Sessions[0]
        │
        ▼  SelectedSession 变化（CommunityToolkit 源生成器钩子）
OnSelectedSessionChanged(value)                     [MainWindowViewModel.cs]
        ├─ value == null → ShowEmptyState=true，结束（消息区清空/不加载）
        └─ value != null
             ├─ _suppressReload ? ─ 是 → 跳过加载（防 SelectedItem 回写重复加载）
             └─ 否 → LoadMessages(value)
                    │
                    ├─ nodes = Messages.ListBySession(session.Id)   [MessageRepository.cs]
                    │     ├─ SELECT messages  ORDER BY id        → MessageNode 本体
                    │     └─ SELECT parts     ORDER BY seq       → 按 id 填回 Parts
                    │
                    ├─ Messages.Clear()
                    ├─ path = SessionTree.PathTo(nodes, CurrentMessageId)
                    │     └─ 当前节点 ─ParentId→ … → 根，再 Reverse ⇒ 根…当前节点
                    │        （其他分支节点不在 path 中 → 不显示）
                    ├─ foreach node in path
                    │     └─ Messages.Add(new MessageViewModel(node))
                    │           └─ 助手：FlushRender() → Blocks = MarkdownRenderer.Render(Text)
                    └─ UpdateBranch(nodes, session)
                          └─ BranchNavigator.FindBranchNode + SessionTree.BranchPosition
                             ⇒ BranchText="i/n"、CanNavigateBranch
        │
        ▼  ObservableCollection<MessageViewModel> Messages
ItemsControl.ItemsSource                            [MainWindow.axaml]
        ├─ IsUser      → SelectableTextBlock(Text)
        └─ IsAssistant → ItemsControl(Blocks) → 按 DataTemplate 选 Presenter
        │
        ▼  CollectionChanged
OnMessagesChanged → ScrollToEnd()                   [MainWindow.axaml.cs]
```

### 2.3 关键规则小结

- **树→线性唯一规则**：`PathTo(CurrentMessageId)`，倒序回溯再反转；显示顺序即父子顺序（`SessionTree.cs::PathTo`）。
- **置顶（Pinned）**：只影响会话列表排序（`SessionRepository.List` 的 `ORDER BY pinned DESC, updated_at DESC`）与 `IsPinned` 显示，**不影响消息渲染**（`SessionRepository.cs`、`MainWindowViewModel.cs::TogglePin`）。
- **编辑提问**：`BeginEdit` 把原文塞进 `Composer`；发送时 `parent = editing.ParentId`，于是生成一条**新的兄弟提问 + 新回答**，原消息仍在树里但从当前路径上消失（视觉上「分叉」）（`MainWindowViewModel.cs::SendAsync` / `BeginEdit`；`SessionTree.cs::EditParent`）。
- **重新生成**：`RegenerateMessageAsync` 以 `message.ParentId` 为父重跑，产生同一提问下的新回答分支（`MainWindowViewModel.cs::RegenerateMessageAsync`）。
- **滚动位置**：现状每次会话切换都是 `Clear + Add`，滚动位置**不保留**，统一滚到底部；新界面若要保留每会话滚动位置，需另做 View 级状态（见 §7 后续增强）。

---

## 3. 实时流式渲染链路

### 3.1 流程（文字）

1. **入口**：`SendAsync`（新消息）/ `RegenerateAsync` / `RegenerateMessageAsync` / `RetryMessageAsync` 最终都调用 `RunAssistantAsync(sessionId, parentId, provider)`（`MainWindowViewModel.cs`）。
2. **请求组装**：`PromptAssembler.Build(session, SessionTree.PathTo(nodes, parentId))` 把当前路径转成 `ChatRequest`；**非 `Finished` 的助手节点不入上下文**，失败/被过滤节点不进入请求（`PromptAssembler.cs::Build`）。Thinking 能力由 `ProviderCapabilities.Thinking` 决定 `extra["thinking"]="enabled"`（`MainWindowViewModel.cs::RunAssistantAsync`）。
3. **建占位消息**：`NextId` 取新 id，插入一条 `Status=Wip` 的空助手 `MessageNode` 到库；`Messages.Add(new MessageViewModel(assistant) { Status = Wip })`——这是**整个流式过程中唯一一次向集合追加消息**（`MainWindowViewModel.cs::RunAssistantAsync`；`MessageRepository.cs::NextId/Insert`）。
4. **事件循环**：`await foreach (provider.StreamAsync(request, context, cts.Token).ConfigureAwait(true))`，按事件类型处理（`MainWindowViewModel.cs::RunAssistantAsync`；`ChatEvent.cs`：
   - `TextDelta` → `vm.Text += delta.Text` → `OnTextChanged` → `ScheduleRender()`；
   - `ThinkingDelta` → `vm.Thinking += thinking.Text` → `OnThinkingChanged` → 通知 `HasThinking`；
   - `UsageEvent` → `StatusText = "用量 …"`；
   - `Failed` → `vm.Status = Failed; ErrorText = failed.Message`；
   - `Done(reason)` → `vm.Status = MapStatus(reason)`（`EndTurn/StopSequence/ToolUse→Finished`，`MaxTokens→ContextLengthExceeded`，`ContentFilter→ContentFilter`，`Cancelled→Incomplete`，其余 `Failed`）。
5. **节流重渲染**：`OnTextChanged` 触发 `ScheduleRender()`——首次立即 `FlushRender()`，之后用 `DispatcherTimer(RenderThrottle=50ms)` 合并，每个窗口最多一次 `Blocks = MarkdownRenderer.Render(Text)`；`OnStatusChanged`（Wip→终态）会再次 `FlushRender()`，finally 还有一次兜底，保证不丢最后一段（`MessageViewModel.cs`）。
6. **停止/异常**：`StopCommand → _cts.Cancel()` 抛 `OperationCanceledException`；catch 中若 `cts.Token.IsCancellationRequested`（用户停）→ `Incomplete`，否则 → `Timeout` 并写 `ErrorText`；其他异常 → `Failed`。finally 把仍是 `Wip` 的强制改为 `Incomplete`，`IsStreaming=false`，`Messages.Update` 落库、`Sessions.Upsert` 更新 `CurrentMessageId`、`RefreshSessions`（`MainWindowViewModel.cs::Stop / RunAssistantAsync`）。
7. **UI 自动滚底**：因为每帧只改 `MessageViewModel` 属性、`Messages` 集合不变，ItemsControl 不重建项容器、不会整列表刷新；`ScrollToEnd` 只在集合 Add/Clear 时由 `OnMessagesChanged` 触发，**流式 token 增长不滚底**（`MainWindow.axaml.cs::OnMessagesChanged`）。这正是「用户手动上滚时不会被每帧拉回」的现状机制——用户上滚后继续生成的 token 不会打断阅读。

### 3.2 ASCII 图

```
[Send / Regenerate / RetryCommand]
        │
        ▼
RunAssistantAsync(sessionId, parentId, provider)         [MainWindowViewModel.cs]
        ├─ PromptAssembler.Build(路径=PathTo(parentId)) → ChatRequest
        ├─ 插入 Wip 助手节点 + Messages.Add(vm)   ★唯一一次追加新消息
        ├─ cts = new CTS(); IsStreaming=true; vm.Text/Thinking 从空开始
        │
        ▼  await foreach provider.StreamAsync(cts.Token).ConfigureAwait(true)
        │        [ChatEvent.cs / DeepSeekProvider.cs: content_block_delta]
        ├─ TextDelta      → vm.Text     += delta → OnTextChanged     → ScheduleRender()
        ├─ ThinkingDelta  → vm.Thinking += delta → OnThinkingChanged → HasThinking⇢Expander
        ├─ UsageEvent     → StatusText = "用量 …"
        ├─ Failed         → vm.Status=Failed, ErrorText=msg（HasError⇢错误条）
        └─ Done(reason)   → vm.Status=MapStatus(reason) → OnStatusChanged → FlushRender()
        │
        ▼  MessageViewModel 节流（避免每 token 重建 Inlines）
   首个增量立即 FlushRender；其后 DispatcherTimer 每 50ms 最多一次
        → Blocks = MarkdownRenderer.Render(Text) → OnBlocksChanged → HasBlocks
        │
        ▼  finally / Stop
   status 收尾 → IsStreaming=false → FlushRender() 兜底
        → Messages.Update(落库) + Sessions.Upsert + RefreshSessions
        → ScrollToEnd 仅因集合变化触发；token 帧不触发（用户上滚不被拉回）
```

### 3.3 「避免整列表刷新」与「上滚不强制拉回」的落地要点

- **现状已具备**：流式只改最后一条 `MessageViewModel` 的属性（`Text`/`Thinking`/`Status`/`Blocks`），`ObservableCollection` 不 Add/Remove ⇒ `ItemsControl` 项容器不重建；再叠加 50ms 渲染节流，避免每个 token 重建 Inlines（`MainWindowViewModel.cs::RunAssistantAsync`、`MessageViewModel.cs::ScheduleRender`）。
- **现状的缺口（需增强）**：`OnMessagesChanged` 对任何 `Add` 都无条件 `ScrollToEnd()`——用户正在上方阅读历史时，发送/重新生成产生的新消息会把视图猛地拉到底（`MainWindow.axaml.cs::OnMessagesChanged`）。
- **建议做法（在 `ConversationView` 中实现，保持 View 级纯视觉状态、不进 VM）**：
  1. 订阅 `ScrollViewer.ScrollChanged`，维护 `bool _atBottom`（`Offset + ViewportHeight ≥ ExtentHeight - 阈值`，阈值建议 48px）；
  2. `OnMessagesChanged` 改为「仅当 `_atBottom` 或新增项是当前用户自己发的消息时才 `ScrollToEnd()`」；否则显示「↓ 有新消息」提示按钮，点击后再滚底；
  3. 继续用 `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)` 把滚动排到布局之后，避免取到旧 `ExtentHeight`（沿用 `MainWindow.axaml.cs::ScrollToEnd` 的写法）。

---

## 4. Markdown 渲染策略

### 4.1 现有解析与显示管线

`MarkdownRenderer.Render(string?)`（Markdig + `UseAdvancedExtensions()`）把文本解析成与 UI 无关的 `IReadOnlyList<MarkdownBlock>`；`MessageViewModel.Blocks` 持有该列表；XAML 的 DataTemplate 按 Block 类型选控件；Presenter 把 `MarkdownInline` 映射为 Avalonia `Run/Span/LineBreak`（`MarkdownRenderer.cs`、`MarkdownPresenters.cs`、`MainWindow.axaml::<Window.DataTemplates>`）。

**现有支持的 Block 类型（8 种，均有 DataTemplate）**：

| Block 类型（定义于 `MarkdownRenderer.cs`） | DataTemplate 呈现（`MainWindow.axaml`） | 支持情况 |
|---|---|---|
| `MarkdownHeading(Level, Inlines)` | `MarkdownHeadingPresenter`（按 Level 设字号 22/19/17/15/14） | ✅ |
| `MarkdownParagraph(Inlines)` | `MarkdownParagraphPresenter`（`SelectableTextBlock`，Wrap） | ✅ |
| `MarkdownCodeBlock(Language, Code)` | `Border` + 横向 `ScrollViewer` + 等宽 `SelectableTextBlock`（NoWrap） | ✅ 保留语言字段，但**无语法高亮** |
| `MarkdownList(IsOrdered, Items)` | 嵌套 `ItemsControl` + `Marker`（"•" 或 "3."） | ✅ 支持有序起始序号 |
| `MarkdownQuote(Blocks)` | `Border` 左边框 + 嵌套 `ItemsControl` | ✅ 支持嵌套块 |
| `MarkdownTable(Rows, HasHeader)` | `MarkdownTablePresenter`（`Grid` 逐格生成，首行加粗） | ✅ 单元格按纯文本，列多时可能溢出 |
| `MarkdownThematicBreak` | 1px `Border` 分隔线 | ✅ |
| `MarkdownHtml(Text)` | `SelectableTextBlock`（muted）**按文本显示，不渲染 HTML** | ⚠️ 降级为纯文本 |

**行内类型（6 种，`MarkdownInlineKind`）**：`Text`、`Code`（等宽+高亮色）、`Strong`、`Emphasis`、`Link`（下划线+蓝色，`Url` 保留但当前模板只显示 label，未做可点击跳转）、`LineBreak`（硬换行）。软换行渲染为空格（`MarkdownRenderer.cs::ConvertInlines`；`MarkdownRendererTests.cs`）。

### 4.2 流式期与完成期的切换策略（建议）

> 现状：`IsStreaming=true` 时助手消息**仍然渲染 Markdown**，靠 50ms 节流降低频率（`MessageViewModel.cs::ScheduleRender`）。在 token 速率很高、文本很长时，反复 `MarkdownRenderer.Render` + Inlines 重建仍是热点。

建议引入显式双模式（在 `MessageItemView` 就地切换，不动解析器）：

| 阶段 | 判定 | 呈现 | 理由 |
|---|---|---|---|
| 流式生成中 | `IsStreaming == true`（即 `Status == Wip`，`MessageViewModel.IsStreaming`） | 用 `SelectableTextBlock Text="{Binding Text}"` 纯文本/极轻量渲染，**跳过 Block 解析** | token 逐字到达，此时块结构不稳定（半个代码围栏、半个表格行），解析既费 CPU 又会产生闪烁的半成品块 |
| 生成结束 | `IsStreaming == false`（`Status ∈ {Finished, Incomplete, Failed, …}`） | 切到 `ItemsControl ItemsSource="{Binding Blocks}"` 完整 Markdown | 文本已稳定，一次性解析得到正确块结构；只在终态解析 1 次 |

- **切换触发点**：`MessageViewModel.OnStatusChanged`（Wip → 终态时调用 `FlushRender()`，该方法已存在于 `MessageViewModel.cs`）以及 `RunAssistantAsync` finally 中的 `vm.FlushRender()`。XAML 用触发器按 `IsStreaming` 切换两个呈现控件的 `IsVisible`（参考现有 `IsVisible="{Binding IsUser}"` 用法，`MainWindow.axaml` 消息模板）。
- **性能理由**：把「每个节流窗口一次全树解析」降为「整个流生命周期 1 次解析」；避免半截 Markdown（未闭合的 ` ``` `、表格分隔行）造成的中间态重排与视觉跳动；与现有 `HasBlocks` / `FlushRender` 契约完全兼容，测试断言（`MarkdownRendererTests.cs`）不受影响。
- **兼容提示**：用户消息始终是纯文本（现状即如此），只有助手消息参与该切换；思维链（`Thinking`）一直用 `Expander + SelectableTextBlock` 纯文本呈现，不解析 Markdown（`MainWindow.axaml` 消息模板）。
- **后续增强**：若希望流式期保留有限样式，可用「纯文本 + 首帧延迟解析」的折中（例如仅在出现换行/围栏时解析），但要先测量，不为未证实的热点引入复杂度。

---

## 5. MessageItemView 操作栏设计

### 5.1 现有命令（可直接绑定，均定义于 `MainWindowViewModel.cs` 的 `[RelayCommand]`）

| 命令名（XAML 中绑定用） | 来源方法 | 适用范围 / 绑定条件 | 现有 XAML 用例 |
|---|---|---|---|
| `CopyMessageCommand` | `CopyMessageAsync(message)` | 全部消息；复制 `message.Text`（**原始 Markdown 文本**，非渲染后纯文本）；无剪贴板服务时仍更新 `LastCopiedText` | `MainWindow.axaml` 操作栏「复制」 |
| `BeginEditCommand` | `BeginEdit(message)` | 仅用户消息（方法内 `!message.IsUser` 直接返回）；把原文填入 `Composer` 进入编辑态 | 「编辑」（`IsVisible=IsUser`） |
| `RegenerateMessageCommand` | `RegenerateMessageAsync(message)` | 仅助手消息；以 `message.ParentId` 为父重跑，产生新分支 | 「重新生成」（`IsVisible=IsAssistant`） |
| `RetryMessageCommand` | `RetryMessageAsync(message)` | 仅助手且 `CanRetry`（`Failed/ContentFilter/Incomplete/Timeout/ContextLengthExceeded`，见 `MessageViewModel.CanRetry`） | 「重试」（`IsVisible=CanRetry`） |
| `PrevBranchCommand` / `NextBranchCommand` | `PrevBranch` / `NextBranch` → `MoveBranch(±1)` | 会话级分支导航；配合 `BranchText`（"i/n"）与 `CanNavigateBranch` 显示；`MoveBranch` 内部走 `RefreshSessions(sessionId)` 整链重载 | 会话工具栏（新界面可放到操作栏/顶栏） |
| `StopCommand` | `Stop()` → `_cts?.Cancel()` | 流式期间全局停止；配合 `ShowStop` / `IsStreaming` | Composer 区「停止」 |
| `RegenerateCommand` | `RegenerateAsync()` | 会话级「重新生成最后一条」，不接收 `MessageViewModel` 参数 | `deepseek-ui-design.md` §2 列为 ConversationView 绑定项 |

> 分支导航也可用 `SessionTree.BranchPosition` / `BranchNavigator.FindBranchNode` 只读判断当前是否处于分支（`MainWindowViewModel.cs::UpdateBranch`、`BranchNavigator.cs`）。

### 5.2 需要增强（当前 VM 没有现成命令/字段）

| 需求 | 现状 | 建议 |
|---|---|---|
| 点赞 / 点踩（DeepSeek 有） | VM 无对应属性或命令 | 新增消息级反馈状态与命令；先确认是否要求持久化（`ContentPart` 无反馈类型，需新表/新字段） |
| 单条消息级分支切换 | 只有会话级 `PrevBranch/NextBranch`，基于 `CurrentMessageId` 路径上最深分支（`BranchNavigator.cs`） | 若要在某条消息卡片上直接切它的兄弟分支，需新增带 `MessageViewModel` 参数的命令，并复用 `DescendToLatest` + `RefreshSessions` |
| 复制 Markdown 源码 vs 渲染纯文本 | `CopyMessageCommand` 复制 `message.Text`（原始 Markdown） | 若期望复制所见即所得文本，可复用 `MarkdownRenderer.ToPlainText(...)`（`MarkdownRenderer.cs`）提供另一命令 |
| 「复制代码块」按钮 | CodeBlock 模板只有文本展示，无复制按钮 | 块级命令或 View 交互（可给 `MarkdownCodeBlock` 加命令属性，需改渲染模型，属增强） |
| Thinking 展开/收起 | 已有 `IsThinkingExpanded` + `HasThinking` + `ThinkingHeader`（`MessageViewModel.cs`），由 `Expander` 双向绑定 | 无需新增命令，直接绑定即可 |
| 删除/收敛分支 | 无 | 需新增命令且涉及写库（树节点删除），属后续增强，需单独设计 |

---

## 6. 边界情况

| 场景 | 现状行为（证据） | 复刻注意事项 |
|---|---|---|
| **空消息 / 空白输入** | `SendAsync` 对 `Composer.Trim().Length == 0` 直接返回（`MainWindowViewModel.cs`）；`MarkdownRenderer.Render` 对 null/空白返回空列表（`MarkdownRenderer.cs`）；`MessageViewModel.HasText/HasBlocks` 控制可见性 | 空白助手消息会显示状态文案但无正文块；`MessageItemView` 要能优雅显示「生成中…/已停止」而不出现空白大块 |
| **超长文本** | `SelectableTextBlock TextWrapping=Wrap`；Markdown 逐块生成控件，`ItemsControl` **默认无虚拟化**（`MainWindow.axaml` 消息模板）；`ListBySession` 一次性读全量节点（`MessageRepository.cs`） | 超长会话可能一次性生成大量控件；建议后续引入 `VirtualizingStackPanel`（属增强，本文不改） |
| **代码块** | 围栏/缩进代码 → `MarkdownCodeBlock`，等宽字体 + 横向 `ScrollViewer`，`Language` 字段保留但不做语法高亮（`MainWindow.axaml` DataTemplate、`MarkdownRenderer.cs::FenceLanguage`） | 复刻时保留横向滚动；流式期半截围栏建议走 §4.2 纯文本模式避免闪烁 |
| **表格** | `MarkdownTable` → `MarkdownTablePresenter` 按最长行列数生成 `Grid`（列宽 `Star`），首行加粗（`MarkdownPresenters.cs`） | 列很多时会挤压/溢出；后续可加横向滚动容器 |
| **HTML** | `HtmlBlock` → `MarkdownHtml`，DataTemplate 用 `SelectableTextBlock` **按纯文本显示**，不渲染（`MarkdownRenderer.cs`、`MainWindow.axaml`） | 明确「HTML 降级为文本」；不要误以为支持富文本 |
| **图片 / 多模态** | ❌ **当前无图片渲染支持**：`ChatPart.Image`（`src/Chat.Abstractions/ChatContent.cs`）与 `PartKind.Image`（`ContentPart.cs`）虽在契约/持久化层定义，但 `MarkdownRenderer` 不产出图片 Block，`MainWindow.axaml` 无图片 DataTemplate，`MessageViewModel` 只读 Text/Thinking 两类 Part | 明确标注：历史消息与流式中的图片**都不会显示**；如需多模态，须新增 Block 类型 + DataTemplate + VM 取 Part 逻辑（后续增强） |
| **链接可点击** | `MarkdownInlineKind.Link` 保留 `Url`，但 Presenter 只用蓝色下划线 `Span` 显示 label，未接跳转（`MarkdownPresenters.cs`、`MainWindow.axaml`） | 复刻时保持「仅样式」现状，或增强为可点击 |
| **工具调用/结果 Part** | `PartKind.ToolCall/ToolResult` 会被 `PromptAssembler.ToParts` 转成请求，但不进入 `MessageViewModel` 的显示字段（`ContentPart.cs`、`PromptAssembler.cs`、`MessageViewModel.cs`） | 对话区不展示工具过程，符合当前产品范围 |
| **状态与错误** | `StatusText` 覆盖 Wip/Incomplete/Failed/Timeout/ContentFilter/ContextLengthExceeded；`ErrorText` + `HasError`（`MessageViewModel.cs`、`MainWindowViewModel.cs::OnErrorTextChanged`） | 操作栏「重试」只在 `CanRetry` 时显示；错误条由 `HasError` 驱动 |

---

## 7. 可验证的验收清单

**渲染一致性**
- [ ] 切换会话后，消息列表顺序与 `SessionTree.PathTo(CurrentMessageId)` 一致；非当前分支节点不显示（核对 `MessageRepository.ListBySession` + `SessionTree.cs`）。
- [ ] 用户消息显示纯文本、助手消息显示 `Blocks`；`Expander` 思考区仅在 `HasThinking` 时可见（对照 `MainWindow.axaml` 消息模板）。
- [ ] 8 类 Markdown Block 均有对应 DataTemplate，显示与旧 `MainWindow` 一致；HTML 以纯文本呈现、图片不渲染（`MainWindow.axaml::<Window.DataTemplates>`）。

**流式与节流**
- [ ] 发送后只 `Messages.Add` 一次助手占位消息；逐 token 期间操作栏/项容器不重建（可用 `RenderCount` 观测：`MessageViewModel.RenderCount`）。
- [ ] 高频 token 下 Markdown 重渲染次数远小于 token 数（默认节流 50ms，`MessageViewModel.RenderThrottle`；单测已覆盖类似断言思路）。
- [ ] 点击停止后 `Status=Incomplete`（「已停止」）；provider 自身超时显示 `Timeout` 与 `ErrorText`（`MainWindowViewModel.cs::RunAssistantAsync`）。
- [ ] 流式期间用户上滚，后续 token 不把视图拉回底部；新消息到达时的滚底策略符合 §3.3 实现。

**命令绑定**
- [ ] 操作栏「复制/编辑/重新生成/重试」分别绑定 `CopyMessageCommand`/`BeginEditCommand`/`RegenerateMessageCommand`/`RetryMessageCommand`，可见性与 `IsUser`/`IsAssistant`/`CanRetry` 一致。
- [ ] 分支导航显示 `BranchText`（"i/n"），`CanNavigateBranch=false` 时按钮禁用（`MainWindowViewModel.cs::UpdateBranch`）。

**边界与回归**
- [ ] 空白输入不发送；空助手消息不出现空白大块；超长文本横向/纵向滚动正常。
- [ ] 旧 `MainWindow` 及其回归测试（`tests/Chat.App.Tests/`）不受影响；`get_errors` 无新增错误。
- [ ] `tests/Chat.App.Tests/MarkdownRendererTests.cs` 全绿（解析是纯函数，迁移 DataTemplate 不应破坏）。

---

## 8. 范围划分

### 8.1 本次 UI 复刻必须做

1. 新建 `ConversationView` / `MessageItemView`（及设计文档其余 5 个控件），沿用同一个 `MainWindowViewModel` DataContext（`deepseek-ui-design.md` §2/§4）。
2. **迁移而非重写** Markdown 资源：把 `<Window.DataTemplates>` 与 Presenter 用法搬进新控件资源（`docs/architecture/deepseek-ui-design.md` §2 注、§5 步骤 2）。
3. `MessageItemView` 按角色分流：用户 `Text` 纯文本、助手 `Blocks`；绑定 `Thinking/HasThinking/IsThinkingExpanded`、`StatusText/ShowStatus`、`CanRetry`。
4. 操作栏绑定 §5.1 的现成命令（复制/编辑/重新生成/重试 + 会话级分支导航/停止）。
5. `ConversationView` 的 `ItemsControl` 绑定 `Messages`，实现自动滚底；按 §3.3 补「贴底才滚 + 新消息提示」，避免上滚被拉回。
6. 接入 §4.2 的流式双模式：`IsStreaming` 时纯文本、结束后切 Markdown（触发点复用 `FlushRender()`）。
7. 编译验证（`get_errors` 0 错误）+ 旧窗口回归 + 三断点下人工/截图验收。

### 8.2 后续增强（本次不做，需单独设计/评估）

- 消息级分支切换命令、点赞/点踩、分支删除/收敛（涉及 VM 新命令与写库设计）。
- 长列表 UI 虚拟化、每会话滚动位置保留。
- 流式期更精细的轻量样式（折叠代码/表格增量解析），先测量再优化。
- 代码块语法高亮与复制按钮、表格横向滚动、链接可点击跳转。
- 图片/多模态渲染（新增 Block + DataTemplate + Part 提取逻辑）、HTML 富文本渲染。
- 复制「渲染后纯文本」的独立命令。

---

## 附：主要证据文件索引

- 持久化与仓储：`src/Chat.Storage/ChatDatabase.cs`、`MessageRepository.cs`、`SessionRepository.cs`
- 领域模型与树算法：`src/Chat.Core/Session.cs`、`MessageNode.cs`、`ContentPart.cs`、`SessionTree.cs`、`PromptAssembler.cs`
- VM 与展示模型：`src/Chat.App/ViewModels/MainWindowViewModel.cs`、`MessageViewModel.cs`
- 分支与滚动：`src/Chat.App/Services/BranchNavigator.cs`、`src/Chat.App/Views/MainWindow.axaml.cs`
- 流式契约与 Provider：`src/Chat.Abstractions/ChatEvent.cs`、`IChatProvider.cs`、`ChatContent.cs`、`src/Chat.Kernel/ProviderHost.cs`、`src/Chat.Providers.DeepSeek/DeepSeekProvider.cs`
- Markdown 渲染：`src/Chat.App/Rendering/MarkdownRenderer.cs`、`MarkdownPresenters.cs`、`src/Chat.App/Views/MainWindow.axaml`、`tests/Chat.App.Tests/MarkdownRendererTests.cs`
