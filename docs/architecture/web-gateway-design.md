# Web 前端 + 本地网关设计（方案 B）

> 状态：设计定稿，待实现。背景：官方 chat.deepseek.com 的历史列表读其服务端且只保留七天；
> 开源 chat-demo 是纯静态 React SPA，数据层只在 IndexedDB，API_ORIGIN 可配置。
> 方案：fork demo 前端 + 自建本地网关，把会话历史落到我们已有的 SQLite，网页与 Avalonia 客户端共用同一真相源。

## 1. 组件与数据流

```
浏览器 (web/chat-demo/dist, 由网关同源托管)
  │  fetch /api/...          历史读写（替代 IndexedDB）
  │  fetch /anthropic/v1/... 聊天请求（SSE）
  ▼
Chat.Gateway (ASP.NET Core, net10.0, 默认 http://127.0.0.1:5080)
  ├─ 静态文件托管 demo dist（SPA fallback 到 index.html）
  ├─ /api/sessions*  ── v3 桥接 ──> Chat.Storage (SQLite, 与 Avalonia/CLI 同一个库)
  ├─ /api/files/{id} ───────────> FileStore (内容寻址)
  └─ /anthropic/v1/* ── 透明转发 ─> https://api.deepseek.com/anthropic/v1/*
        · Bearer Key 来自 app_settings["deepseek.apiKey"]（前端不接触密钥）
        · SSE 原样透传；记录 usage 事件到日志
```

设计约束：**不动 Chat.Abstractions 契约**、**不动三个 provider**、**不动现有 SQLite schema**。
网关是新的宿主工程，只复用 Chat.Storage / Chat.Core 的现有能力。

## 2. 历史桥接（核心）

chat-demo 的 `Session` JSON 与我们的 v3 归档 `chat_sessions/{id}.json` 是同一个结构
（`toArchive.ts` 已证实：messages 树 + parentId/selectedChildId + FILE 片段 + fragmentType）。
因此不写新映射器，复用已经过真实 ZIP 验证的 v3 双向转换：

- 保存 `PUT /api/sessions/{id}`：收到 Session JSON → 内存中构造 v3 ZIP
  （manifest.json 首条 → chat_sessions/{id}.json → chat_sessions/{id}_files/... → chat_session_index.json）
  → 调 `ChatDemoArchive.ImportAsync(stream, dataDir, mode: MergeUpsert)`。
  附件原件从请求 multipart 的 files 部分落 FileStore，FILE 片段的 id 必须等于前端 FileAttachment.id。
- 读取 `GET /api/sessions/{id}`：调 `ChatDemoExportService.ExportAsync` 导出单会话 ZIP →
  解出 `chat_sessions/{id}.json` 原样返回。
- 列表 `GET /api/sessions`：直接查 sessions 表，返回 `SessionSummary[]`（id/title/createdAt/updatedAt）。
- 删除 `DELETE /api/sessions/{id}`：软删（deleted_at）并级联清理附件引用。
- 重命名 `PATCH /api/sessions/{id}`：只更新 title + updated_at。

## 3. HTTP API 契约（前端 db.ts 的 14 个导出函数逐一映射）

