# DeepSeek 风格聊天界面 —— UI 设计说明书

> 目标：在 `Chat.App` 中用 SukiUI 控件复刻 DeepSeek 网页端（https://chat.deepseek.com/）的布局与交互。
> 本文只做设计与拆分，不含实现代码。所有结论都基于当前仓库事实与参考页面实测。

---

## 0. 现状事实（设计前提）

| 事实 | 证据 | 对设计的影响 |
|---|---|---|
| 启动窗口是 `HomePage`，且仍是 SukiUI 模板空壳 | `App.axaml.cs` 中 `desktop.MainWindow = new HomePage()`；`HomePage.axaml` 只有 `SukiSideMenu` 占位 | 需要一个真正的"外壳"控件替代它 |
| `MainWindow` 是功能完整的旧界面，测试仍在用 | `MainWindow.axaml` 有完整会话/消息/导入导出布局；测试项目引用它 | 旧窗口保留给回归测试，不删除、不破坏 |
| 业务逻辑已齐备，无需重做 | `MainWindowViewModel` 用 `[ObservableProperty]`/`[RelayCommand]` 实现了会话、流、导入导出、设置等 | 新界面复用同一 VM，只换 View |
| VM 已有 DeepSeek 所需属性 | `GroupedSessions`（今天/昨天/过去7天/更早）、`IsDeepThinkEnabled`、`IsWebSearchEnabled`、`Composer`、`ShowSend`/`ShowStop`、`NewSessionCommand` | Composer 开关、分组列表可直接绑定 |
| 包版本 | Avalonia 12.1.6 / SukiUI 7.0.1 / CommunityToolkit.Mvvm 8.4.2 | 用 SukiUI 7 的控件集；MVVM 用 Toolkit 即可 |

---

## 1. 界面分区（一个界面包含哪几部分）

按 DeepSeek 实测页面，界面由 **6 个功能区域** 组成：

```
┌──────────────────────────────────────────────────────────────────┐
│ ① TopBar 顶栏        ☰               新建对话 ✚        ⚙/头像      │
├────────────────┬─────────────────────────────────────────────────┤
│                │                                                 │
│ ② Sidebar      │  ④ ContentArea（主内容区）                        │
│ 侧边栏          │                                                 │
│                │   · 空状态 → ⑤ Welcome                            │
│  🔍 搜索会话    │   · 有会话 → ⑥ Conversation（消息流）             │
│                │                                                 │
│  ＋ 开启新对话   │      [ 消息列表 ScrollViewer ]                    │
│                │                                                 │
│  今天          │      ┌─────────────────────────────────┐        │
│   · 会话1       │      │ ③ Composer 输入框               │        │
│   · 会话2       │      │  [ 多行输入                  ] │        │
│  昨天          │      │  深度思考  智能搜索   📎    ⬆   │        │
│   · 会话3       │      └─────────────────────────────────┘        │
│                │                                                 │
│  👤 用户 / 设置 │                                                 │
└────────────────┴─────────────────────────────────────────────────┘
```

| 编号 | 区域 | 职责 | 关键元素 |
|---|---|---|---|
| ① | TopBar 顶栏 | 全局操作与窗口控制 | 汉堡菜单（切换侧边栏）、会话标题/模型名、新建对话、主题切换、窗口最小化/关闭 |
| ② | Sidebar 侧边栏 | 会话导航 | 搜索框（防抖，复用 VM 的 `SearchText`）、"开启新对话"按钮、按日期分组的会话列表（`GroupedSessions`）、底部账户/设置入口 |
| ③ | Composer 输入框 | 消息输入与发送 | 多行 `TextBox`（`Composer`）、深度思考开关（`IsDeepThinkEnabled`）、智能搜索开关（`IsWebSearchEnabled`）、附件按钮、发送/停止按钮（`ShowSend`/`ShowStop`）、发送命令（`SendCommand`） |
| ④ | ContentArea 主内容区 | 承载两种状态 | 用 `ContentControl`/`Panel` 在 Welcome 与 Conversation 间切换（`ShowEmptyState`） |
| ⑤ | Welcome 欢迎页 | 新会话首屏 | 居中 logo + "下午好，我能帮什么忙吗？" + 居中 Composer + 建议问题区（可选） |
| ⑥ | Conversation 对话区 | 消息展示 | 消息列表（`ItemsControl` + `MessageViewModel`）、每条消息含 Markdown 渲染与操作栏（复制/重新生成/点赞点踩）、自动滚动到底部 |

