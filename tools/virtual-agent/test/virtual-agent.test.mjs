/*
 * Copyright 2026 QRun.IO, Inc. Licensed under the MIT License (see LICENSE).
 * SPDX-License-Identifier: MIT
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { Agent, LIMITS, defaultOutDir, defaultStateFile, backoff, checkTransport, compareVersions, frame, outputName, parseArgs, requirePrinting, sessionCookie, verifyDocument } from '../virtual-agent.mjs'

// Stable native-major boundary from qqq-print-protocol/tools/verify-realtime.mjs.
test('discovery accepts stable compatible minors and rejects noncanonical versions', () => {
  for (const version of ['2.0.0', '2.1.0', '2.7.3', '2.10.0']) {
    assert.equal(requirePrinting({ extensions: [{ name: 'printing', version }] }).version, version)
  }
  for (const version of ['1.0.0', '1.1.0', '3.0.0', '2.0', '2', '2.01.0', '02.0.0', '2.0.00', '2.0.0-rc.1', '2.0.0+build', '2junk.0.0', '2.0.0.1', ' 2.0.0', '2.0.0\n', '', null, undefined, 2]) {
    assert.throws(() => requirePrinting({ extensions: [{ name: 'printing', version }] }), undefined, String(version))
  }
  assert.ok(compareVersions('2.9007199254740993.0', '2.9007199254740992.0') > 0)
})

/** Own only ephemeral loopback fixtures; no QQQ server, spooler or account is used. */
async function listen(t, handler) {
  const server = createServer(handler)
  t.after(() => new Promise((resolve, reject) => {
    server.close((error) => error ? reject(error) : resolve())
    server.closeAllConnections()
  }))
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  return `http://127.0.0.1:${server.address().port}`
}

