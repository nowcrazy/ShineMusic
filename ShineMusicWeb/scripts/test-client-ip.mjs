/** Client IP display regression: run with `node scripts/test-client-ip.mjs`. */
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import vm from 'node:vm'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const file = path.join(project, 'src/utils/ip.ts')
const compiled = ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
}).outputText
const module = { exports: {} }
vm.runInNewContext(`(function(module,exports){${compiled}\n})`, {}, { filename: file })(module, module.exports)
const { describeIp } = module.exports

test('IPv4 preserves the recorded public exit without inferring a VPN', () => {
  const ip = describeIp('198.51.100.27')
  assert.equal(ip.kind, 'public')
  assert.equal(ip.label, '公网')
  assert.equal(ip.value, '198.51.100.27')
  assert.equal(ip.display, '198.51.100.27')
  assert.equal(ip.full, '198.51.100.27')
})

for (const address of [
  '2408:844f:1516:a41e:1234:5678:90ab:cdef',
  '2001:db8::1:2:3:4',
  '::ffff:198.51.100.27',
]) {
  test(`complete IPv6 or legacy mapped address is retained: ${address}`, () => {
    const ip = describeIp(address)
    assert.equal(ip.kind, 'public')
    assert.equal(ip.value, address)
    assert.equal(ip.display, address)
    assert.equal(ip.full, address)
    assert.doesNotMatch(ip.display, /…/)
  })
}

for (const raw of ['共享地址 100.100.100.100', 'VPN 100.100.100.100']) {
  test(`shared addresses and historical VPN labels do not claim a proxy: ${raw}`, () => {
    const ip = describeIp(raw)
    assert.equal(ip.kind, 'shared')
    assert.equal(ip.label, '共享地址')
    assert.equal(ip.value, '100.100.100.100')
    assert.equal(ip.display, '100.100.100.100')
    assert.equal(ip.full, '共享地址 100.100.100.100')
  })
}

test('private IPv4 and IPv6 prefixes remain compatible', () => {
  for (const address of ['192.168.1.2', 'fd12:3456:789a::1', 'fe80::1']) {
    const ip = describeIp(`内网 ${address}`)
    assert.equal(ip.kind, 'private')
    assert.equal(ip.label, '内网')
    assert.equal(ip.value, address)
    assert.equal(ip.display, address)
    assert.equal(ip.full, `内网 ${address}`)
  }
})

test('loopback continues to use the local-machine label', () => {
  const ip = describeIp('本机')
  assert.equal(ip.kind, 'loopback')
  assert.equal(ip.label, '本机')
  assert.equal(ip.value, '')
  assert.equal(ip.full, '本机')
})

for (const raw of [null, undefined, '', ' ', '\t\r\n', '-', '—', '  -  ', '  —  ', '内网 -', '共享地址 —', 'VPN ']) {
  test(`missing address is displayed as unknown: ${JSON.stringify(raw)}`, () => {
    const ip = describeIp(raw)
    assert.equal(ip.kind, 'unknown')
    assert.equal(ip.label, '—')
    assert.equal(ip.value, '')
    assert.equal(ip.display, '—')
    assert.equal(ip.full, '')
  })
}

test('whitespace around historical values does not become part of the address', () => {
  const ip = describeIp('  VPN   100.100.100.100  ')
  assert.equal(ip.kind, 'shared')
  assert.equal(ip.value, '100.100.100.100')
  assert.equal(describeIp(' 198.51.100.27 ').display, '198.51.100.27')
})
