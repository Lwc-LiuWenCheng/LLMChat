/**
 * 本地网关版会话存储：把 db.ts 原本的 IndexedDB 实现替换为对网关的 HTTP 调用。
 * 导出签名与语义与旧 db.ts 保持一致，五个消费点（store/backup/exportArchive/importArchive/useFileView）零改动。
 *
 * HTTP 契约见 docs/architecture/web-gateway-design.md 第 3 节。
 * 错误统一为 Error + code/message：not_found / invalid_session / io_error / not_implemented。
 */
import type { Session, SessionSummary } from './types.js'

/** 文件原件按附件的本地 id 存储；与旧 db.ts 的 StoredFile 完全同构。 */
export type StoredFile = {
  id: string
  blob: Blob
}

export type RemoteDbErrorShape = Error & {
  code: string
  status?: number
}

const remoteError = (code: string, message: string, status?: number): RemoteDbErrorShape => {
  const error = new Error(message) as RemoteDbErrorShape
  error.name = 'RemoteDbError'
  error.code = code
  if (status !== undefined) {
    error.status = status
  }
  return error
}

/** 运行时由 DefinePlugin 注入；Node 测试环境下回退为空串（同源相对路径）。 */
const apiOrigin = (): string =>
  typeof __API_ORIGIN__ === 'string' ? __API_ORIGIN__ : ''

const apiUrl = (path: string): string => `${apiOrigin()}${path}`

const summary = (value: SessionSummary): SessionSummary => ({
  id: value.id,
  title: value.title,
  createdAt: value.createdAt,
  updatedAt: value.updatedAt,
})

type ErrorBody = { error?: { code?: unknown; message?: unknown } }

const readError = async (response: Response): Promise<RemoteDbErrorShape> => {
  const text = await response.text().catch(() => '')
  let code = 'io_error'
  let message = text || response.statusText || `HTTP ${response.status}`
  try {
    const body = JSON.parse(text) as ErrorBody
    if (typeof body.error?.code === 'string') {
      code = body.error.code
    }
    if (typeof body.error?.message === 'string') {
      message = body.error.message
    }
  } catch {
    // 非 JSON 响应体：原文作为 message，code 用 io_error。
  }
  return remoteError(code, message, response.status)
}

const request = async (url: string, init: RequestInit): Promise<Response> => {
  let response: Response
  try {
    response = await fetch(url, init)
  } catch (error) {
    throw remoteError('io_error', error instanceof Error ? error.message : String(error))
  }
  if (!response.ok) {
    throw await readError(response)
  }
  return response
}

const jsonRequest = async <T>(url: string, init: RequestInit): Promise<T> => {
  const response = await request(url, init)
  return (await response.json()) as T
}

const jsonHeaders = { 'Content-Type': 'application/json' }

const toBase64 = async (blob: Blob): Promise<string> => {
  const bytes = new Uint8Array(await blob.arrayBuffer())
  let binary = ''
  // 分块避免大附件触发字符串参数长度上限。
  const chunk = 0x8000
  for (let offset = 0; offset < bytes.length; offset += chunk) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk))
  }
  // 浏览器与 Node 18+ 都提供全局 btoa。
  return btoa(binary)
}

const fileName = (file: StoredFile): string => {
  const name = (file.blob as Blob & { name?: unknown }).name
  return typeof name === 'string' && name ? name : file.id
}

const toStoredFiles = async (files: StoredFile[]) =>
  Promise.all(
    files.map(async (file) => ({
      id: file.id,
      name: fileName(file),
      mime: file.blob.type || 'application/octet-stream',
      base64: await toBase64(file.blob),
    }))
  )

const multipart = (files: StoredFile[]): FormData => {
  const form = new FormData()
  for (const file of files) {
    form.append('files', file.blob, fileName(file))
  }
  return form
}

/** GET /api/sessions → SessionSummary[] */
export const loadSessionSummaries = async (): Promise<SessionSummary[]> => {
  const list = await jsonRequest<SessionSummary[]>(apiUrl('/api/sessions'), { method: 'GET' })
  return list.map(summary)
}

