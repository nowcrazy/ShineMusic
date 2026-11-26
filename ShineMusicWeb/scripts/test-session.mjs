/** A08 behavior regression: run with `node scripts/test-session.mjs`. */
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const ts = require('typescript')
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src')
const flush = async () => { for (let i = 0; i < 15; i++) await Promise.resolve() }
const person = (id = 'A') => ({ id, roles: ['admin'], displayName: id, email: `${id}@test.invalid` })

function setup({ credentials = true } = {}) {
  const storage = new Map(credentials ? [
    ['shine.auth.accessToken', 'access-A'], ['shine.auth.refreshToken', 'refresh-A'],
    ['shine.auth.lastActivityAt', Date.now()],
  ] : [])
  const requests = []
  const timers = new Map()
  const listeners = new Map()
  const themes = { applied: 0, reset: 0 }
  let timerId = 0
  const modules = new Map()
  const eventTarget = {
    addEventListener(name, fn) { listeners.set(name, fn) },
    removeEventListener(name) { listeners.delete(name) },
  }
  const context = vm.createContext({
    console, URL, Headers, Response, Blob, FormData, Date, Promise,
    location: { origin: 'https://music.test' },
    __SM_API_BASE__: '',
    navigator: { onLine: true },
    window: eventTarget,
    document: { ...eventTarget, visibilityState: 'visible' },
    setTimeout(fn, ms) { const id = ++timerId; timers.set(id, { fn, ms }); return id },
    clearTimeout(id) { timers.delete(id) },
    uni: {
      getStorageSync: key => storage.get(key) ?? '',
      setStorageSync: (key, value) => storage.set(key, value),
      removeStorageSync: key => storage.delete(key),
      request(options) { requests.push(options); return { abort() {} } },
      showModal() {},
    },
  })
  const vue = {
    ref: value => ({ value }), shallowRef: value => ({ value }), reactive: value => value,
    computed: getter => ({ get value() { return getter() } }),
  }
  const theme = {
    currentTheme: { value: 'glass' }, DEFAULT_THEME: 'glass', MAX_MASK_OPACITY: 0.85,
    applyTheme() {}, resetCustomTheme() { themes.reset++ }, applyCustomTheme() { themes.applied++ },
  }
  function load(file) {
    const full = path.resolve(root, file.endsWith('.ts') ? file : `${file}.ts`)
    if (modules.has(full)) return modules.get(full).exports
    const module = { exports: {} }
    modules.set(full, module)
    const code = ts.transpileModule(fs.readFileSync(full, 'utf8').replaceAll('import.meta.env', '({})'), {
      compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    }).outputText
    const localRequire = name => {
      if (name === 'vue') return vue
      if (name === '@/locale') return { t: key => key }
      if (name === '@/stores/admin') return { useAdminStore: () => ({ resetBreadcrumbs() {} }) }
      if (name === './catalog') return { mapTrack: value => value }
      if (name === './theme') return theme
      if (name === './user-theme') return { syncRemoteTheme: async () => {} }
      if (name.startsWith('@/')) return load(`${name.slice(2)}.ts`)
      if (name.startsWith('.')) return load(path.relative(root, path.resolve(path.dirname(full), name)))
      throw new Error(`Unexpected dependency ${name}`)
    }
    vm.runInContext(`(function(require,module,exports){${code}\n})`, context, { filename: full })(localRequire, module, module.exports)
    return module.exports
  }
  const session = load('services/session')
  const http = load('services/http')
  const tokens = load('services/tokens')
  const respond = (request, status, data = {}) => request.success({ statusCode: status, data })
  const fail = request => request.fail({ errMsg: 'request:fail timeout' })
  async function authenticate(id = 'A') {
    const p = session.restoreSession()
    await flush()
    respond(requests.at(-1), 200, person(id))
    await p
  }
  return { storage, requests, timers, listeners, themes, context, load, session, http, tokens, respond, fail, authenticate }
}

let passed = 0
async function test(name, fn) {
  await fn()
  passed++
  console.log(`ok ${passed} - ${name}`)
}

await test('offline startup retains tokens, blocks protected work and recovers', async () => {
  const x = setup()
  const restore = x.session.restoreSession()
  await flush()
  x.fail(x.requests[0])
  await restore
  assert.equal(x.session.sessionStatus.value, 'unavailable')
  assert.equal(x.tokens.getRefreshToken(), 'refresh-A')
  assert.equal(x.storage.get('shine.auth.accessToken'), 'access-A')
  await assert.rejects(x.http.http.post('/api/v1/admin/test'), e => e.code === 'session_unavailable')
  assert.equal(x.requests.length, 1)
  await x.authenticate()
  assert.equal(x.session.isAuthenticated.value, true)
  assert.equal(x.timers.size, 0)
})

