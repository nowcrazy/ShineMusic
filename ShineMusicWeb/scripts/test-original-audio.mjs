/** Binary exports use the real TypeScript auth/context boundary with mocked HTTP and DOM. */
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const app = JSON.parse(fs.readFileSync(path.join(project, 'package.json'), 'utf8')).name === 'shine-music'
const source = path.join(project, 'src/services')

function harness({ browser = true, clickThrows = false } = {}) {
  const modules = new Map()
  const requests = []
  const created = []
  const revoked = []
  const anchors = []
  const timers = []
  const token = { access: 'access-secret-A' }
  let responder = async () => new Response(new Uint8Array([1, 2, 3]), {
    status: 200, headers: { 'Content-Type': 'audio/flac', 'Content-Disposition': "attachment; filename*=UTF-8''%E5%8E%9F%E4%BB%B6.flac" },
  })
  class BrowserURL extends URL {
    static createObjectURL(blob) { created.push(blob); return `blob:test-${created.length}` }
    static revokeObjectURL(url) { revoked.push(url) }
  }
  const context = vm.createContext({
    console, URL: BrowserURL, Headers, Response, Blob, FormData, AbortController,
    location: { origin: 'https://music.test' }, __SM_API_BASE__: '',
    setTimeout(fn) { timers.push(fn); return timers.length }, clearTimeout() {},
    document: browser ? {
      createElement(tag) {
        assert.equal(tag, 'a')
        const anchor = { click() { this.clicked = true; if (clickThrows) throw new Error('click failed') }, remove() { this.removed = true } }
        anchors.push(anchor)
        return anchor
      },
      body: { appendChild(anchor) { anchor.appended = true } },
    } : undefined,
    uni: { getStorageSync: () => '', setStorageSync() {}, removeStorageSync() {} },
    fetch: async (url, options = {}) => {
      requests.push({ url, options, authorization: new Headers(options.headers).get('Authorization') })
      return responder(url, options)
    },
  })
  function load(name) {
    const file = path.join(source, `${name}.ts`)
    if (modules.has(file)) return modules.get(file).exports
    const module = { exports: {} }
    modules.set(file, module)
    const compiled = ts.transpileModule(fs.readFileSync(file, 'utf8').replaceAll('import.meta.env', '({})'), {
      compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    }).outputText
    const require = dependency => {
      if (dependency === '@/locale') return { t: key => key }
      if (dependency === './tokens') return { getAccessToken: () => token.access, getRefreshToken: () => 'refresh-secret', clearTokens: () => { token.access = '' } }
      if (dependency.startsWith('./')) return load(dependency.slice(2))
      throw new Error(`Unexpected dependency: ${dependency}`)
    }
    vm.runInContext(`(function(require,module,exports){${compiled}\n})`, context, { filename: file })(require, module, module.exports)
    return module.exports
  }
  const http = load('http')
  if (app) { http.setApiUserId('user-A'); http.setAuthRequestGuard(() => true) }
  else http.setSessionRequestGuard(() => true, () => {}, () => {})
  const download = load('original-audio')[app ? 'downloadMyOriginalAudio' : 'downloadAdminOriginalAudio']
  return {
    download, http, token, requests, created, revoked, anchors,
    names: load('original-audio-file'),
    respond(fn) { responder = fn },
    invalidate() { if (app) http.setApiUserId('user-B'); else http.invalidateSessionRequests() },
    flushTimers() { while (timers.length) timers.shift()() },
  }
}

test('authenticated attachment uses the original endpoint and releases its Blob URL', async () => {
  const h = harness()
  await h.download('id/with?', 'Track')
  assert.equal(h.requests.length, 1)
  assert.equal(h.requests[0].url, app ? '/api/v1/me/library/id%2Fwith%3F/original' : '/api/v1/admin/tracks/id%2Fwith%3F/original')
  assert.equal(h.requests[0].authorization, 'Bearer access-secret-A')
  assert.equal(h.requests[0].options.redirect, 'error')
  assert.doesNotMatch(h.requests[0].url, /secret|token|Bearer/)
  assert.equal(h.anchors[0].download, '原件.flac')
  assert.equal(h.anchors[0].clicked, true)
  assert.equal(h.anchors[0].removed, true)
  assert.deepEqual([...new Uint8Array(await h.created[0].arrayBuffer())], [1, 2, 3])
  h.flushTimers()
  assert.deepEqual(h.revoked, ['blob:test-1'])
})

