# 消息对话区复刻方案 —— 基于 DeepSeek 网页实测 DOM

> 本文只做调研结论 + 实施方案，不含实现代码。所有样式数字/颜色来自对 `chat.deepseek.com` 的 `getComputedStyle` 实测（视口 1440×900 / 1920×1080，已登录账号的历史会话，未发送任何新消息）。
> 取证报告（含完整 SVG path）：见浏览器会话资源；设计图：`docs/diagrams/deepseek-message-list.excalidraw`。
> 现有渲染链路：`docs/architecture/deepseek-ui-rendering-design.md`（树→线性、流式节流、Markdown 两层渲染）。

---

## 1. 调研结论：网页真实样式（实测值）

### 1.1 容器与布局

| 项 | 实测值 |
|---|---|
| 内容列宽 | **固定 752px**，1440 与 1920 视口下均为 752、水平居中（非百分比/max-width） |
| 消息间距 | **50px**（相邻消息下一条 top − 上一条 bottom） |
| 容器 padding | 0 |
| 滚动容器 | 虚拟列表 `display:flex; flex-direction:column`，`overflow-y:auto` |
| 消息区背景 | 透明，透出 AppBg |

### 1.2 用户消息

| 项 | 实测值 |
|---|---|
| 对齐 | 靠右，气泡右缘与 752px 列右缘重合 |
| 背景 / 文字 | `#EDF3FE` / `#0F1115` |
| 圆角 / padding | `22px` / `10px 16px` |
| 最大宽 | `calc(100% - 88px)`（≈664px） |
| 字号 / 行高 | `16px / 24px`，可选中 |
| 操作栏 | **未观察到**（hover 也不出现编辑/复制） |
| 头像 | **未观察到** |

### 1.3 助手消息

| 项 | 实测值 |
|---|---|
| 对齐 / 占位 | 靠左，占满 752px，padding 0 |
| 背景 / 文字 | 透明 / `#0F1115` |
| 字号 / 行高 | `16px / 28px`，正文距顶 `margin:10px 0 0` |
| 段间距 | `p { margin: 0 0 16px }` |
| 头像 | **未观察到**（无 img/logo 节点） |
| AI 免责声明 | 文末「本回答由 AI 生成，内容仅供参考，请仔细甄别」，`margin:16px 0 0` |
| 引用来源 chip | 「45 个网页」等，白底 `radius:100px`、`padding:4px 10px` |

### 1.4 思考链（重点）

**标题原文**：`已思考（用时 N 秒）`（实测纠正：不是"已深度思考"）。

| 状态 | 实测值 |
|---|---|
| 折叠态 | 高度 **34px**；标题 `#61666B`、`16px/28px`；右向 chevron；`cursor:pointer`、`user-select:none` |
| 展开态 | 文字 `#61666B`、`14px/24px`；`padding: 5px 0 5px 22px`；**无底色、无边框**；`max-height:none` 全量展开不滚动；下向 chevron |
| 动画 | 父容器 `transition:all`；折叠时整体高度 1737px → 34px |
| chevron | 折叠/展开是**两套不同的 SVG path**（不是旋转），viewBox `0 0 14 14` |
| 特殊块 | 思考流中混有「搜索到 45 个网页」「浏览 3 个页面」chip（34px 高） |

### 1.5 操作栏（重点）

| 项 | 实测值 |
|---|---|
| 触发 | hover 显示，`opacity 0→1`，`0.2s cubic-bezier(.4,0,.2,1)` |
| 位置 | 消息末尾下方，**文档流内**（非浮层），左对齐于 752px 列 |
| 按钮 | **28×28px**、圆角 4096px（圆形）、图标 viewBox `0 0 16 16` |
| 颜色 | 默认图标 `#81858C`；hover 背景 `rgba(38,49,72,0.06)` |
| 间距 | gap `10px` |
| 顺序 | **复制 → 重新生成 → 赞 → 踩 → 朗读 → 分享** |
| 用户消息操作栏 | **未观察到** |

### 1.6 Markdown 元素

