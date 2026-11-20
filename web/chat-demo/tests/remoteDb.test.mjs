/**
 * 轻量前端网关数据层测试：用全局 fetch stub 验证 13 个函数 + reportStorageError 的
 * URL / method / body 形状与错误映射，不启动浏览器、不引新依赖。
 *
 * Node 24 可直接剥离 TS 类型后 import remoteDb.ts；__API_ORIGIN__ 在 Node 下未定义，
 * remoteDb 回退为空串，故 URL 均为同源相对路径 /api/...。
 */
import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import * as db from '../src/services/chat/remoteDb.ts'

const calls = []

/** 安装 fetch stub：next() 返回 { status, body, blob? } 或抛错。 */
const stub = (handler) => {
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init })
    return handler(url, init, calls.length - 1)
  }
}

const jsonResponse = (body, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  statusText: '',
  json: async () => body,
  text: async () => JSON.stringify(body),
  blob: async () => new Blob(['file-bytes'], { type: 'image/png' }),
})

const session = (id = 's1') => ({
  id,
  title: '标题',
  createdAt: 1,
  updatedAt: 2,
  messages: [],
})

beforeEach(() => {
  calls.length = 0
  stub(() => jsonResponse({}))
})

test('导出面齐全：13 个函数 + StoredFile 类型（运行时验证 13 个函数）', () => {
  for (const name of [
    'loadSessionSummaries', 'loadSession', 'saveSession', 'renameSession',
    'deleteSession', 'saveFiles', 'loadFile', 'clearStaging', 'addStagingSession',
    'saveStagingFiles', 'updateStagingFileTypes', 'commitStaging', 'reportStorageError',
  ]) {
    assert.equal(typeof db[name], 'function', name)
  }
})

test('loadSessionSummaries → GET /api/sessions，摘要只保留 4 字段', async () => {
  stub(() => jsonResponse([{ id: 's1', title: 'T', createdAt: 1, updatedAt: 2, extra: 'drop' }]))
  const result = await db.loadSessionSummaries()
  assert.deepEqual(result, [{ id: 's1', title: 'T', createdAt: 1, updatedAt: 2 }])
  assert.equal(calls[0].url, '/api/sessions')
  assert.equal(calls[0].init.method, 'GET')
})

test('loadSession → GET /api/sessions/{id}，返回完整 Session；404 → undefined', async () => {
  stub(() => jsonResponse(session('s/1')))
  assert.deepEqual(await db.loadSession('s/1'), session('s/1'))
  assert.equal(calls[0].url, '/api/sessions/s%2F1')
  assert.equal(calls[0].init.method, 'GET')
  stub(() => jsonResponse({ error: { code: 'not_found', message: 'gone' } }, 404))
  assert.equal(await db.loadSession('missing'), undefined)
})

test('saveSession → PUT /api/sessions/{id}，body={session,files:[{id,name,mime,base64}]}', async () => {
  stub(() => ({ ok: true, status: 204, text: async () => '' }))
  const blob = new Blob(['hi'], { type: 'text/plain' })
  blob.name = 'note.txt'
  await db.saveSession(session(), [{ id: 'f1', blob }])
  assert.equal(calls[0].url, '/api/sessions/s1')
  assert.equal(calls[0].init.method, 'PUT')
  assert.equal(calls[0].init.headers['Content-Type'], 'application/json')
  const body = JSON.parse(calls[0].init.body)
  assert.deepEqual(body.session, session())
  assert.equal(body.files.length, 1)
  assert.deepEqual(body.files[0], { id: 'f1', name: 'note.txt', mime: 'text/plain', base64: 'aGk=' })
  // 无附件时 files 为空数组。
  await db.saveSession(session())
  assert.deepEqual(JSON.parse(calls[1].init.body).files, [])
})

test('renameSession → PATCH /api/sessions/{id}，body={title}', async () => {
  stub(() => ({ ok: true, status: 204, text: async () => '' }))
  await db.renameSession('s 1', '新标题')
  assert.equal(calls[0].url, '/api/sessions/s%201')
  assert.equal(calls[0].init.method, 'PATCH')
  assert.deepEqual(JSON.parse(calls[0].init.body), { title: '新标题' })
})

