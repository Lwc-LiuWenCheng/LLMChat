/**
 * 会话历史存储入口：已由 IndexedDB 切换到本地网关 HTTP 实现。
 *
 * 对外 14 个导出（13 个函数 + StoredFile 类型）签名与语义保持不变，
 * 消费点 store.ts / backup.ts / history/exportArchive.ts / history/importArchive.ts /
 * components/files/useFileView.ts 无需改动。
 *
 * 实现与 HTTP 契约见 ./remoteDb.ts 和 docs/architecture/web-gateway-design.md 第 3 节。
 */
export * from './remoteDb.js'