| 元素 | 实测值 |
|---|---|
| 代码块 | 背景 `#F9FAFB`、圆角 12、`margin:16px 0 11px`；banner 白底 40px（左语言名，右复制/下载按钮 13px `#61666B`）；pre `padding:16px`、13px/22px、等宽字体、横向滚动 |
| 行内代码 | 背景 `#EBEEF2`、圆角 6、文字 `#61666B`、12.25px/22px、`padding:0 5px` |
| 无序/有序列表 | `padding-left:18px`、`margin:4px 0`（ol 底 12px）、16px/28px |
| 引用块 | `border-left:2px solid #ADB2B8`、`padding-left:14px`、`margin:16px 0 0`、**无背景** |
| 表格 | 表体可超宽（实测 1102px > 752px），外层横向滚动；`th/td padding:10px 16px 10px 0`、15px/25px；无可见边框、表头无背景 |
| 链接 | `#3964FE`；h3 `20px/30px`、`margin:32px 0 16px` |

### 1.7 未取证项（不臆造）

深色主题下的消息区、窄屏响应式、实时生成中的"停止/思考中"状态：历史会话无此状态，本次未触发（未发消息），**方案按现有机制保留，不作为本次复刻目标**。

---

## 2. 现状差距（当前实现 vs 实测）

| 区域 | 当前实现 | 实测要求 | 差距 |
|---|---|---|---|
| 内容列宽 | `content-frame` MaxWidth 860、MinWidth 600 | 固定 752 居中 | 宽度不符 |
| 消息间距 | 无（仅 `Border.msg` margin 10） | 50px | 差很多 |
| 用户气泡 | 左对齐、无背景（MessageItemView 只是 StackPanel） | 靠右、#EDF3FE、圆角22、padding10/16、max 664 | 完全不符 |
| 助手正文 | 16px 默认行高 | 16/28、段间距16、距顶10 | 行高/间距不符 |
| 思考链 | 标准 `Expander`（有边框/背景、标题"思考过程"/"思考中…"） | 标题"已思考（用时 N 秒）"、34px、无底色无边框、缩进22、两套 chevron | 样式/文案/交互均不符 |
| 操作栏 | 4 个文字按钮（复制/编辑/重新生成/重试）常驻 | 6 个 28px 圆形图标按钮、hover 显示、gap10 | 数量/样式/触发全不符 |
| 代码块 | `#CodeBg` 圆角6、无 banner、12px | 圆角12、40px banner、13px、复制/下载 | 结构缺失 |
| 行内代码 | 前景 `#E6C07B`、无背景 | 背景 #EBEEF2、圆角6、前景 #61666B | 不符 |
| 引用块 | `BorderThickness=3,0,0,0`、色 BorderSubtle | 2px #ADB2B8、左 padding14、无背景 | 数值不符 |
| 表格 | `Grid` 每格 1px 边框 + 表头深灰背景 | 无边框、表头透明、超宽横滚 | 不符 |
| 免责声明 | 仅 Welcome 有（对话区无） | 每条助手消息末尾都要有 | 缺失 |

---

## 3. 实施方案（改动清单）

### 3.1 数据层 / ViewModel（`MessageViewModel`，最小改动）

1. **思考耗时**：新增属性把现有 `Thinking` 与耗时展示解耦。
   - `ThinkingHeaderText`：完成时 `已思考（用时 N 秒）`，流式中 `思考中…`。
   - N 的来源：优先用助手节点 `CreatedAt - 对应提问 CreatedAt`（差值为秒）；无提问时间时退化为 `1`。**不新增数据库字段**。
2. **操作栏可见性**：当前操作栏常驻，改为 hover 触发——纯 View 行为（见 3.3），VM 不加属性。
3. **免责声明**：固定文案放 `AppBranding.Disclaimer`（已有类似文案，统一为网页原文「本回答由 AI 生成，内容仅供参考，请仔细甄别」）。
4. 其余 `Text/Thinking/Blocks/Status/CanRetry` 与命令（`CopyMessageCommand`/`BeginEditCommand`/`RegenerateMessageCommand`/`RetryMessageCommand`）全部保留，不改渲染链路。

