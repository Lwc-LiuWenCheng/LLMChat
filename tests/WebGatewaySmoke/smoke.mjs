/**
 * 方案 B 端到端冒烟（Node 驱动）：真实 fetch 打真实 Chat.Gateway，
 * 前端数据层直接 import web/chat-demo/src/services/chat/remoteDb.ts（Node 24 原生 TS 剥离）。
 *
 * 覆盖：会话/附件 CRUD + SQLite 落库 + 刷新读回、同源静态托管与 SPA fallback、
 * 聊天 Bearer 注入与 SSE 帧顺序、缺 Key 428。假 DeepSeek handler 与 Key 注入由
 * tests/WebGatewaySmoke/Program.cs 提供；本脚本不连真实网络、不读真实密钥。
 */
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'

const base = process.env.SMOKE_BASE_URL
const recordPath = process.env.SMOKE_RECORD
const dbPath = process.env.SMOKE_DB
if (!base || !recordPath || !dbPath) {
  console.error('缺少 SMOKE_BASE_URL / SMOKE_RECORD / SMOKE_DB')
  process.exit(2)
}

const NL = String.fromCharCode(10)

// remoteDb.ts 用相对路径 /api/...；Node fetch 不支持相对 URL，这里只做 base 拼接。
const nativeFetch = globalThis.fetch
globalThis.fetch = (url, init) => nativeFetch(
  typeof url === 'string' && url.startsWith('/') ? new URL(url, base) : url,
  init
)

const db = await import('../../web/chat-demo/src/services/chat/remoteDb.ts')

const results = []
const check = async (label, fn) => {
  await fn()
  results.push(label)
  console.log('  PASS ' + label)
}

const SESSION_ID = 'smoke-e2e-session'
const FILE_ID = 'smoke-att-1'
const FILE_TEXT = 'hello from web gateway smoke'
const TITLE = '端到端冒烟会话'
const RENAMED = '重命名后的冒烟会话'

const session = (title) => ({
  id: SESSION_ID,
  title,
  createdAt: 1700000000000,
  updatedAt: 1700000001000,
  rootMessageId: 'm1',
  messages: [
    {
      id: 'm1',
      role: 'user',
      content: '问题：请回答 hello',
      createdAt: 1700000000000,
      files: [{
        id: FILE_ID,
        serverId: null,
        name: 'note.txt',
        size: FILE_TEXT.length,
        isImage: false,
        mimeType: 'text/plain',
      }],
    },
    {
      id: 'm2',
      parentId: 'm1',
      role: 'assistant',
      createdAt: 1700000000100,
      status: 'done',
      model: 'deepseek-chat',
      prefix: 'final',
      blocks: [
        { type: 'thinking', thinking: 'THINK-smoke' },
        { type: 'text', text: 'TEXT_DELTA-smoke' },
      ],
    },
  ],
})

const sqlite = () => new DatabaseSync(dbPath, { readOnly: true })
const scalar = (database, sql, params) => database.prepare(sql).get(...(params || []))
const count = (database, table, where, params) => scalar(database, 'SELECT COUNT(*) AS n FROM ' + table + (where ? ' WHERE ' + where : ''), params).n