/** GET /api/sessions/{id} → 完整 Session JSON；404 → undefined */
export const loadSession = async (id: string): Promise<Session | undefined> => {
  let response: Response
  try {
    response = await fetch(apiUrl(`/api/sessions/${encodeURIComponent(id)}`), { method: 'GET' })
  } catch (error) {
    throw remoteError('io_error', error instanceof Error ? error.message : String(error))
  }
  if (response.status === 404) {
    return undefined
  }
  if (!response.ok) {
    throw await readError(response)
  }
  return (await response.json()) as Session
}

/** PUT /api/sessions/{id}，body = { session, files: [{id,name,mime,base64}] }；幂等 upsert */
export const saveSession = async (session: Session, files: StoredFile[] = []): Promise<void> => {
  await request(apiUrl(`/api/sessions/${encodeURIComponent(session.id)}`), {
    method: 'PUT',
    headers: jsonHeaders,
    body: JSON.stringify({ session, files: await toStoredFiles(files) }),
  })
}

/** PATCH /api/sessions/{id}，body = { title } */
export const renameSession = async (id: string, title: string): Promise<void> => {
  await request(apiUrl(`/api/sessions/${encodeURIComponent(id)}`), {
    method: 'PATCH',
    headers: jsonHeaders,
    body: JSON.stringify({ title }),
  })
}

/** DELETE /api/sessions/{id}（204） */
export const deleteSession = async (sessionId: string): Promise<void> => {
  await request(apiUrl(`/api/sessions/${encodeURIComponent(sessionId)}`), { method: 'DELETE' })
}

/** POST /api/files，multipart 上传原件；返回 {id}[] 由服务端处理，调用方不依赖返回值。 */
export const saveFiles = async (files: StoredFile[]): Promise<void> => {
  await request(apiUrl('/api/files'), { method: 'POST', body: multipart(files) })
}

/** GET /api/files/{id} → Blob；404 → undefined */
export const loadFile = async (id: string): Promise<Blob | undefined> => {
  let response: Response
  try {
    response = await fetch(apiUrl(`/api/files/${encodeURIComponent(id)}`), { method: 'GET' })
  } catch (error) {
    throw remoteError('io_error', error instanceof Error ? error.message : String(error))
  }
  if (response.status === 404) {
    return undefined
  }
  if (!response.ok) {
    throw await readError(response)
  }
  return await response.blob()
}

/** POST /api/import/clear：开始导入前清空服务端暂存。 */
export const clearStaging = async (): Promise<void> => {
  await request(apiUrl('/api/import/clear'), { method: 'POST' })
}

/** POST /api/import/session：向暂存写入一个会话。 */
export const addStagingSession = async (session: Session): Promise<void> => {
  await request(apiUrl('/api/import/session'), {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify(session),
  })
}

/** POST /api/import/files：暂存附件原件，multipart。 */
export const saveStagingFiles = async (files: StoredFile[]): Promise<void> => {
  await request(apiUrl('/api/import/files'), { method: 'POST', body: multipart(files) })
}

/** POST /api/import/file-types：回填附件的 MIME 类型。 */
export const updateStagingFileTypes = async (types: ReadonlyMap<string, string>): Promise<void> => {
  await request(apiUrl('/api/import/file-types'), {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify(Object.fromEntries(types)),
  })
}

/** POST /api/import/commit：事务提交暂存，返回本次提交的 SessionSummary[]。 */
export const commitStaging = async (): Promise<SessionSummary[]> => {
  const list = await jsonRequest<SessionSummary[]>(apiUrl('/api/import/commit'), { method: 'POST' })
  return list.map(summary)
}

/** 本地存储失败不打断对话，只在控制台留痕（与旧 db.ts 完全一致）。 */
export const reportStorageError =
  (action: string) =>
  (error: unknown): void => {
    // eslint-disable-next-line no-console
    console.error(`[storage] ${action} failed`, error)
  }