test('401 refresh retries with the new token rather than embedding credentials in URLs', async () => {
  const h = harness()
  h.http.setRefreshHandler(async () => { h.token.access = 'access-secret-B'; return true })
  h.respond(async () => h.requests.length === 1 ? new Response('{}', { status: 401 }) : new Response('audio', { status: 200, headers: { 'Content-Type': 'audio/flac' } }))
  await h.download('track', 'Track')
  assert.deepEqual(h.requests.map(request => request.authorization), ['Bearer access-secret-A', 'Bearer access-secret-B'])
  assert.equal(h.created.length, 1)
  h.flushTimers()
})

for (const status of [401, 403, 404, 500, 206]) {
  test(`HTTP ${status} is an error and never triggers a download`, async () => {
    const h = harness()
    h.http.setRefreshHandler(async () => false)
    h.respond(async () => new Response('{"error":{"code":"blocked","message":"Not available"}}', { status }))
    await assert.rejects(h.download('track', 'Track'), error => error.status === status)
    assert.equal(h.created.length, 0)
    assert.equal(h.anchors.length, 0)
  })
}

test('server/account change while reading audio prevents saving it in the next session', async () => {
  const h = harness()
  h.respond(async () => {
    const response = new Response('audio', { status: 200, headers: { 'Content-Type': 'audio/flac' } })
    response.blob = async () => { h.invalidate(); return new Blob(['audio']) }
    return response
  })
  await assert.rejects(h.download('track', 'Track'), error => ['api_context_changed', 'session_changed'].includes(error.code))
  assert.equal(h.created.length, 0)
  if (app) assert.equal(h.requests[0].options.signal.aborted, true)
})

test('DOM save failure still removes its anchor and revokes the object URL', async () => {
  const h = harness({ clickThrows: true })
  await assert.rejects(h.download('track', 'Track'), /click failed/)
  assert.equal(h.anchors[0].removed, true)
  h.flushTimers()
  assert.deepEqual(h.revoked, ['blob:test-1'])
})

for (const [body, type] of [['{"error":true}', 'application/json'], ['<html>Sign in</html>', 'text/html'], ['', 'audio/flac']]) {
  test(`a 200 ${type} ${body ? 'non-audio response' : 'empty file'} is not reported as a saved original`, async () => {
    const h = harness()
    h.respond(async () => new Response(body, { status: 200, headers: { 'Content-Type': type } }))
    await assert.rejects(h.download('track', 'Track'), error => error.code === 'invalid_original_response')
    assert.equal(h.created.length, 0)
  })
}

for (const [disposition, title, type, expected] of [
  ["attachment; filename*=UTF-8''%E6%AD%8C%E6%9B%B2.mp3", 'fallback', 'audio/mpeg', '歌曲.mp3'],
  ['attachment; filename="../bad.exe"', 'fallback', 'audio/flac', '_bad.exe.audio'],
  ["attachment; filename*=UTF-8''%XX; filename=ok.mp3", 'fallback', 'audio/mpeg', 'ok.mp3'],
  ['attachment; filename="CON.flac"', 'fallback', 'audio/flac', '_CON.flac'],
  ['attachment; filename="song\u202Eexe.mp3"', 'fallback', 'audio/mpeg', 'song_exe.mp3'],
  [null, 'No header', 'audio/flac', 'No header.flac'],
  [null, 'Track', 'application/octet-stream', 'Track.audio'],
]) {
  test(`safe filename: ${expected}`, () => {
    const h = harness()
    assert.equal(h.names.originalAudioFileName(disposition, title, type), expected)
  })
}

if (app) {
  test('native/non-browser platform reports unsupported without fetching or claiming a save', async () => {
    const h = harness({ browser: false })
    await assert.rejects(h.download('track', 'Track'), error => error.code === 'original_download_unsupported')
    assert.equal(h.requests.length, 0)
    assert.equal(h.created.length, 0)
  })
}