try {
  console.log('==> 1. 前端数据层经网关写 SQLite，重新 GET 读回（刷新语义）')
  await check('POST /api/files 上传附件原件（multipart）', async () => {
    const blob = new Blob([FILE_TEXT], { type: 'text/plain' })
    blob.name = 'note.txt'
    await db.saveFiles([{ id: FILE_ID, blob }])
  })

  await check('PUT /api/sessions 保存会话 + 附件（前端 saveSession）', async () => {
    const blob = new Blob([FILE_TEXT], { type: 'text/plain' })
    blob.name = 'note.txt'
    await db.saveSession(session(TITLE), [{ id: FILE_ID, blob }])
  })

  await check('SQLite 落库：sessions/messages/parts/files 与 app_settings key', async () => {
    const database = sqlite()
    try {
      assert.equal(count(database, 'sessions', 'id = ?', [SESSION_ID]), 1)
      assert.equal(count(database, 'messages', 'session_id = ?', [SESSION_ID]), 2)
      assert.equal(count(database, 'parts', "session_id = ? AND kind = 'Thinking'", [SESSION_ID]), 1)
      assert.equal(count(database, 'parts', "session_id = ? AND kind = 'File'", [SESSION_ID]), 1)
      assert.equal(count(database, 'files'), 1)
      const file = scalar(database, 'SELECT original_name, size FROM files LIMIT 1')
      assert.equal(file.original_name, 'note.txt')
      assert.equal(file.size, FILE_TEXT.length)
      assert.equal(scalar(database, 'SELECT value FROM app_settings WHERE key = ?', ['deepseek.apiKey']).value, 'smoke-server-key')
    } finally {
      database.close()
    }
  })

  await check('GET /api/sessions 列表包含新会话（摘要 4 字段）', async () => {
    const list = await db.loadSessionSummaries()
    const item = list.find((s) => s.id === SESSION_ID)
    assert.ok(item, '列表里应有新会话')
    assert.equal(item.title, TITLE)
    assert.deepEqual(Object.keys(item).sort(), ['createdAt', 'id', 'title', 'updatedAt'])
  })

  await check('GET /api/sessions/{id} 原样读回（标题/正文/附件/thinking 文本）', async () => {
    const loaded = await db.loadSession(SESSION_ID)
    assert.ok(loaded, '应能读回会话')
    assert.equal(loaded.title, TITLE)
    assert.equal(loaded.messages.length, 2)
    assert.equal(loaded.messages[0].content, '问题：请回答 hello')
    assert.equal(loaded.messages[0].files[0].name, 'note.txt')
    assert.deepEqual(loaded.messages[1].blocks.map((b) => b.type), ['thinking', 'text'])
    assert.equal(loaded.messages[1].blocks[0].thinking, 'THINK-smoke')
    assert.equal(loaded.messages[1].blocks[1].text, 'TEXT_DELTA-smoke')
  })

  await check('GET /api/files/{sha256} 原样下载附件（loadFile）', async () => {
    const database = sqlite()
    let sha
    try {
      sha = scalar(database, 'SELECT sha256 FROM files LIMIT 1').sha256
    } finally {
      database.close()
    }
    const blob = await db.loadFile(sha)
    assert.ok(blob instanceof Blob)
    assert.equal(await blob.text(), FILE_TEXT)
  })

  await check('PATCH 重命名 + 再次 GET 仍是新标题（刷新语义）', async () => {
    await db.renameSession(SESSION_ID, RENAMED)
    const fresh = await db.loadSession(SESSION_ID)
    assert.equal(fresh.title, RENAMED)
  })

  console.log('==> 2. 聊天中转：Bearer 注入 + SSE 帧顺序 + 缺 Key 428')
  await check('POST /anthropic/v1/messages 收到完整 SSE（THINK/TEXT_DELTA/usage/done）', async () => {
    const response = await nativeFetch(new URL('/anthropic/v1/messages', base), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'x-api-key': 'client-forged-key' },
      body: JSON.stringify({ model: 'deepseek-chat', stream: true, messages: [{ role: 'user', content: 'hi' }] }),
    })
    assert.equal(response.status, 200)
    assert.ok((response.headers.get('content-type') || '').includes('text/event-stream'))
    const text = await response.text()
    const events = text.split(NL).filter((line) => line.startsWith('event: ')).map((line) => line.slice(7))
    assert.deepEqual(events, ['message_start', 'content_block_delta', 'content_block_delta', 'message_delta', 'message_stop'])
    assert.ok(text.includes('THINK-smoke'), 'SSE 应含 THINK 帧')
    assert.ok(text.includes('TEXT_DELTA-smoke'), 'SSE 应含 TEXT_DELTA 帧')
    assert.ok(text.includes('output_tokens'), 'SSE 应含 usage 帧')
    assert.ok(text.includes('message_stop'), 'SSE 应含 done 帧')
  })

  await check('假 handler 记录：Bearer 网关 Key 注入，客户端伪造 x-api-key 被忽略', async () => {
    const lines = readFileSync(recordPath, 'utf8').trim().split(NL).filter(Boolean).map(JSON.parse)
    assert.equal(lines.length, 1)
    const call = lines[0]
    assert.equal(call.method, 'POST')
    assert.equal(call.absolutePath, '/anthropic/v1/messages')
    assert.equal(call.authorization, 'Bearer smoke-server-key')
    assert.equal(call.anthropicBeta, 'files-api-2025-04-14')
    assert.equal(call.xApiKey, '')
    assert.equal(JSON.parse(call.body).model, 'deepseek-chat')
  })

  await check('清空 app_settings key 后聊天返回 428 missing_api_key（真实 SQLite 写入）', async () => {
    const writable = new DatabaseSync(dbPath)
    try {
      writable.prepare('DELETE FROM app_settings WHERE key = ?').run('deepseek.apiKey')
    } finally {
      writable.close()
    }
    const response = await nativeFetch(new URL('/anthropic/v1/messages', base), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model: 'deepseek-chat', stream: true }),
    })
    assert.equal(response.status, 428)
    const body = await response.json()
    assert.equal(body.error.code, 'missing_api_key')
    const calls = readFileSync(recordPath, 'utf8').trim().split(NL).filter(Boolean)
    assert.equal(calls.length, 1, '缺 Key 不应触达上游 handler')
  })

  console.log('==> 3. 静态 dist 同源托管与 SPA fallback')
  await check('GET / 返回构建产物 index.html', async () => {
    const response = await nativeFetch(new URL('/', base))
    assert.equal(response.status, 200)
    const html = await response.text()
    assert.ok(html.includes('<div id="root">'), 'index 应含 root 节点')
    assert.ok(html.includes('static/js/main.'), 'index 应引用 main JS')
  })

  await check('GET /static/js/main.*.js 可加载（静态资源同源）', async () => {
    const index = await (await nativeFetch(new URL('/', base))).text()
    const needle = 'src="static/js/'
    const at = index.indexOf(needle)
    assert.ok(at >= 0, 'index.html 应引用 main JS')
    const js = 'static/js/' + index.slice(at + needle.length).split('"')[0]
    const response = await nativeFetch(new URL(js, base))
    assert.equal(response.status, 200)
    assert.ok((await response.text()).length > 0)
  })

  await check('GET /deep/route 回退 index.html（SPA fallback）', async () => {
    const response = await nativeFetch(new URL('/deep/route', base))
    assert.equal(response.status, 200)
    assert.ok((await response.text()).includes('<div id="root">'))
  })

  await check('GET /api/nonexistent 返回 404 not_found（不被 SPA fallback 吞掉）', async () => {
    const response = await nativeFetch(new URL('/api/nonexistent', base))
    assert.equal(response.status, 404)
    assert.equal((await response.json()).error.code, 'not_found')
  })

  console.log('==> 4. DELETE 软删会话，列表与详情不再可见（SQLite deleted_at 落库）')
  await check('DELETE /api/sessions/{id} 后 404 + SQLite deleted_at 非空', async () => {
    await db.deleteSession(SESSION_ID)
    assert.equal(await db.loadSession(SESSION_ID), undefined)
    const list = await db.loadSessionSummaries()
    assert.ok(!list.some((s) => s.id === SESSION_ID))
    const database = sqlite()
    try {
      assert.ok(scalar(database, 'SELECT deleted_at FROM sessions WHERE id = ?', [SESSION_ID]).deleted_at)
    } finally {
      database.close()
    }
  })

  console.log('')
  console.log('全部冒烟检查通过（' + results.length + ' 项）。')
} catch (error) {
  console.error('')
  console.error('[smoke FAIL] ' + (error && error.stack ? error.stack : error))
  process.exit(1)
}