/** Match the native localhost cookie/Origin boundary without changing its policy. */
test('native cookie POSTs carry exact Origin; foreign targets and redirects receive no credentials', { timeout: 10000 }, async (t) => {
  let foreignRequests = 0
  const foreign = await listen(t, (_request, response) => {
    foreignRequests++
    response.end('{}')
  })
  let accepted = 0
  let origin
  origin = await listen(t, (request, response) => {
    if (request.url === '/redirect') {
      response.writeHead(302, { location: `${foreign}/capture` })
      response.end()
      return
    }
    if (request.headers.cookie && !['GET', 'HEAD', 'OPTIONS'].includes(request.method) && request.headers.origin !== origin) {
      response.writeHead(403)
      response.end('{}')
      return
    }
    assert.equal(request.headers.cookie, 'sessionUUID=synthetic-only')
    if (request.method === 'POST') assert.equal(request.headers.origin, origin)
    accepted++
    response.end('{}')
  })
  const directory = mkdtempSync(join(tmpdir(), 'qqq-virtual-http-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  // the home directory is the temporary one too, so the identity never lands in the real home
  const agent = new Agent(parseArgs(['--server', origin, '--out', directory], {}, directory), () => {})
  agent.cookie = 'sessionUUID=synthetic-only'
  for (const badOrigin of [undefined, foreign]) {
    const headers = { Cookie: agent.cookie }
    if (badOrigin) headers.Origin = badOrigin
    const response = await fetch(`${origin}/native`, { method: 'POST', headers, redirect: 'error' })
    assert.equal(response.status, 403)
    await response.text()
  }
  for (const path of ['/qqq/v1/processes/fixture', '/qqq/v1/qbit/printing/devices/enroll', '/qqq/v1/qbit/printing/device/jobs/claim']) {
    await agent.request('POST', path, { body: {} })
  }
  assert.equal(accepted, 3)
  await assert.rejects(agent.request('POST', `${foreign}/capture`, { body: {} }), /origin/i)
  await assert.rejects(agent.request('POST', `//127.0.0.1:${new URL(foreign).port}/capture`, { body: {} }), /origin/i)
  await assert.rejects(agent.request('GET', '/redirect'))
  assert.equal(foreignRequests, 0)
})

test('requires printing >=2.0.0 <3.0.0', () => {
  assert.equal(requirePrinting({ extensions: [{ name: 'printing', version: '2.0.0' }] }).version, '2.0.0')
  assert.throws(() => requirePrinting({ extensions: [{ name: 'printing', version: '1.0.0' }] }), /not supported/)
  assert.throws(() => requirePrinting({ extensions: [{ name: 'printing', version: '3.1.0' }] }), /not supported/)
  assert.throws(() => requirePrinting({ extensions: [{ name: 'esb', version: '1.1.0' }] }), /installed: esb/)
  assert.ok(compareVersions('2.10.0', '2.9.0') > 0)
})

test('plain http only for loopback', () => {
  checkTransport('http://localhost:8078')
  checkTransport('http://127.0.0.1:8078')
  checkTransport('https://print.example.com')
  assert.throws(() => checkTransport('http://print.example.com'), /https/)
})

test('verifies length, hash and PDF magic', () => {
  const bytes = Buffer.from('%PDF-1.7 test')
  const job = { format: 'PDF', contentLength: bytes.length, contentSha256: createHash('sha256').update(bytes).digest('hex') }
  verifyDocument(job, bytes)
  assert.throws(() => verifyDocument({ ...job, contentLength: 3 }, bytes), /length/)
  assert.throws(() => verifyDocument({ ...job, contentSha256: '0'.repeat(64) }, bytes), /SHA-256/)
  const zpl = Buffer.from('^XA^XZ')
  assert.throws(() => verifyDocument({ ...job, contentLength: zpl.length, contentSha256: createHash('sha256').update(zpl).digest('hex') }, zpl), /not a PDF/)
})

test('output names are safe and keep the job id', () => {
  const name = outputName({ jobId: 'j-1', title: 'Person: Ada / Lovelace?', format: 'PDF' }, new Date('2026-11-01T10:00:00Z'))
  assert.equal(name, '2026-11-01T10-00-00-000Z_j-1_Person-Ada-Lovelace.pdf')
  assert.match(outputName({ jobId: 'j-2', title: '', format: 'ZPL' }), /_j-2_document\.zpl$/)
})

test('frames, cookies, backoff, limits and arguments', () => {
  const f = frame('PING', {}, 'm-1')
  assert.deepEqual(f, { type: 'PING', messageId: 'm-1', payload: {} })
  assert.equal(sessionCookie(['other=1', 'sessionUUID=abc%3D; Path=/'], 'sessionUUID'), 'abc=')
  assert.equal(sessionCookie([], 'sessionId'), undefined)
  assert.equal(backoff(0, {}, () => 0.5), 1000)
  assert.equal(backoff(10, {}, () => 1), 30000)
  assert.equal(LIMITS.maxConcurrentPrints, 1)
  const o = parseArgs(['--server', 'http://localhost:8078/x', '--out', '/tmp/qqq-va-no-such-dir', '--enroll', '--formats', 'pdf'], {}, '/home/u')
  assert.equal(o.server, 'http://localhost:8078')
  assert.equal(o.state, '/home/u/.qqq-print-agent/state.json')
  assert.deepEqual(o.formats, ['PDF'])
  assert.ok(o.enroll && !o.approve)
  assert.throws(() => parseArgs(['--formats', 'PS'], {}), /PDF/)
  assert.throws(() => parseArgs(['--bogus'], {}), /unknown/)
})

test('documents go to ~/Downloads unless --out or QQQ_PRINT_OUT says otherwise; an older identity next to the output is still used', () => {
  assert.equal(defaultOutDir('/home/u'), join('/home/u', 'Downloads'))
  assert.equal(defaultStateFile('/home/u'), join('/home/u', '.qqq-print-agent', 'state.json'))
  assert.equal(parseArgs([], {}, '/home/u').out, join('/home/u', 'Downloads'))
  assert.equal(parseArgs([], { QQQ_PRINT_OUT: '' }, '/home/u').out, join('/home/u', 'Downloads'))
  assert.equal(parseArgs([], { QQQ_PRINT_OUT: '/srv/printed' }, '/home/u').out, '/srv/printed')
  assert.equal(parseArgs(['--out', '/x/y'], { QQQ_PRINT_OUT: '/srv/printed' }, '/home/u').out, '/x/y')
  const directory = mkdtempSync(join(tmpdir(), 'qqq-va-legacy-'))
  try {
    writeFileSync(join(directory, '.agent-state.json'), '{}')
    assert.equal(parseArgs(['--out', directory], {}, '/home/u').state, join(directory, '.agent-state.json'))
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