> **注意**：③ Composer 是**复用控件**，同时出现在 ⑤ Welcome（居中）与 ⑥ Conversation（贴底）。它必须是独立 UserControl，不能写死在某一个页面里。

---

## 2. 控件拆分（分成几个控件，每个控件做什么）

严格遵循"不能都在一个控件内"——每个区域一个 `UserControl`，外壳只做编排：

| 控件（建议文件名，Views/ 下） | 类型 | 职责 | 绑定的 VM 成员（复用现有） |
|---|---|---|---|
| `ChatShellView` | 外壳 UserControl（替代 `HomePage` 作为启动窗口内容） | 顶层 Grid 布局；持有侧边栏展开状态；按断点切换响应式视觉状态；居中约束内容宽度 | `ShowEmptyState`、`StatusText` |
| `TopBarView` | UserControl | 汉堡按钮（通知外壳切换侧边栏）、标题/模型名、新建对话、主题、窗口按钮 | `SelectedSession.DisplayTitle`、`Model`、`NewSessionCommand` |
| `SidebarView` | UserControl | 搜索框、新建对话、分组会话列表、置顶标记、底部设置入口 | `SearchText`、`GroupedSessions`、`SelectedSession`、`NewSessionCommand` |
| `WelcomeView` | UserControl | 居中 logo、问候语、居中放置 `ComposerView` | `Composer` 及 Composer 全部开关 |
| `ConversationView` | UserControl | 消息列表滚动容器、自动滚底、贴底放置 `ComposerView` | `Messages`、`RegenerateCommand`、`IsStreaming` |
| `ComposerView` | UserControl（复用） | 输入区全部交互：输入框、两个模式开关、附件、发送/停止 | `Composer`、`IsDeepThinkEnabled`、`IsWebSearchEnabled`、`SendCommand`、`StopCommand`、`ShowSend`/`ShowStop` |
| `MessageItemView` | UserControl（数据模板） | 单条消息气泡：用户/助手区分、Markdown 渲染、操作栏（复制/重新生成/分支导航） | `MessageViewModel` 各字段 |

**编排关系**：
- `ChatShellView` = 根 Grid（两列：Sidebar / 主区）。主区 = Grid（两行：TopBar / ContentArea）。
- ContentArea 内同时放 `WelcomeView` 与 `ConversationView`，用 `IsVisible` + `ShowEmptyState` 切换（简单可靠，优于动态 DataTemplate 切换，便于保留滚动位置）。
- `WelcomeView` 与 `ConversationView` 各自内嵌一个 `ComposerView`；Composer 的绑定全部指向同一个 VM，因此状态天然同步。
- 侧边栏折叠状态 **属于外壳的纯视觉状态**，不进 VM（详见 §4）。

**为什么不引入额外控件层**：
- 消息 Markdown 渲染已有 `Chat.App/Rendering/` 的 Presenter 与 `MainWindow.axaml` 的 `DataTemplates`——原样搬到 `ChatShellView`/`ConversationView` 的资源里即可，不重写。
- 导入/导出、设置弹窗是低频功能，放入外壳的浮层（`SukiDialog`/`Popup`），不作为常驻控件拆分，避免主布局复杂化。

---

## 3. 不同分辨率与布局下的实现

### 3.1 三个断点

| 断点 | 宽度范围 | 侧边栏 | 内容区 | Composer |
|---|---|---|---|---|
| 宽屏 Wide | ≥ 1100 | 固定展开，260px | 最大宽度 ~860px 居中，两侧留白 | 与内容区同宽，贴底 |
| 中屏 Medium | 720–1099 | 默认折叠为图标条（60px），可展开覆盖内容 | 占满剩余宽度 | 贴底 |
| 窄屏 Narrow | < 720 | 完全隐藏，汉堡按钮唤出**覆盖式抽屉** + 半透明遮罩；选中会话后自动收起 | 全宽，问候语字号缩小 | 贴底、内边距收紧 |

### 3.2 实现方式（Avalonia 12 的具体手段）

- **尺寸 → 视觉状态的桥接**：Avalonia XAML 没有 WPF 触发器那么强的原生断点支持。在 `ChatShellView` 的 code-behind 订阅 `SizeChanged`（或绑定 `Bounds.Width`），把宽度映射为 `Classes`：`.wide` / `.medium` / `.narrow`。XAML 用 `Style Selector` 根据 class 改 Grid 列宽、侧边栏宽度、内容区最大宽度与字号。
  - 这只是**纯视觉状态**，不含业务逻辑，放在 View 的 code-behind 是可接受的（MVVM 不禁止视图自身的布局逻辑）。
  - 阈值集中定义为常量，避免散落魔法数字。