### 3.2 图标资源（`AppIcons` 追加）

从取证报告的 SVG path 追加真实图标（viewBox 均为 `0 0 16 16`，除 chevron 为 `0 0 14 14`）：
- `Copy`、`Regenerate`、`ThumbUp`、`ThumbDown`、`ReadAloud`（3 path）、`Share`
- `ThinkingChevronRight`、`ThinkingChevronDown`（**两套独立 path**）
- `ThinkingBrain`（思考链头部新版脑图，区别于输入框那个）
- 代码块 banner 的复制/下载复用 `Copy` / 新增 `Download`

### 3.3 视图层

**`ConversationView.axaml`**
- ScrollViewer 内 `ItemsControl` 宽度固定 752、水平居中（改 `ContentFrame` 约束）。
- `ItemsControl.ItemContainerTheme` 或外包裹设置 `Margin` 实现消息间距 50px。

**`MessageItemView.axaml`（重做）**
- 根 `Border` 按角色分流：
  - 用户：`HorizontalAlignment=Right`、`MaxWidth=664`、`Background=#EDF3FE`、圆角22、padding10/16、16/24，只显示 `Text`；**去掉用户操作栏**。
  - 助手：占满宽、透明底、正文 16/28、段距 16。
- **思考链**：弃用标准 `Expander`，用 `ToggleButton` + 自定义控件模板：
  - 头部 34px 高、`#61666B` 16/28、文案绑 `ThinkingHeaderText`；右侧 chevron 按 `IsThinkingExpanded` 切换两套 path（用两个 Path + `IsVisible` 互斥）。
  - 展开内容：`Border` 无背景无边框、`Padding=5,0,5,22`、文字 `#61666B` 14/24、直接全量显示（不设 MaxHeight、不滚动）。
- **操作栏**：`StackPanel` 6 个 28px 圆形按钮放消息末尾；默认 `Opacity=0`，鼠标进入消息根 Border 时置 1（code-behind 处理 PointerEnter/Leave，纯 UI 状态）。图标用 3.2 的真实 SVG；赞/踩/朗读/分享暂以 ToolTip 标注（无对应 VM 命令，见 §4）。
- **免责声明**：助手消息末尾加 `TextBlock`，文案 `AppBranding.Disclaimer`，`margin:16,0,0`，12px 次要色。

**Markdown DataTemplates（在 `ChatShellView.axaml` 资源中调整）**
- 代码块：外层圆角 12、背景 `CodeBg`；新增 40px banner（语言名 + 复制/下载图标）；pre 13px 等宽、`padding:16`、横向滚动。
- 行内代码：`MarkdownInlineBuilder` 中把 Code 的 Run 改为带背景/圆角/前景 #61666B（Avalonia Run 不支持背景，改用 `Span` 或自定义 Inline；若不支持则降级为前景色 + 字体区分，并在方案标注）。
- 引用块：`BorderThickness=2,0,0,0`、`BorderBrush=#ADB2B8`、`Padding=14,0,0,0`、无背景。
- 表格：`MarkdownTablePresenter` 去掉每格边框与表头深灰背景，外层套横向 ScrollViewer。
- 标题：h3 20/30、`margin:32,0,16`（同步调整 `MarkdownHeadingPresenter` 字号表）。

### 3.4 主题配色（纳入现有主题字典）

新增语义键（两套主题都给值）：
- `UserBubbleBg`：亮 `#EDF3FE` / 暗 `#2C2C2E`（已有 `UserBubbleBrush`，沿用）
- `ThinkingText`：亮 `#61666B` / 暗 `#ADB2B8`（网页暗色实测 secondary/tertiary）
- `ActionIcon`：亮 `#81858C` / 暗 `#ADB2B8`
- `ActionHoverBg`：亮 `rgba(38,49,72,0.06)` / 暗 `rgba(255,255,255,0.08)`
- `CodeBannerBg`：亮 `#FFFFFF` / 暗 `#2C2C2E`
- `InlineCodeBg`：亮 `#EBEEF2` / 暗 `#2C2C2E`
- `QuoteBorder`：亮 `#ADB2B8` / 暗 `#545557`
- `LinkColor`：亮 `#3964FE` / 暗 `#5686FE`

