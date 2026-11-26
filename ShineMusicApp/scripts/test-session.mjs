/**
 * A08 regression checks against the real TypeScript service modules.
 * Uses the project's existing TypeScript compiler and Node built-ins: no test
 * runner, browser, backend, or additional packages are required.
 *
 * Run: node scripts/test-session.mjs
 */
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import ts from 'typescript'
import * as Vue from 'vue'

const servicesDir = fileURLToPath(new URL('../src/services/', import.meta.url))
const activeSessions = new Set()
const origin = 'https://session.test'
const ok = (data = {}, status = 200) => ({ status, data })
const rejected = (status, code = 'authentication_failed') => ok({ error: { code, message: code } }, status)
const user = id => ({ id, email: `${id}@example.test`, displayName: id, roles: ['user'] })
const tokenPair = id => ({ accessToken: `access-${id}`, refreshToken: `refresh-${id}` })
const deferred = () => {
  let resolve, reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const observe = promise => promise.then(value => ({ value }), error => ({ error }))

async function until(predicate, description) {
  for (let turn = 0; turn < 100; turn++) {
    if (predicate()) return
    await new Promise(resolve => setImmediate(resolve))
  }
  throw new Error(`Timed out waiting for ${description}`)
}

function harness({ libraryHttp, realVueAndCollections = false } = {}) {
  const storage = new Map()
  const requests = []
  const resets = new Map()
  const cache = new Map()
  const currentTheme = { value: 'custom' }
  let themeSyncs = 0
  let respond = request => { throw new Error(`Unexpected request: ${request.method} ${request.pathname}`) }
  const reset = name => () => resets.set(name, (resets.get(name) ?? 0) + 1)
  const stubs = {
    vue: {
      reactive: value => value,
      ref: value => ({ value }),
      computed: get => ({ get value() { return get() } }),
      watch: () => () => {},
    },
    '@/locale': { t: key => key },
    './catalog': { mapTrack: value => value, resetCatalogCaches: reset('catalog') },
    './capabilities': {
      resetCapabilities: reset('capabilities'),
      useCapabilities: () => ({ capabilities: { editorialCollections: false } }),
    },
    './feedback': { resetFeedback: reset('feedback') },
    './notifications': { resetNotifications: reset('notifications') },
    './recommend': { resetDailyRecommendations: reset('recommendations') },
    './saved-collections': { resetSavedCollections: reset('savedCollections') },
    './user-library': { resetMyLibrary: reset('myLibrary') },
    './user-theme': { syncRemoteTheme: async () => { themeSyncs++ } },
    './theme': {
      DEFAULT_THEME: 'glass', currentTheme,
      resetCustomTheme: reset('customTheme'),
      applyTheme: value => { currentTheme.value = value },
    },
  }
  if (realVueAndCollections) {
    stubs.vue = Vue
    delete stubs['./saved-collections']
  }
  const context = vm.createContext({
    URL, console, setTimeout, clearTimeout, setInterval, clearInterval, queueMicrotask,
    __SM_API_BASE__: origin,
    location: { origin },
    uni: {
      getStorageSync: key => storage.get(key) ?? '',
      setStorageSync: (key, value) => storage.set(key, value),
      removeStorageSync: key => storage.delete(key),
      getSystemInfoSync: () => ({ platform: 'test' }),
      showModal: () => {},
      request(options) {
        const request = { ...options, pathname: new URL(options.url).pathname, aborted: false }
        requests.push(request)
        // Deliberately still deliver callbacks after abort: platforms may race
        // cancellation, so the real HTTP context check must reject stale work.
        Promise.resolve().then(() => respond(request)).then(
          result => options.success({ statusCode: result.status, data: result.data }),
          error => options.fail({ errMsg: error.message ?? 'offline' }),
        )
        return { abort: () => { request.aborted = true } }
      },
    },
  })
  function load(name, owner = servicesDir) {
    if (Object.hasOwn(stubs, name)) return stubs[name]
    const filename = name.startsWith('@/')
      ? path.resolve(servicesDir, '..', `${name.slice(2)}.ts`)
      : path.resolve(owner, name.endsWith('.ts') ? name : `${name}.ts`)
    if (cache.has(filename)) return cache.get(filename).exports
    const module = { exports: {} }
    cache.set(filename, module)
    const source = fs.readFileSync(filename, 'utf8').replaceAll('import.meta.env', '({})')
    const { outputText } = ts.transpileModule(source, {
      fileName: filename,
      compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
    })
    const require = specifier => {
      if (libraryHttp && filename === path.join(servicesDir, 'library.ts') && specifier === './http') {
        const actual = load(specifier, path.dirname(filename))
        return { ...actual, http: { ...actual.http, ...libraryHttp } }
      }
      return load(specifier, path.dirname(filename))
    }
    vm.runInContext(`(function(require,module,exports){${outputText}\n})`, context, { filename })(require, module, module.exports)
    return module.exports
  }
  const http = load('./http')
  const tokens = load('./tokens')
  const session = load('./session')
  activeSessions.add(session)
  const library = load('./library')
  load('./user-state')
  function seed(id = 'A') {
    const pair = tokenPair(id)
    tokens.saveTokens(pair.accessToken, pair.refreshToken, origin)
  }
  async function authenticate(id = 'A') {
    respond = request => {
      if (request.pathname === '/api/v1/auth/login') return ok(tokenPair(id))
      if (request.pathname === '/api/v1/me') return ok(user(id))
      if (request.pathname === '/api/v1/auth/logout') return ok()
      throw new Error(`Unexpected request ${request.pathname}`)
    }
    await session.login(`${id}@example.test`, 'test-password')
    assert.equal(session.currentUser.value?.id, id)
  }
  return {
    http, tokens, session, library, storage, requests, resets, seed, authenticate,
    savedCollections: realVueAndCollections ? load('./saved-collections') : null,
    get themeSyncs() { return themeSyncs },
    setRespond: handler => { respond = handler },
  }
}

test.afterEach(() => {
  for (const session of activeSessions) session.endSession()
  activeSessions.clear()
})

test('offline startup retains credentials and blocks authenticated actions', async () => {
  const h = harness()
  h.seed()
  h.setRespond(() => { throw new Error('offline') })
  await h.session.restoreSession()
  assert.equal(h.tokens.getRefreshToken(), 'refresh-A')
  assert.equal(h.storage.get('shine.auth.accessToken'), 'access-A')
  assert.equal(h.session.sessionStatus.value, 'unavailable')
  assert.equal(h.session.isAuthenticated.value, false)
  assert.equal(h.session.currentUser.value, null)
  const before = h.requests.length
  await assert.rejects(h.http.http.get('/api/v1/me/favorites'))
  assert.equal(h.requests.length, before, 'the guard must block protected requests before dispatch')
})

test('startup 503 retains the recoverable session', async () => {
  const h = harness()
  h.seed()
  h.setRespond(() => rejected(503, 'service_unavailable'))
  await h.session.restoreSession()
  assert.equal(h.tokens.getRefreshToken(), 'refresh-A')
  assert.equal(h.session.sessionStatus.value, 'unavailable')
  assert.equal(h.session.isAuthenticated.value, false)
})

for (const failure of ['network', '503']) {
  test(`ordinary protected request ${failure} suspends permissions but keeps credentials`, async () => {
    const h = harness()
    await h.authenticate()
    h.library.favoriteTracks.value = [{ id: 'private-A' }]
    h.setRespond(() => {
      if (failure === 'network') throw new Error('offline')
      return rejected(503, 'temporary_failure')
    })
    await assert.rejects(h.http.http.get('/api/v1/me/playlists'), error => error.status === (failure === 'network' ? 0 : 503))
    assert.equal(h.session.sessionStatus.value, 'unavailable')
    assert.equal(h.session.isAuthenticated.value, false)
    assert.equal(h.session.currentUser.value, null)
    assert.equal(h.tokens.getRefreshToken(), 'refresh-A')
    assert.equal(h.library.favoriteTracks.value.length, 0)
  })
}

test('refresh 503 propagates the real error instead of the original 401', async () => {
  const h = harness()
  await h.authenticate()
  h.setRespond(request => request.pathname === '/api/v1/auth/refresh'
    ? rejected(503, 'refresh_unavailable') : rejected(401))
  await assert.rejects(h.http.http.get('/api/v1/me/favorites'), error => error.status === 503 && error.code === 'refresh_unavailable')
  assert.equal(h.tokens.getRefreshToken(), 'refresh-A')
  assert.equal(h.session.sessionStatus.value, 'unavailable')
  assert.equal(h.session.isAuthenticated.value, false)
})

test('refresh network failure retains tokens and reports a network error', async () => {
  const h = harness()
  await h.authenticate()
  h.setRespond(request => {
    if (request.pathname === '/api/v1/auth/refresh') throw new Error('refresh offline')
    return rejected(401)
  })
  await assert.rejects(h.http.http.get('/api/v1/me/favorites'), error => error.status === 0 && error.code === 'network_error')
  assert.equal(h.tokens.getRefreshToken(), 'refresh-A')
  assert.equal(h.session.sessionStatus.value, 'unavailable')
})

test('refresh without local credentials does not poison a later valid refresh', async () => {
  const h = harness()
  assert.equal(await h.http.refreshAccessToken(), false)
  assert.equal(await h.http.refreshAccessToken(), false)
  assert.equal(h.requests.length, 0)
  h.seed()
  let refreshes = 0
  h.setRespond(() => { refreshes++; return ok(tokenPair(`rotated-${refreshes}`)) })
  assert.equal(await h.http.refreshAccessToken(), true)
  assert.equal(await h.http.refreshAccessToken(), true)
  assert.equal(refreshes, 2, 'a completed single-flight promise must be released')
  assert.equal(h.tokens.getRefreshToken(), 'refresh-rotated-2')
})

test('explicit refresh rejection ends the session and clears personal state', async () => {
  const h = harness()
  await h.authenticate()
  h.library.favoriteIds.value = new Set(['private-A'])
  const resetCount = h.resets.get('notifications') ?? 0
  h.setRespond(() => rejected(401))
  await assert.rejects(h.http.http.get('/api/v1/me/favorites'))
  assert.equal(h.tokens.hasSession(), false)
  assert.equal(h.session.sessionStatus.value, 'anonymous')
  assert.equal(h.session.currentUser.value, null)
  assert.equal(h.library.favoriteIds.value.size, 0)
  assert.ok(h.resets.get('notifications') > resetCount)
})

test('access-token-only 401 clears the session without attempting refresh', async () => {
  const h = harness()
  await h.authenticate()
  h.tokens.saveTokens('access-A', '', origin)
  h.setRespond(() => rejected(401))
  const before = h.requests.length
  await assert.rejects(h.http.http.get('/api/v1/me/playlists'), error => error.status === 401)
  assert.equal(h.requests.length, before + 1)
  assert.equal(h.tokens.hasSession(), false)
  assert.equal(h.session.sessionStatus.value, 'anonymous')
  assert.equal(h.session.currentUser.value, null)
})

test('a replayed request returning 401 after a successful refresh clears the session', async () => {
  const h = harness()
  await h.authenticate()
  let refreshes = 0
  let attempts = 0
  h.setRespond(request => {
    if (request.pathname === '/api/v1/auth/refresh') { refreshes++; return ok(tokenPair('rotated')) }
    attempts++
    return rejected(401)
  })
  await assert.rejects(h.http.http.get('/api/v1/me/playlists'), error => error.status === 401)
  assert.equal(refreshes, 1)
  assert.equal(attempts, 2)
  assert.equal(h.tokens.hasSession(), false)
  assert.equal(h.session.sessionStatus.value, 'anonymous')
})

test('offline logout clears all local personal state immediately and resolves', async () => {
  const h = harness()
  await h.authenticate()
  h.library.favoriteIds.value = new Set(['private-A'])
  h.library.favoriteTracks.value = [{ id: 'private-A' }]
  const before = new Map(h.resets)
  const remote = deferred()
  h.setRespond(() => remote.promise)
  const logout = h.session.logout()
  assert.equal(h.tokens.hasSession(), false)
  assert.equal(h.session.currentUser.value, null)
  assert.equal(h.session.isAuthenticated.value, false)
  assert.equal(h.library.favoriteTracks.value.length, 0)
  for (const name of ['myLibrary', 'feedback', 'notifications', 'savedCollections', 'recommendations', 'customTheme']) {
    assert.ok(h.resets.get(name) > (before.get(name) ?? 0), `${name} should clear synchronously`)
  }
  remote.reject(new Error('offline'))
  await assert.doesNotReject(logout)
})

test('logout resolves before remote completion and late remote failure cannot clear account B', async () => {
  const h = harness()
  await h.authenticate()
  const remote = deferred()
  h.setRespond(() => remote.promise)
  let finished = false
  const logout = h.session.logout().then(() => { finished = true })
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(finished, true, 'local logout must not await a slow revocation endpoint')
  assert.equal(h.tokens.hasSession(), false)
  await h.authenticate('B')
  remote.reject(new Error('late logout network failure'))
  await logout
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(h.session.currentUser.value?.id, 'B')
  assert.equal(h.tokens.getRefreshToken(), 'refresh-B')
})

test('real Vue synchronous collection watcher initializes without a session import cycle', async () => {
  const h = harness({ realVueAndCollections: true })
  await h.authenticate()
  h.savedCollections.savedCollections.value = [{ id: 'private-A' }]
  h.session.endSession()
  assert.equal(h.savedCollections.savedCollections.value.length, 0)
  assert.equal(h.session.isAuthenticated.value, false)
})

test('network recovery revalidates a suspended session and syncs its theme', async () => {
  const h = harness()
  h.seed()
  h.setRespond(() => { throw new Error('offline') })
  await h.session.restoreSession()
  h.setRespond(() => ok(user('A')))
  await h.session.resumeSession()
  assert.equal(h.session.currentUser.value?.id, 'A')
  assert.equal(h.session.isAuthenticated.value, true)
  assert.equal(h.tokens.getRefreshToken(), 'refresh-A')
  assert.equal(h.themeSyncs, 1, 'normal identity adoption must not be mistaken for stale work')
  const before = h.requests.length
  await h.session.resumeSession()
  assert.equal(h.requests.length, before, 'an authenticated session needs no resume request')
})

test('concurrent recovery calls share one identity verification', async () => {
  const h = harness()
  h.seed()
  h.setRespond(() => { throw new Error('offline') })
  await h.session.restoreSession()
  const recovery = deferred()
  let verifications = 0
  h.setRespond(() => { verifications++; return recovery.promise })
  const first = h.session.resumeSession()
  const second = h.session.resumeSession()
  await until(() => verifications > 0, 'recovery verification')
  assert.equal(verifications, 1)
  recovery.resolve(ok(user('A')))
  await Promise.all([first, second])
  assert.equal(h.session.isAuthenticated.value, true)
  assert.equal(verifications, 1)
})

test('concurrent 401 responses share one refresh and retry with the new token', async () => {
  const h = harness()
  await h.authenticate()
  const refresh = deferred()
  let refreshCount = 0
  h.setRespond(request => {
    if (request.pathname === '/api/v1/auth/refresh') { refreshCount++; return refresh.promise }
    return request.header.Authorization === 'Bearer access-new' ? ok({ success: true }) : rejected(401)
  })
  const first = h.http.http.get('/api/v1/me/favorites')
  const second = h.http.http.get('/api/v1/me/playlists')
  await until(() => refreshCount > 0, 'shared refresh')
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(refreshCount, 1)
  refresh.resolve(ok(tokenPair('new')))
  const values = await Promise.all([first, second])
  assert.ok(values.every(value => value.success === true))
  assert.equal(refreshCount, 1)
  assert.equal(h.tokens.getRefreshToken(), 'refresh-new')
})

test('a late login from the anonymous phase cannot replace a newer account', async () => {
  const h = harness()
  const old = deferred()
  h.setRespond(() => old.promise)
  const result = observe(h.session.login('A@example.test', 'test-password'))
  await until(() => h.requests.length === 1, 'old login dispatch')
  await h.session.logout()
  await h.authenticate('B')
  old.resolve(ok(tokenPair('A')))
  await result
  assert.equal(h.session.currentUser.value?.id, 'B')
  assert.equal(h.tokens.getRefreshToken(), 'refresh-B')
})

test('a late startup user response cannot resurrect the previous account', async () => {
  const h = harness()
  h.seed()
  const old = deferred()
  h.setRespond(request => request.pathname === '/api/v1/me' ? old.promise : ok())
  const restoration = observe(h.session.restoreSession())
  await until(() => h.requests.some(request => request.pathname === '/api/v1/me'), 'old startup request')
  await h.session.logout()
  await h.authenticate('B')
  old.resolve(ok(user('A')))
  await restoration
  assert.equal(h.session.currentUser.value?.id, 'B')
  assert.equal(h.tokens.getRefreshToken(), 'refresh-B')
})

test('a late anonymous-phase refresh cannot overwrite the next account', async () => {
  const h = harness()
  h.seed()
  const old = deferred()
  h.setRespond(request => request.pathname === '/api/v1/auth/refresh' ? old.promise : ok())
  const refresh = observe(h.http.refreshAccessToken())
  await until(() => h.requests.some(request => request.pathname === '/api/v1/auth/refresh'), 'old refresh dispatch')
  await h.session.logout()
  await h.authenticate('B')
  old.resolve(ok(tokenPair('A-rotated')))
  await refresh
  assert.equal(h.session.currentUser.value?.id, 'B')
  assert.equal(h.tokens.getRefreshToken(), 'refresh-B')
})

test('late favorites cannot refill data cleared by logout or overwrite the next account', async () => {
  const old = deferred()
  let count = 0
  const h = harness({ libraryHttp: { get: () => ++count === 1 ? old.promise : Promise.resolve({ items: [{ id: 'B-track' }], total: 1 }) } })
  await h.authenticate()
  const loading = h.library.loadFavorites()
  await h.session.logout()
  await h.authenticate('B')
  await h.library.loadFavorites()
  old.resolve({ items: [{ id: 'A-track' }], total: 1 })
  await loading
  assert.deepEqual([...h.library.favoriteIds.value], ['B-track'])
  assert.equal(h.library.favoriteTracks.value[0].id, 'B-track')
})

test('an old failed optimistic update cannot roll back the next account favorites', async () => {
  const old = deferred()
  let count = 0
  const h = harness({ libraryHttp: { put: () => ++count === 1 ? old.promise : Promise.resolve({}) } })
  await h.authenticate()
  const pending = observe(h.library.toggleFavorite({ id: 'same-track' }))
  await h.session.logout()
  await h.authenticate('B')
  await h.library.toggleFavorite({ id: 'same-track' })
  old.reject(new Error('old account request failed'))
  assert.ok((await pending).error)
  assert.deepEqual([...h.library.favoriteIds.value], ['same-track'])
  assert.equal(h.library.favoritesTotal.value, 1)
})