await test('refresh timeout stays a network error and can be retried', async () => {
  const x = setup()
  await x.authenticate()
  const work = x.http.http.get('/api/v1/admin/test')
  x.respond(x.requests.at(-1), 401)
  await flush()
  x.fail(x.requests.at(-1))
  await assert.rejects(work, e => e.status === 0 && e.code === 'network_error')
  assert.equal(x.tokens.getRefreshToken(), 'refresh-A')
  assert.equal(x.session.isAdmin.value, false)
  await x.authenticate()
  assert.equal(x.session.isAdmin.value, true)
})

await test('refresh 503 retains credentials; explicit refresh 401 ends the session', async () => {
  for (const status of [503, 401]) {
    const x = setup()
    await x.authenticate()
    const work = x.http.http.get('/api/v1/admin/test')
    x.respond(x.requests.at(-1), 401)
    await flush()
    x.respond(x.requests.at(-1), status)
    await assert.rejects(work, e => e.status === status)
    assert.equal(x.tokens.hasSession(), status === 503)
    assert.equal(x.session.sessionStatus.value, status === 503 ? 'unavailable' : 'anonymous')
  }
})

await test('parallel 401s share one refresh and replay once', async () => {
  const x = setup()
  await x.authenticate()
  const first = x.http.http.get('/api/v1/admin/first')
  const firstRequest = x.requests.at(-1)
  const second = x.http.http.get('/api/v1/admin/second')
  x.respond(firstRequest, 401)
  x.respond(x.requests.at(-1), 401)
  await flush()
  assert.equal(x.requests.filter(r => r.url.endsWith('/auth/refresh')).length, 1)
  x.respond(x.requests.at(-1), 200, { accessToken: 'rotated-A', refreshToken: 'rotated-refresh-A' })
  await flush()
  const replay = x.requests.slice(-2)
  assert.ok(replay.every(r => r.header.Authorization === 'Bearer rotated-A'))
  replay.forEach(r => x.respond(r, 200, { ok: true }))
  await Promise.all([first, second])
})

await test('offline logout clears personal data synchronously and resolves immediately', async () => {
  const x = setup()
  await x.authenticate()
  const library = x.load('services/library')
  const privateLibrary = x.load('services/user-library')
  const feedback = x.load('services/feedback')
  const notifications = x.load('services/notifications')
  library.favoriteTracks.value = [{ id: 'private-A' }]
  privateLibrary.myLibraryItems.value = [{ trackId: 'private-A' }]
  feedback.feedbackMap.value = new Map([['private-A', 'like']])
  notifications.unreadCount.value = 8
  await x.session.logout()
  assert.equal(x.tokens.hasSession(), false)
  assert.equal(x.session.currentUser.value, null)
  assert.equal(library.favoriteTracks.value.length, 0)
  assert.equal(privateLibrary.myLibraryItems.value.length, 0)
  assert.equal(feedback.feedbackMap.value.size, 0)
  assert.equal(notifications.unreadCount.value, 0)
  assert.ok(x.themes.reset > 0)
  x.fail(x.requests.at(-1))
  await flush()
})

await test('an anonymous pending restore cannot revive a logged-out session', async () => {
  const x = setup()
  const restore = x.session.restoreSession()
  await flush()
  const oldMe = x.requests.at(-1)
  await x.session.logout()
  x.respond(oldMe, 200, person())
  await restore
  assert.equal(x.session.sessionStatus.value, 'anonymous')
  assert.equal(x.tokens.hasSession(), false)
})

await test('late login A cannot replace a newer login B', async () => {
  const x = setup({ credentials: false })
  const first = x.session.login('A@test.invalid', 'not-a-real-password').catch(e => e)
  const firstRequest = x.requests.at(-1)
  const second = x.session.login('B@test.invalid', 'not-a-real-password')
  x.respond(x.requests.at(-1), 200, { accessToken: 'access-B', refreshToken: 'refresh-B' })
  await flush()
  x.respond(x.requests.at(-1), 200, person('B'))
  await second
  x.respond(firstRequest, 200, { accessToken: 'access-A', refreshToken: 'refresh-A' })
  const error = await first
  assert.equal(error.code, 'session_changed')
  assert.equal(x.session.currentUser.value.id, 'B')
  assert.equal(x.tokens.getAccessToken(), 'access-B')
})

await test('old refresh failure cannot clear B and old refresh success cannot overwrite B', async () => {
  for (const status of [401, 200]) {
    const x = setup()
    await x.authenticate()
    const old = x.http.http.get('/api/v1/admin/test').catch(e => e)
    x.respond(x.requests.at(-1), 401)
    await flush()
    const oldRefresh = x.requests.at(-1)
    const login = x.session.login('B@test.invalid', 'not-a-real-password')
    x.respond(x.requests.at(-1), 200, { accessToken: 'access-B', refreshToken: 'refresh-B' })
    await flush()
    x.respond(x.requests.at(-1), 200, person('B'))
    await login
    x.respond(oldRefresh, status, { accessToken: 'late-A', refreshToken: 'late-refresh-A' })
    await old
    assert.equal(x.session.currentUser.value.id, 'B')
    assert.equal(x.tokens.getAccessToken(), 'access-B')
  }
})

