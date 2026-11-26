/**
 * Native music notification regressions against the real service module.
 * Run: node scripts/test-music-notification.mjs
 */
import assert from 'node:assert/strict'
import fs from 'node:fs'
import vm from 'node:vm'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import ts from 'typescript'

const filename = fileURLToPath(new URL('../src/services/music-notification.ts', import.meta.url))
const { outputText } = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
  fileName: filename,
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
})

const track = {
  id: 'track-1', title: 'Song', artist: 'Artist',
  cover: 'https://music.test/cover.jpg', duration: 123.456,
}

function harness({ enabled = true, omitFlag = false, platform = 'Android', loadPlugin } = {}) {
  const requests = []
  const events = new Map()
  const calls = { init: [], update: [], state: [], position: [], cancel: 0 }
  let createCalls = 0
  let created
  let now = 10_000
  const plugin = {
    init: value => calls.init.push({ ...value }),
    createNotification(callback) { createCalls++; created = callback },
    update: value => calls.update.push({ ...value }),
    playOrPause: value => calls.state.push(value),
    setPosition: value => calls.position.push(value),
    cancel: () => { calls.cancel++ },
  }
  const module = { exports: {} }
  const context = vm.createContext({
    module, exports: module.exports,
    ...(omitFlag ? {} : { __SM_MUSIC_NOTIFICATION_ENABLED__: enabled }),
    uni: {
      requireNativePlugin(name) {
        requests.push(name)
        return loadPlugin ? loadPlugin(plugin) : plugin
      },
    },
    plus: {
      os: { name: platform },
      globalEvent: {
        addEventListener(name, callback) {
          assert.equal(events.has(name), false, `duplicate event listener: ${name}`)
          events.set(name, callback)
        },
      },
    },
    Date: class extends Date { static now() { return now } },
  })
  vm.runInContext(outputText, context, { filename })
  const actions = []
  const handlers = {
    onToggle: () => actions.push('toggle'),
    onPrevious: () => actions.push('previous'),
    onNext: () => actions.push('next'),
    onClose: () => actions.push('close'),
    onSeek: seconds => actions.push(['seek', seconds]),
  }
  return {
    api: module.exports, handlers, requests, events, calls, actions,
    get createCalls() { return createCalls },
    finishCreating: () => created(),
    advance: milliseconds => { now += milliseconds },
  }
}

function exerciseAllEntrypoints(h) {
  h.api.initMusicNotification(h.handlers)
  h.api.updateMusicNotification(track, true)
  h.api.updateMusicNotification(null, false)
  h.api.updateMusicNotificationState(false)
  h.api.syncMusicNotificationProgress(5)
  h.api.cancelMusicNotification()
  h.api.resetMusicNotification()
}

for (const settings of [
  { name: 'an unconfigured plugin', enabled: false },
  { name: 'a stale build without an injected feature flag', omitFlag: true },
  { name: 'iOS', platform: 'iOS' },
]) {
  test(`${settings.name} never probes the Android plugin`, () => {
    const h = harness(settings)
    assert.doesNotThrow(() => {
      exerciseAllEntrypoints(h)
      exerciseAllEntrypoints(h)
    })
    assert.deepEqual(h.requests, [])
    assert.equal(h.events.size, 0)
    assert.deepEqual(h.calls, { init: [], update: [], state: [], position: [], cancel: 0 })
  })
}

for (const [name, loadPlugin] of [
  ['null', () => null],
  ['undefined', () => undefined],
  ['an unavailable module object', () => ({})],
  ['a native bridge error', () => { throw new Error('plugin not in this base') }],
  ['an initialization error', plugin => ({ ...plugin, init() { throw new Error('init failed') } })],
]) {
  test(`${name} is probed only once across playback and session resets`, () => {
    const h = harness({ loadPlugin })
    assert.doesNotThrow(() => {
      exerciseAllEntrypoints(h)
      exerciseAllEntrypoints(h)
    })
    assert.deepEqual(h.requests, ['XZH-musicNotification'])
    assert.equal(h.events.size, 0)
    assert.deepEqual(h.calls.update, [])
    assert.deepEqual(h.calls.state, [])
    assert.deepEqual(h.calls.position, [])
    assert.equal(h.calls.cancel, 0)
  })
}

test('cleanup before playback never loads or creates a native notification', () => {
  const h = harness()
  h.api.cancelMusicNotification()
  h.api.resetMusicNotification()
  assert.deepEqual(h.requests, [])
  assert.equal(h.createCalls, 0)
  assert.equal(h.calls.cancel, 0)
})

test('an available plugin initializes once and receives metadata only after creation', () => {
  const h = harness()
  h.api.initMusicNotification(h.handlers)
  h.api.initMusicNotification(h.handlers)
  h.api.updateMusicNotification({ ...track, title: 'Previous song' }, false)
  h.api.updateMusicNotification(track, true)
  assert.deepEqual(h.calls.init, [{ path: '/pages/home/index', icon: '' }])
  assert.equal(h.createCalls, 1)
  assert.deepEqual(h.calls.update, [])
  assert.deepEqual(h.calls.state, [])

  h.finishCreating()
  assert.deepEqual(h.calls.update, [{
    songName: 'Song', artistsName: 'Artist', picUrl: track.cover,
    duration: 123456, favour: false,
  }])
  assert.deepEqual(h.calls.state, [true])
  h.api.updateMusicNotificationState(false)
  assert.deepEqual(h.calls.state, [true, false])
  assert.equal(h.calls.update.length, 1, 'a state change must not resend metadata')

  h.api.syncMusicNotificationProgress(1.25)
  h.advance(1000)
  h.api.syncMusicNotificationProgress(2.25)
  h.advance(1000)
  h.api.syncMusicNotificationProgress(3.25)
  assert.deepEqual(h.calls.position, [1250, 3250])

  h.events.get('musicNotificationPause')()
  h.events.get('musicNotificationPrevious')()
  h.events.get('musicNotificationNext')()
  h.events.get('musicNotificationClose')()
  h.events.get('musicSeekTo')({ position: 12 })
  assert.deepEqual(h.actions, ['toggle', 'previous', 'next', 'close', ['seek', 12]])
  h.api.cancelMusicNotification()
  assert.equal(h.calls.cancel, 1)
  assert.deepEqual(h.requests, ['XZH-musicNotification'])
})

test('a session reset clears queued metadata and detaches previous player handlers', () => {
  const h = harness()
  h.api.initMusicNotification(h.handlers)
  h.api.updateMusicNotification(track, true)
  h.api.resetMusicNotification()
  h.finishCreating()
  h.events.get('musicNotificationPause')()
  assert.deepEqual(h.calls.update, [])
  assert.deepEqual(h.actions, [])
  assert.equal(h.calls.cancel, 1)

  const nextActions = []
  h.api.initMusicNotification({ ...h.handlers, onToggle: () => nextActions.push('toggle') })
  h.api.updateMusicNotification({ ...track, title: 'New session song' }, true)
  h.events.get('musicNotificationPause')()
  assert.equal(h.calls.update[0].songName, 'New session song')
  assert.deepEqual(nextActions, ['toggle'])
  assert.deepEqual(h.actions, [])
  assert.equal(h.createCalls, 1)
  assert.deepEqual(h.requests, ['XZH-musicNotification'])
})