### 3.5 自动滚动（沿用 + 一处增强）

`ConversationView` 现有 `Messages.CollectionChanged → ScrollToEnd` 保留；新增：助手消息 `Text` 变化时若用户处于"贴底"状态（距底 < 80px）则跟随滚动，用户主动上滚则不拉回。纯 View 逻辑，不进 VM。

---

## 4. 命令映射与能力边界（务实说明）

| 操作栏按钮 | VM 现有命令 | 本次处理 |
|---|---|---|
| 复制 | `CopyMessageCommand` | 直接绑定 |
| 重新生成 | `RegenerateMessageCommand` | 直接绑定 |
| 赞 / 踩 | 无 | 按前文结论：API 无回传通道，做成**仅视觉反馈的本地切换**（图标点击后高亮，不写库）或暂不绑定图标（灰显）。建议本地切换。 |
| 朗读 | 无 | 需要 TTS 依赖，本次做灰显 + ToolTip「暂未支持」 |
| 分享 | 无 | 本地无对应能力，灰显 + ToolTip「暂未支持」 |

用户消息操作栏实测"未观察到"，因此**移除**当前的用户"编辑/复制"按钮（编辑功能入口保留在 VM，后续若要恢复可放右键菜单）。

---

## 5. 可验证验收清单

- [ ] 内容列固定 752px 居中；相邻消息间距 50px（截图 + 视觉比对）
- [ ] 用户消息靠右、圆角22、#EDF3FE、10/16 padding、16/24，无操作栏
- [ ] 助手消息占满、透明底、16/28、段距16、距顶10，无头像
- [ ] 思考链折叠文案「已思考（用时 N 秒）」、34px、#61666B；展开无底色无边框、缩进22、14/24、全量不滚动；两套 chevron 切换
- [ ] 操作栏 6 个 28px 圆形图标、gap10、hover 显现、左对齐；顺序正确
- [ ] 代码块 40px banner + 圆角12 + 13px；行内代码 #EBEEF2 圆角6；引用块 2px #ADB2B8；表格横滚；链接 #3964FE
- [ ] 助手消息末尾免责声明
- [ ] 明暗两套配色跟随主题切换
- [ ] 贴底时流式文本跟随滚动，上滚时不被拉回
- [ ] `get_errors` 无错误；新增/更新 headless 测试覆盖思考链切换、操作栏 hover、角色分流

---

## 6. 实施顺序（确认后执行）

1. `AppIcons` 补齐操作栏/思考链 SVG path
2. `App.axaml` 补语义画刷；`AppBranding` 补免责声明原文
3. `MessageViewModel` 加 `ThinkingHeaderText`（耗时计算）
4. 重做 `MessageItemView.axaml`（角色分流 + 思考链 + 操作栏 + 免责声明）
5. 调整 `ChatShellView.axaml` Markdown DataTemplates + `MarkdownPresenters.cs` / `MarkdownTablePresenter`
6. `ConversationView` 容器 752 + 间距 50 + 贴底滚动
7. 编译 → headless 测试 → 亮/暗截图比对

---

## 7. 风险与待确认

1. **行内代码背景**：Avalonia 的 `Run` 不支持 Background，需用 `Span`/自定义 Inline 实现；若渲染受限则降级为前景色+等宽字体，届时会说明。
2. **思考耗时 N**：网页用真实推理耗时；本地数据库无此字段，方案用 `CreatedAt` 差值估算。若你要求精确到秒的真实耗时，需要新增字段并改 provider（**本次不做**）。
3. **生成中/停止态**：未取证（未发消息），沿用现有"纯文本流式 + 停止按钮"，不视为本次复刻缺口。
4. **虚拟列表**：网页用虚拟滚动；本项目消息量小，继续用 ItemsControl，不做虚拟化（性能足够，且能保留现有滚动/选中行为）。
