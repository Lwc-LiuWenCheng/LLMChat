# 数据源配置向导设计（DeepSeek 优先）

## 背景与目标
用户原始需求（m01701）：在数据源配置处跳转 DeepSeek 登录、创建 API、把凭证填回本地、显示余额。
用户选 A 方案（m01755：「用A吧 试下看看」）。

## 关键前提验证结论：纯 A（复用 DSH 私有 PKCE 通道）不可行
源码证据（`dsh-deepseek-account-platform/lib/index.js`，在 DSH 安装目录下）：
1. `requestPlatform()` 打 `POST {platformOrigin}/auth-api/v0/dsh/{auth_init|auth_exchange}` —— 路径硬编码 `/dsh/`，DeepSeek 给 DSH 客户端的私有通道。
2. 请求头带 `platformClientHeaders(this.platform, client)` 注入的 DSH 专用客户端身份；LLMChat 拿不到。
3. 浏览器跳转固定 `/dsh/authorize`、完成页 `/dsh/authorized`，`login_source` 仅 `web`/`desktop`（DSH 语义）。
4. DSH 也没有「自动创建/抓取 API Key」的协议；`dsh-llm-deepseek-api-key` 的 `apiKeyEnv` 是纯手动配置。

网络探测：`GET https://api.deepseek.com/user/balance` 无凭证 401（端点存在且公开）；`platform.deepseek.com` 整站对当前 IP 429（限流，无法进一步探测，但源码证据已足够）。

## 可落地形态：跳转 + 回填 + 验证 + 余额（不碰插件契约）
- **跳转**：系统浏览器打开 `https://platform.deepseek.com/api_keys`（创建/复制 Key）与 `https://platform.deepseek.com/top_up`（充值）。
- **回填**：用户在向导内粘贴 Key（DeepSeek 官方无「自动抓取」协议，无法免粘贴；诚实的半自动）。
- **验证**：点「验证并保存」→ `GET https://api.deepseek.com/user/balance`（`Authorization: Bearer <key>`）→ 成功写 `SettingsRepository.Set("deepseek.apiKey", key)` 并显示余额。
- **余额**：官方响应 `{is_available:bool, balance_infos:[{currency:"CNY"|"USD", total_balance, granted_balance, topped_up_balance}]}`（decimal string）。展示每币种总/赠金/充值余额；`is_available=false` 提示余额不可用。
- **错误分类**：401 → key 无效；429 → 限流稍后重试；网络错误 → 网络失败；其他 non-2xx → 服务错误。UI 文案不泄漏 key。
- **URL 打开抽象**：`IUrlLauncher.Launch(Uri)`，生产用 `SystemUrlLauncher`（`Process.Start` / Avalonia TopLevel.Launcher），headless 测试注入假实现，不真开浏览器。

## 边界
- 不动 `Chat.Abstractions`（零契约变更）、不动三个 provider 插件、不动 `Chat.Storage`。向导是宿主（Chat.App）功能，不是 provider 契约的一部分，符合「单个接口变更不炸整体」的插件化精神。
- 不实现 PKCE grant token 路径；未来 DeepSeek 官方若开放公开 OAuth，再加独立 adapter，不破坏现有形态。
- 密钥仍存 `app_settings`（v1 明文 + 文件权限，沿用现状），不在日志/UI 输出完整 key。

## 里程碑
- M-A1（本轮）：DeepSeek 数据源向导——跳转、粘贴回填、余额验证、余额展示、错误分类、headless 测试。
- M-A2（待官方开放 OAuth 或用户授权）：账号 grant 登录 adapter（独立服务，不动 A1）。