| 前端 db 函数 | HTTP | 说明 |
|---|---|---|
| loadSessionSummaries | GET /api/sessions | 返回 SessionSummary[] |
| loadSession | GET /api/sessions/{id} | 返回完整 Session JSON，404 → undefined |
| saveSession | PUT /api/sessions/{id} | body = { session, files: [{id,name,mime,base64}] }；幂等 upsert |
| renameSession | PATCH /api/sessions/{id} | body = { title } |
| deleteSession | DELETE /api/sessions/{id} | 204 |
| saveFiles | POST /api/files | multipart，返回 {id}[] |
| loadFile | GET /api/files/{id} | 返回 Blob (image/* 或 application/octet-stream) |
| clearStaging / addStagingSession / saveStagingFiles / updateStagingFileTypes / commitStaging | POST /api/import/* | 服务端 staging 表，事务提交；MVP 可先返回明确的 501 并在 UI 禁用 ZIP 导入，后续补 |

错误格式统一 `{ error: { code, message } }`：`not_found` / `invalid_session` / `io_error` / `not_implemented`。

## 4. 中转与鉴权

- `POST /anthropic/v1/messages`：读请求体，注入 `Authorization: Bearer <key>` 与
  `anthropic-beta: files-api-2025-04-14`，流式转发到 DeepSeek；保留 SSE 帧顺序，末尾 usage 写日志。
  请求头里的 `x-api-key` **忽略**（前端不应持有密钥）。
- `GET /models` → 转发 DeepSeek `/models`。
- `POST /anthropic/v1/files` 与 `GET/DELETE /anthropic/v1/files/{id}`：MVP 转发到 DeepSeek
  （保持 demo 的 serverId 语义），同时在 FileStore 留本地副本；失败时返回明确错误。
- 网关只监听 `127.0.0.1`（可通过 `--urls` 改），不暴露公网；无鉴权层，靠本机隔离。
- API Key 未设置时，聊天端点返回 428 并提示「请先在设置里保存 DeepSeek API Key」。

## 5. 前端改造范围（最小化）

只改 `web/chat-demo/src/services/chat/db.ts` 一个文件：把所有 IndexedDB 调用替换为
`fetch` 到网关的 HTTP 客户端（新增 `src/services/chat/remoteDb.ts`），保持 14 个导出函数签名不变，
这样 `store.ts` / `backup.ts` / `exportArchive.ts` / `importArchive.ts` / `useFileView.ts` 全部不用改。
构建配置改 `apiOrigin: ''`（同源相对路径），由网关托管 dist。
保留 demo 的 MIT LICENSE 与 upstream 来源说明（新增 `web/chat-demo/UPSTREAM.md`）。

## 6. 验证策略（不依赖真实网络/密钥）

- 网关测试：用 in-memory 临时数据目录 + 假 DeepSeek handler（记录请求、回放录制 SSE），
  覆盖 ① Session JSON → SQLite → 读回 round-trip（含多分支、附件、thinking 块）
  ② 幂等 PUT ③ 聊天转发注入 Bearer 且 SSE 顺序不变 ④ 缺 Key 返回 428 ⑤ 坏 JSON 错误格式。
- 前端测试：demo 无测试框架，新增轻量 `node --test` 用例，用假 fetch 验证 db.ts
  的 14 个函数调用了正确 URL 与 body 形状（不启动浏览器）。
- 端到端冒烟（手动）：启动网关 → 浏览器打开 → 发消息 → 刷新页面 → 历史仍在；
  Avalonia 客户端打开同一个库 → 能看到网页新建的会话。
- 全量回归：`dotnet build Chat.slnx` 0W0E；`bash scripts/test.sh` 8 套全绿（基线 246）。

## 7. 里程碑与验收

- **M-W1 历史桥接 + 前端换源**：网页会话写入 SQLite、刷新后不丢；Avalonia 可见。
  验收：网关 round-trip 测试 + 前端 URL 测试 + 手动冒烟脚本记录。
- **M-W2 聊天中转 + 附件**：网页可正常对话、图片附件可用、密钥不出本机。
  验收：假 handler 的转发测试 + 手动冒烟。
- **M-W3 ZIP 导入 staging + 文档**：把 demo 的 ZIP 导入/导出也接到网关。
  验收：真实脱敏 ZIP 在隔离目录 round-trip。

## 8. 风险与未决

- 前端 `StagedSession` 的 5 个函数在 MVP 返回 501，需确认是否阻塞用户（demo 的 ZIP 导入功能会不可用）。
- DeepSeek Files API 的 serverId 生命周期仍由服务端控制；若服务端清理文件，网页历史附件可能失效。
  本地副本保留在 FileStore，后续可加 `/api/files/{id}` 后端回放。
- 网关注入 Key 后，浏览器端 demo 的「设置 API Key」入口应隐藏（前端不改的话会显示但无效，不阻塞）。
- 同源托管避免 CORS；若用户想用 rspack dev server 开发，需配 devServer.proxy。