test('deleteSession → DELETE /api/sessions/{id}', async () => {
  stub(() => ({ ok: true, status: 204, text: async () => '' }))
  await db.deleteSession('s1')
  assert.equal(calls[0].url, '/api/sessions/s1')
  assert.equal(calls[0].init.method, 'DELETE')
})

test('saveFiles → POST /api/files，multipart FormData，file 部分按 id 命名', async () => {
  stub(() => ({ ok: true, status: 204, text: async () => '' }))
  await db.saveFiles([{ id: 'f1', blob: new Blob(['x'], { type: 'image/png' }) }])
  assert.equal(calls[0].url, '/api/files')
  assert.equal(calls[0].init.method, 'POST')
  assert.ok(calls[0].init.body instanceof FormData)
  const part = calls[0].init.body.get('files')
  assert.ok(part instanceof File)
  assert.equal(part.name, 'f1')
})

test('loadFile → GET /api/files/{id} 返回 Blob；404 → undefined', async () => {
  stub(() => jsonResponse({}))
  const blob = await db.loadFile('f1')
  assert.ok(blob instanceof Blob)
  assert.equal(await blob.text(), 'file-bytes')
  assert.equal(calls[0].url, '/api/files/f1')
  assert.equal(calls[0].init.method, 'GET')
  stub(() => jsonResponse({}, 404))
  assert.equal(await db.loadFile('missing'), undefined)
})

test('clearStaging / addStagingSession / saveStagingFiles / updateStagingFileTypes / commitStaging → /api/import/*', async () => {
  stub((url) => (
    url === '/api/import/commit'
      ? jsonResponse([{ id: 's1', title: 'T', createdAt: 1, updatedAt: 2 }])
      : { ok: true, status: 204, text: async () => '' }
  ))
  await db.clearStaging()
  await db.addStagingSession(session())
  await db.saveStagingFiles([{ id: 'f1', blob: new Blob(['y']) }])
  await db.updateStagingFileTypes(new Map([['f1', 'image/png']]))
  const committed = await db.commitStaging()
  assert.deepEqual(calls.map((c) => [c.url, c.init.method]), [
    ['/api/import/clear', 'POST'],
    ['/api/import/session', 'POST'],
    ['/api/import/files', 'POST'],
    ['/api/import/file-types', 'POST'],
    ['/api/import/commit', 'POST'],
  ])
  assert.deepEqual(JSON.parse(calls[1].init.body), session())
  assert.ok(calls[2].init.body instanceof FormData)
  assert.deepEqual(JSON.parse(calls[3].init.body), { f1: 'image/png' })
  assert.deepEqual(committed, [{ id: 's1', title: 'T', createdAt: 1, updatedAt: 2 }])
})

test('错误映射：服务端 {error:{code,message}} 原样透传 code/message + status', async () => {
  stub(() => jsonResponse(
    { error: { code: 'invalid_session', message: '会话格式错误' } },
    422
  ))
  await assert.rejects(db.saveSession(session()), (error) => {
    assert.equal(error.code, 'invalid_session')
    assert.equal(error.message, '会话格式错误')
    assert.equal(error.status, 422)
    return true
  })
})

test('网络失败映射为 io_error；非 JSON 错误体 message 用原文', async () => {
  stub(() => { throw new Error('boom') })
  await assert.rejects(db.loadSessionSummaries(), (error) => {
    assert.equal(error.code, 'io_error')
    assert.equal(error.message, 'boom')
    return true
  })
  stub(() => ({ ok: false, status: 500, statusText: 'Server Error', text: async () => 'plain failure' }))
  await assert.rejects(db.loadSession('s1'), (error) => {
    assert.equal(error.code, 'io_error')
    assert.equal(error.message, 'plain failure')
    return true
  })
})

test('reportStorageError 返回的函数记录日志而不抛出', () => {
  const original = console.error
  const lines = []
  console.error = (...args) => lines.push(args)
  try {
    const handler = db.reportStorageError('save session')
    handler(new Error('x'))
  } finally {
    console.error = original
  }
  assert.equal(lines.length, 1)
  assert.match(lines[0][0], /^\[storage\] save session failed$/)
})