await test('late favorites and optimistic rollback cannot populate another session', async () => {
  const x = setup()
  await x.authenticate()
  const library = x.load('services/library')
  const favorites = library.loadFavorites().catch(e => e)
  const oldList = x.requests.at(-1)
  const favorite = library.toggleFavorite({ id: 'private-A' }).catch(e => e)
  const oldMutation = x.requests.at(-1)
  x.session.endSession()
  x.respond(oldList, 200, { items: [{ id: 'late-private-A' }] })
  x.fail(oldMutation)
  await Promise.all([favorites, favorite])
  assert.equal(library.favoriteTracks.value.length, 0)
  assert.equal(library.favoriteIds.value.size, 0)
})

await test('network recovery retries and idle expiry still clears retained credentials', async () => {
  const x = setup()
  x.session.startSessionRecovery()
  const restore = x.session.restoreSession()
  await flush()
  x.fail(x.requests.at(-1))
  await restore
  x.listeners.get('online')()
  await flush()
  x.respond(x.requests.at(-1), 200, person())
  await flush()
  assert.equal(x.session.isAuthenticated.value, true)
  x.storage.set('shine.auth.lastActivityAt', Date.now() - 31 * 60 * 1000)
  assert.equal(x.session.enforceIdleTimeout(), true)
  assert.equal(x.tokens.hasSession(), false)
})

await test('late authenticated fetch body cannot cross a logout boundary', async () => {
  const x = setup()
  await x.authenticate()
  let release
  x.context.fetch = async () => ({
    status: 200, arrayBuffer: async () => new ArrayBuffer(0), blob: async () => new Blob(),
    formData: async () => new FormData(), text: async () => '',
    json: () => new Promise(resolve => { release = resolve }),
  })
  const response = await x.http.fetchWithSession('/api/v1/admin/test')
  const body = response.json().catch(e => e)
  x.session.endSession()
  release({ privateData: 'A' })
  assert.equal((await body).code, 'session_changed')
})

await test('a missing refresh token does not latch future refresh attempts', async () => {
  const x = setup({ credentials: false })
  assert.equal(await x.http.refreshAccessToken(), false)
  const login = x.session.login('B@test.invalid', 'not-a-real-password')
  x.respond(x.requests.at(-1), 200, { accessToken: 'access-B', refreshToken: 'refresh-B' })
  await flush()
  x.respond(x.requests.at(-1), 200, person('B'))
  await login
  const refresh = x.http.refreshAccessToken()
  await flush()
  x.respond(x.requests.at(-1), 200, { accessToken: 'rotated-B', refreshToken: 'rotated-refresh-B' })
  assert.equal(await refresh, true)
  assert.equal(x.tokens.getAccessToken(), 'rotated-B')
})

await test('logout remote failure cannot clear a new login', async () => {
  const x = setup()
  await x.authenticate()
  await x.session.logout()
  const remoteLogout = x.requests.at(-1)
  const login = x.session.login('B@test.invalid', 'not-a-real-password')
  x.respond(x.requests.at(-1), 200, { accessToken: 'access-B', refreshToken: 'refresh-B' })
  await flush()
  x.respond(x.requests.at(-1), 200, person('B'))
  await login
  x.fail(remoteLogout)
  await flush()
  assert.equal(x.tokens.getAccessToken(), 'access-B')
  assert.equal(x.session.currentUser.value.id, 'B')
})

await test('late wallpaper synchronization never applies a previous account theme', async () => {
  const x = setup()
  await x.authenticate()
  let release
  let started
  x.context.fetch = async () => ({
    status: 200, ok: true, arrayBuffer: async () => new ArrayBuffer(0),
    blob: () => new Promise(resolve => { release = resolve; started = true }),
    formData: async () => new FormData(), text: async () => '', json: async () => ({}),
  })
  const userTheme = x.load('services/user-theme')
  const sync = userTheme.syncRemoteTheme()
  x.respond(x.requests.at(-1), 200, { enabled: true, wallpaperUrl: '/api/v1/me/theme/wallpaper', maskOpacity: 0.4 })
  await flush()
  assert.equal(started, true)
  x.session.endSession()
  release(new Blob(['old-wallpaper']))
  await sync
  assert.equal(x.themes.applied, 0)
})

await test('protected 503 suspends permissions and unavailable sessions still expire', async () => {
  const x = setup()
  await x.authenticate()
  const work = x.http.http.post('/api/v1/admin/test')
  x.respond(x.requests.at(-1), 503)
  await assert.rejects(work, e => e.status === 503)
  assert.equal(x.session.sessionStatus.value, 'unavailable')
  assert.equal(x.tokens.hasSession(), true)
  x.storage.set('shine.auth.lastActivityAt', Date.now() - 31 * 60 * 1000)
  assert.equal(x.session.enforceIdleTimeout(), true)
  assert.equal(x.tokens.hasSession(), false)
})

console.log(`A08 Web behavior: ${passed}/${passed} passed`)
