# chat-demo 上游来源与本地改动

## 上游

- 项目：[deepseek-ai/chat-demo](https://github.com/deepseek-ai/chat-demo)
- 许可：MIT（见 [LICENSE](./LICENSE)）
- fork 基线 commit：`429af5a4558469496b231631e850fbccc0c30c87`

## 我们的改动范围

为把会话历史从浏览器 IndexedDB 迁到本地网关（见 docs/architecture/web-gateway-design.md），
在保持对外 14 个导出签名不变的前提下做如下改造：

1. **新增** src/services/chat/remoteDb.ts：用 fetch 实现原 db.ts 的 13 个函数与 StoredFile 类型，
   调用网关 /api/sessions*、/api/files/{id}、/api/import/*；错误统一为带 code/message 的 Error。
2. **替换** src/services/chat/db.ts：从 IndexedDB 实现改为 export * from ./remoteDb.js，
   消费者 store.ts / backup.ts / history/exportArchive.ts / history/importArchive.ts /
   components/files/useFileView.ts 零改动。
3. **修改** rspack.config.ts：createConfig({ apiOrigin }) 支持环境变量 GATEWAY_ORIGIN
   （设为空串即同源请求网关），默认仍是官方 https://api.deepseek.com 以便对比。
4. **新增** tests/remoteDb.test.mjs：node --test 轻量用例，用全局 fetch stub 验证 14 个导出的
   URL / method / body 形状、404→undefined、错误 code 透传与网络错误映射。
5. **修改** package.json：新增 test 脚本（node --test tests/*.test.mjs）。

原 fork 的其余文件（UI、Markdown、i18n、history ZIP 编解码、API 客户端等）未做改动。
