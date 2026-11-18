# Chat.Console（llmchat-cli）

无 GUI 的命令行客户端：直接复用 `Chat.Kernel` 的插件加载与三个内置 provider 插件，
不引用 Avalonia，因此可以在没有 X server / DISPLAY 的环境里跑真实对话（CI、服务器、容器）。

## 构建与运行

```bash
dotnet build src/Chat.Console/Chat.Console.csproj -m:1 -nodeReuse:false
dotnet src/Chat.Console/bin/Debug/net10.0/llmchat-cli.dll --help
```

构建时会把三个内置插件复制到输出目录的 `plugins/<id>/<version>/`，与桌面版布局一致。
用户插件仍从数据目录（默认按平台：Windows `%APPDATA%\LLMChat`、macOS
`~/Library/Application Support/LLMChat`、Linux `$XDG_DATA_HOME/LLMChat` 或
`~/.local/share/LLMChat`）下的 `plugins/` 加载，可用 `--data-dir` 覆盖。

## 用法

```text
llmchat-cli [选项]

--provider <id>   插件 id，默认 chat.provider.deepseek
--model <id>      模型 id；省略时用 provider 的默认模型或 --set 配置
--api-key <key>   API Key；省略时读 DEEPSEEK_API_KEY / OPENAI_API_KEY / OPENCODE_API_KEY
--base-url <url>  覆盖 base URL（deepseek/openai/opencode）
--system <text>   system 提示
--message <text>  单轮消息；省略时进入交互 REPL（stdin 重定向时读完全部输入）
--thinking        请求开启思维链（provider 支持时）
--set key=value   附加设置，可重复
--list-models     列出模型后退出
--data-dir <dir>  数据目录
--verbose         provider 调试日志
-h, --help        帮助
```

`--api-key` / `--set <id>.apiKey` 的优先级高于环境变量。正文只写 stdout，
思维链（`[thinking]`）、工具与用量摘要、错误与结束原因写 stderr，方便管道处理。

## 示例

```bash
# DeepSeek（环境变量提供密钥）
DEEPSEEK_API_KEY=sk-... dotnet src/Chat.Console/bin/Debug/net10.0/llmchat-cli.dll --message "你好"

# 交互式多轮；行尾反斜杠续行
DEEPSEEK_API_KEY=sk-... dotnet src/Chat.Console/bin/Debug/net10.0/llmchat-cli.dll

# OpenAI 兼容通道的 GLM / MiniMax 预设
OPENAI_API_KEY=... dotnet src/Chat.Console/bin/Debug/net10.0/llmchat-cli.dll \
  --provider chat.provider.openai --set openai.preset=minimax --model MiniMax-M2 --message "你好"

# OpenCode Go
OPENCODE_API_KEY=... dotnet src/Chat.Console/bin/Debug/net10.0/llmchat-cli.dll \
  --provider chat.provider.opencodego --model deepseek-v4-pro --message "你好"

# 本地 OpenAI 兼容 mock（不需要真实网络）
dotnet src/Chat.Console/bin/Debug/net10.0/llmchat-cli.dll \
  --provider chat.provider.openai --base-url http://127.0.0.1:8080/v1 --api-key test --model mock --message "ping"
```

## 输出与退出码

| 事件 | 输出 |
| --- | --- |
| `TextDelta` | stdout 原文流式输出 |
| `ThinkingDelta` | stderr，`[thinking]` 前缀 |
| `ToolCallStart/Completed`、`ToolResult` | stderr 摘要（参数/结果截断到 300 字符） |
| `UsageEvent` | stderr `[usage]` 行 |
| `RawEvent` | stderr `[raw]`（截断到 500 字符） |
| `Failed` | stderr `[failed] code：message` |
| `Done` | stderr `[done] <StopReason>` |

退出码：`0` 成功；`1` 请求/事件流失败；`2` 参数或插件错误；`130` 用户 Ctrl+C。
网络错误、连接被拒绝等会以 `[error]` 打印并返回 `1`，不会崩溃。
