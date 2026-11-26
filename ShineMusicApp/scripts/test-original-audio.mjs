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

function harness({ browser = true, clickThrows = false, native = false } = {}) {
  const modules = new Map()
  const requests = []
  const created = []
  const revoked = []
  const anchors = []
  const timers = []
  const token = { access: 'access-secret-A' }
  const nativeState = { status: 200, type: 'audio/flac', size: 3, length: '3', returnedFilename: null, cancel: false, failCopy: false, beforeDownload: null, beforePicker: null, afterWrite: null, tasks: [], temporary: new Map(), removed: [], output: [], written: [], deleted: [], intents: [] }
  const main = {
    onActivityResult: null,
    startActivityForResult(intent, code) {
      nativeState.intents.push(intent)
      nativeState.beforePicker?.()
      queueMicrotask(() => this.onActivityResult?.(code, nativeState.cancel ? 0 : -1, { getData: () => 'content://chosen/document' }))
    },
    getContentResolver() { return resolver },
    finishActivity(code) { queueMicrotask(() => this.onActivityResult?.(code, 0, null)) },
  }
  const resolver = { openOutputStream() {
    if (nativeState.failCopy) throw new Error('provider write failed')
    return { write(buffer, offset, count) { nativeState.output.push(count); nativeState.written.push(...buffer.slice(offset, offset + count)); nativeState.afterWrite?.() }, flush() {}, close() {} }
  } }
  function Intent(action) { this.action = action; this.addCategory = value => { this.category = value }; this.setType = value => { this.type = value }; this.putExtra = (_key, value) => { this.title = value } }
  Object.assign(Intent, { ACTION_CREATE_DOCUMENT: 'CREATE_DOCUMENT', CATEGORY_OPENABLE: 'OPENABLE', EXTRA_TITLE: 'TITLE' })
  function File(name) { this.length = () => nativeState.temporary.get(name) ?? 0; this.getCanonicalPath = () => name }
  function FileInputStream(name) {
    let position = 0
    this.read = buffer => {
      const count = Math.min(65536, (nativeState.temporary.get(name) ?? 0) - position)
      if (!count) return -1
      for (let i = 0; i < count; i++) buffer[i] = (position + i + 1) % 256
      position += count
      return count
    }
    this.close = () => {}
  }
  const nativePlus = {
    os: { name: 'Android' },
    android: {
      runtimeMainActivity: () => main,
      importClass(name) {
        return { 'android.content.Intent': Intent, 'java.io.File': File, 'java.io.FileInputStream': FileInputStream,
          'java.lang.Byte': { TYPE: 'primitive-byte' },
          'java.lang.reflect.Array': { newInstance(type, size) { assert.equal(type, 'primitive-byte'); assert.equal(size, 65536); return new Array(size) } },
          'android.provider.DocumentsContract': { deleteDocument(_resolver, uri) { nativeState.deleted.push(uri) } } }[name]
      },
      invoke(target, method, ...args) { return target[method](...args) },
      getAttribute: (target, name) => target[name],
    },
    io: {
      convertLocalFileSystemURL: value => value,
      resolveLocalFileSystemURL(value, success) { success({ remove(done) { nativeState.temporary.delete(value); nativeState.removed.push(value); done() } }) },
    },
    downloader: { createDownload(url, options, completed) {
      const task = { url, options, headers: {}, filename: nativeState.returnedFilename ?? options.filename, abort() { this.aborted = true }, setRequestHeader(key, value) { this.headers[key] = value },
        getResponseHeader(key) { return { 'Content-Type': nativeState.type, 'Content-Length': nativeState.length, 'Content-Disposition': "attachment; filename*=UTF-8''%E5%8E%9F%E4%BB%B6.flac" }[key] ?? '' },
        start() { queueMicrotask(() => { nativeState.beforeDownload?.(); if (!this.aborted) { nativeState.temporary.set(options.filename, nativeState.size); completed(this, nativeState.status) } }) },
      }
      nativeState.tasks.push(task)
      return task
    } },
  }
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
    setTimeout(fn) { if (native) queueMicrotask(fn); else timers.push(fn); return timers.length }, clearTimeout() {},
    plus: native ? nativePlus : undefined,
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
    download, http, token, requests, created, revoked, anchors, nativeState, main,
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

  test('Android exports authenticated bytes through the system document picker and removes private temporary files', async () => {
    const h = harness({ browser: false, native: true })
    await h.download('id/with?', 'Track')
    assert.equal(h.nativeState.tasks.length, 1)
    const task = h.nativeState.tasks[0]
    assert.equal(task.url, '/api/v1/me/library/id%2Fwith%3F/original')
    assert.equal(task.headers.Authorization, 'Bearer access-secret-A')
    assert.doesNotMatch(task.url, /secret|token/)
    assert.match(task.options.filename, /^_doc\/original-exports\//)
    assert.equal(h.nativeState.intents[0].action, 'CREATE_DOCUMENT')
    assert.equal(h.nativeState.intents[0].title, '原件.flac')
    assert.deepEqual(h.nativeState.output, [3])
    assert.deepEqual(h.nativeState.written, [1, 2, 3])
    assert.equal(h.nativeState.temporary.size, 0)
    assert.equal(h.main.onActivityResult, null)
  })

  test('Android refreshes 401 once and then exports using the new bearer token', async () => {
    const h = harness({ browser: false, native: true })
    h.nativeState.status = 401
    h.http.setRefreshHandler(async () => { h.token.access = 'access-secret-B'; h.nativeState.status = 200; return true })
    await h.download('track', 'Track')
    assert.deepEqual(h.nativeState.tasks.map(task => task.headers.Authorization), ['Bearer access-secret-A', 'Bearer access-secret-B'])
    assert.equal(h.nativeState.temporary.size, 0)
  })

  test('Android rejects a downloader filename outside the allocated private temporary file', async () => {
    const h = harness({ browser: false, native: true })
    h.nativeState.returnedFilename = '_doc/another-private-file.flac'
    await assert.rejects(h.download('track', 'Track'))
    assert.equal(h.nativeState.intents.length, 0)
    assert.equal(h.nativeState.output.length, 0)
    assert.equal(h.nativeState.temporary.size, 0)
    assert.doesNotMatch(h.nativeState.removed.join(','), /another-private-file/)
  })

  for (const variant of ['cancel', 'copy-failure', 'wrong-type', 'empty', 'truncated', '404', '206']) {
    test(`Android ${variant} never reports a saved original and cleans its temporary file`, async () => {
      const h = harness({ browser: false, native: true })
      if (variant === 'cancel') h.nativeState.cancel = true
      if (variant === 'copy-failure') h.nativeState.failCopy = true
      if (variant === 'wrong-type') h.nativeState.type = 'text/html'
      if (variant === 'empty') h.nativeState.size = 0
      if (variant === 'truncated') h.nativeState.length = '4'
      if (['404', '206'].includes(variant)) h.nativeState.status = Number(variant)
      await assert.rejects(h.download('track', 'Track'))
      assert.equal(h.nativeState.output.length, 0)
      assert.equal(h.nativeState.temporary.size, 0)
      if (variant === 'copy-failure') assert.deepEqual(h.nativeState.deleted, ['content://chosen/document'])
    })
  }

  test('Android account change while choosing a destination cancels the export before copying', async () => {
    const h = harness({ browser: false, native: true })
    h.nativeState.beforePicker = () => h.invalidate()
    await assert.rejects(h.download('track', 'Track'), error => error.code === 'api_context_changed')
    assert.equal(h.nativeState.output.length, 0)
    assert.equal(h.nativeState.temporary.size, 0)
  })

  test('Android account change during a transfer aborts it before opening the picker', async () => {
    const h = harness({ browser: false, native: true })
    h.nativeState.beforeDownload = () => h.invalidate()
    await assert.rejects(h.download('track', 'Track'), error => error.code === 'api_context_changed')
    assert.equal(h.nativeState.tasks[0].aborted, true)
    assert.equal(h.nativeState.intents.length, 0)
    assert.equal(h.nativeState.temporary.size, 0)
  })

  test('Android account change during copying removes the incomplete destination', async () => {
    const h = harness({ browser: false, native: true })
    h.nativeState.size = 65537
    h.nativeState.length = '65537'
    h.nativeState.afterWrite = () => h.invalidate()
    await assert.rejects(h.download('track', 'Track'), error => error.code === 'api_context_changed')
    assert.deepEqual(h.nativeState.output, [65536])
    assert.deepEqual(h.nativeState.deleted, ['content://chosen/document'])
    assert.equal(h.nativeState.temporary.size, 0)
  })

  test('Android restores the previous Activity callback and forwards unrelated activity results', async () => {
    const h = harness({ browser: false, native: true })
    let calls = 0
    const previous = () => { calls++ }
    h.main.onActivityResult = previous
    h.nativeState.beforePicker = () => h.main.onActivityResult(987, -1, {})
    await h.download('track', 'Track')
    assert.equal(calls, 1)
    assert.equal(h.main.onActivityResult, previous)
  })
}