- **侧边栏展开/收起**：外壳持有一个 `bool`（侧边栏是否展开），通过 `Classes` 切换；宽屏时展开为固定列，窄屏时展开为覆盖层（把 Sidebar 放到 Grid 同一单元格的上层 + 遮罩 `Border`）。
- **内容居中**：主区用 `Grid` + `MaxWidth=860` 的居中 `ContentPresenter`（水平居中 `HorizontalAlignment=Center`），宽屏留白、窄屏自动收缩。
- **滚动与贴底**：Conversation 用 `DockPanel`，消息列表 `ScrollViewer` 占 `*`，`ComposerView`  Dock=Bottom；Welcome 用垂直居中的 `StackPanel`。两者不共用同一个布局，避免"居中"与"贴底"互相打架。
- **断言/验证点**：三个断点下，侧边栏可见性、内容区宽度、Composer 位置符合上表；窗口从宽拖到窄再拖回，状态可恢复。

---

## 4. MVVM 是否需要框架

**结论：不引入 Prism / ReactiveUI / MVVM Light 等重型框架。用现有的 CommunityToolkit.Mvvm（已引用 8.4.2） + Avalonia 原生绑定即可。**

理由：
1. **已有基础设施**：`MainWindowViewModel` 已用 `[ObservableProperty]`、`[RelayCommand]`、`ObservableCollection` 实现全部业务，源生成器零反射、性能与可维护性都够。
2. **项目规模不匹配**：单窗口、单一会话编排、无区域导航（Region）、无复杂响应式数据流；重型框架带来的模块化/事件聚合/依赖容器在这里是过度设计，违背"简单优先"。
3. **SukiUI 本身就是 Avalonia 控件库**：与 CommunityToolkit.Mvvm 兼容，无需适配层。

具体约定：
- **View**：7 个 UserControl 只负责布局与视觉状态（尺寸断点、侧边栏展开动画、滚动），不写业务逻辑。
- **ViewModel**：沿用 `MainWindowViewModel`（可重命名为 `ChatShellViewModel` 以贴合新外壳，或保留原名以减少改动——**建议保留原名**，改动最小、测试不破）。各子控件通过绑定共享同一个 DataContext（Avalonia 的 DataContext 继承天然支持）。
- **需要新增的少量纯 UI 状态**（侧边栏展开、断点 class）留在 View 的 code-behind / `Classes`，**不污染 VM**。
- **新建对话/发送消息等命令**全部复用 VM 已有的 `NewSessionCommand`、`SendCommand` 等，不在 View 里写事件处理（文件选择等必须交互除外，可用 `Interaction`/行为或最小 code-behind）。
- **Toolbar 的"新建对话"按钮**绑定 `NewSessionCommand`，与侧边栏的同名按钮共用同一命令，保证行为一致。

> 一句话：不是"原生裸写"，而是 **Avalonia 绑定 + CommunityToolkit.Mvvm 源生成器** 这一轻量组合，它已经足够，且是当前仓库的既定路线。

---

## 5. 落地顺序（设计确认后再实施）

1. 新建 7 个 UserControl 的骨架（XAML + .cs），外壳 `ChatShellView` 先搭好两列/两行 Grid。
2. 把 `MainWindow.axaml` 里现成的 Markdown `DataTemplates` 与样式迁移到外壳资源中（复用，不重写渲染）。
3. 逐个填充 Sidebar → Welcome → Composer → Conversation → MessageItem → TopBar 的 XAML 与绑定。
4. 实现尺寸断点与侧边栏折叠的视觉状态。
5. `App.axaml.cs` 启动改为新外壳（保留 `MainWindow` 供测试）。
6. 编译验证（get_errors 0 错误）+ 三个断点下人工/截图验收。

---

## 6. 验收标准（可验证）

- [ ] 启动时展示 DeepSeek 风格首屏：居中 logo + 问候语 + 输入框 + 深度思考/智能搜索开关。
- [ ] 侧边栏会话按"今天/昨天/过去7天/更早"分组显示，点击会话切换右侧内容。
- [ ] 输入框发送后显示用户/助手消息气泡，Markdown 正常渲染，生成中显示停止按钮。
- [ ] 三个断点（≥1100 / 720–1099 / <720）布局符合 §3 表格；窗口缩放可平滑恢复。
- [ ] 深度思考/智能搜索开关、新建对话、搜索框、设置入口均可用。
- [ ] 旧 `MainWindow` 及其回归测试不受影响；`get_errors` 无新增错误。
