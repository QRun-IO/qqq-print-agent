/*
 * Copyright 2026 QRun.IO, Inc. Licensed under the MIT License (see LICENSE).
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { test } from 'node:test'
import { LIMITS, backoff, checkTransport, compareVersions, frame, outputName, parseArgs, requirePrinting, sessionCookie, verifyDocument } from '../virtual-agent.mjs'

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
  const o = parseArgs(['--server', 'http://localhost:8078/x', '--out', '/tmp/out', '--enroll', '--formats', 'pdf'], {})
  assert.equal(o.server, 'http://localhost:8078')
  assert.equal(o.state, '/tmp/out/.agent-state.json')
  assert.deepEqual(o.formats, ['PDF'])
  assert.ok(o.enroll && !o.approve)
  assert.throws(() => parseArgs(['--formats', 'PS'], {}), /PDF/)
  assert.throws(() => parseArgs(['--bogus'], {}), /unknown/)
})
