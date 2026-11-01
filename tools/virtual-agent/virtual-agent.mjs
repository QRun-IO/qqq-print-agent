#!/usr/bin/env node
/*
 * Copyright 2026 QRun.IO, Inc. Licensed under the MIT License (see LICENSE).
 * SPDX-License-Identifier: MIT
 */

/**
 * Virtual file printer: a development print agent that speaks the same protocol as the Windows
 * service (printing extension 2.0, wire v1, realtime-v1) but "prints" by writing each verified
 * document to a directory. It lets a QQQ server with the printing QBit be exercised end to end on
 * macOS or Linux without hardware or a Windows spooler.
 *
 *   node tools/virtual-agent/virtual-agent.mjs --server http://localhost:8078 --out ./printed \
 *        [--state <file>] [--enroll] [--approve] [--name "Virtual PDF printer"] [--once]
 *
 * Flow: GET /qqq/v1/version (requires printing >=2.0.0 <3.0.0) -> sign in through the server's
 * advertised authentication (MOCK / FULLY_ANONYMOUS: manageSession; TABLE_BASED: HTTP Basic from
 * QQQ_PRINT_USERNAME / QQQ_PRINT_PASSWORD) -> device identity from --state, or with --enroll an
 * enrollment code from the native "Enroll a print device" process and POST devices/enroll ->
 * the authenticated WebSocket: HELLO, WELCOME, STATE_APPLIED, INVENTORY, HEARTBEAT -> on
 * JOB_AVAILABLE a finite claim drain over HTTPS: RECEIVED, download, length and SHA-256 check,
 * SUBMITTING, write the file, SPOOLED, SPOOLER_COMPLETED. Each JOB_EVENT waits for its ACK.
 *
 * --approve approves the virtual printer through the native "Approve printers" process after the
 * first inventory (a local-testing convenience: approval is normally an administrator's decision).
 * Loopback http/ws only is accepted without TLS. Credentials, device tokens and enrollment codes
 * are never printed; the state file is written with mode 0600.
 */

import { createHash, randomUUID } from 'node:crypto'
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

export const AGENT_VERSION = '0.3.1-virtual'
export const CLIENT_CHANNEL = 'api/qqq-print-agent'
export const SUBPROTOCOL = 'qqq-printing.v1'
export const REALTIME_PATH = '/qqq/v1/qbit/printing/device/realtime'
const BASE = '/qqq/v1/qbit/printing'

/** What this agent can honestly do: one download and one print at a time. */
export const LIMITS = {
  maxCopies: 32767,
  maxPdfBytes: 2147483647,
  maxZplBytes: 2147483647,
  maxFrameBytes: 16777216,
  maxQueuedFrames: 4096,
  maxQueuedFrameBytes: 67108864,
  maxConcurrentDownloads: 1,
  maxConcurrentPrints: 1,
  maxStagedBytes: 9007199254740991,
}

export function parseArgs(argv, env = process.env) {
  const options = {
    server: env.QQQ_PRINT_SERVER ?? 'http://localhost:8078',
    out: env.QQQ_PRINT_OUT ?? './virtual-printer-output',
    state: env.QQQ_PRINT_STATE,
    name: env.QQQ_PRINT_PRINTER_NAME ?? 'Virtual PDF printer',
    deviceName: env.QQQ_PRINT_DEVICE_NAME ?? 'Virtual print agent',
    printerId: 'virtual-file',
    formats: ['PDF', 'ZPL'],
    enroll: false,
    approve: false,
    once: false,
    facility: env.QQQ_PRINT_FACILITY ?? 'default',
  }
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    const value = () => {
      const v = argv[++i]
      if (v === undefined) throw new Error(`${arg} needs a value`)
      return v
    }
    switch (arg) {
      case '--server': options.server = value(); break
      case '--out': options.out = value(); break
      case '--state': options.state = value(); break
      case '--name': options.name = value(); break
      case '--device-name': options.deviceName = value(); break
      case '--printer-id': options.printerId = value(); break
      case '--formats': options.formats = value().split(',').map((f) => f.trim().toUpperCase()).filter(Boolean); break
      case '--facility': options.facility = value(); break
      case '--enroll': options.enroll = true; break
      case '--approve': options.approve = true; break
      case '--once': options.once = true; break
      case '-h': case '--help': options.help = true; break
      default: throw new Error(`unknown option ${arg}`)
    }
  }
  options.server = new URL(options.server).origin
  options.out = resolve(options.out)
  options.state = resolve(options.state ?? join(options.out, '.agent-state.json'))
  if (!options.formats.length || options.formats.some((f) => f !== 'PDF' && f !== 'ZPL')) throw new Error('--formats takes PDF and/or ZPL')
  return options
}

/** http is accepted only for loopback development, as the Windows service does. */
export function checkTransport(server) {
  const url = new URL(server)
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
  if (url.protocol === 'https:') return
  if (url.protocol === 'http:' && loopback) return
  throw new Error(`${server}: use https (plain http is accepted only for a loopback server)`)
}

/** Compare canonical stable SemVer without rounding large numeric components. */
export function compareVersions(a, b) {
  const pa = stableVersionParts(a)
  const pb = stableVersionParts(b)
  for (let i = 0; i < 3; i++) if (pa[i] !== pb[i]) return pa[i] < pb[i] ? -1 : 1
  return 0
}

/** Match the protocol's stable three-part version boundary, including exact end of input. */
function stableVersionParts(version) {
  const match = typeof version === 'string' && /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.exec(version)
  if (!match || match[0] !== version) throw new Error('printing version is not supported: expected a canonical stable three-part version')
  return match.slice(1).map((part) => BigInt(part))
}

/** The realtime contract: printing >=2.0.0 <3.0.0 must be listed by GET /version. */
export function requirePrinting(version) {
  const printing = (version?.extensions ?? []).find((e) => e.name === 'printing')
  if (!printing) throw new Error(`the server does not list the printing extension (installed: ${(version?.extensions ?? []).map((e) => e.name).join(', ') || 'none'})`)
  if (compareVersions(printing.version, '2.0.0') < 0 || compareVersions(printing.version, '3.0.0') >= 0) throw new Error(`printing ${printing.version} is not supported: this agent needs >=2.0.0 <3.0.0 (upgrade the server)`)
  return printing
}

/** The value of a session cookie among Set-Cookie headers. */
export function sessionCookie(setCookies, name) {
  for (const c of setCookies) {
    const m = new RegExp(`^\\s*${name}=([^;]+)`).exec(c)
    if (m) return decodeURIComponent(m[1])
  }
  return undefined
}

/** A protocol frame. */
export function frame(type, payload, messageId = randomUUID()) {
  return { type, messageId, payload }
}

/** A file name for a printed job: time, job id and a slug of its title. */
export function outputName(job, now = new Date()) {
  const stamp = now.toISOString().replace(/[:.]/g, '-')
  const slug = String(job.title ?? 'document').normalize('NFKD').replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 60) || 'document'
  return `${stamp}_${job.jobId}_${slug}.${job.format === 'ZPL' ? 'zpl' : 'pdf'}`
}

/** Checks a downloaded document against the claimed job (length, SHA-256, format magic). */
export function verifyDocument(job, bytes) {
  if (bytes.length !== job.contentLength) throw new Error(`length ${bytes.length} does not match the declared ${job.contentLength}`)
  const sha = createHash('sha256').update(bytes).digest('hex')
  if (sha !== job.contentSha256) throw new Error('SHA-256 does not match the declared content hash')
  if (job.format === 'PDF' && bytes.subarray(0, 5).toString('latin1') !== '%PDF-') throw new Error('the document is not a PDF')
}

/** Reconnect delay: bounded exponential backoff with jitter. */
export function backoff(attempt, settings = {}, random = Math.random) {
  const min = settings.reconnectMinDelayMillis ?? 1000
  const max = settings.reconnectMaxDelayMillis ?? 30000
  const jitter = (settings.reconnectJitterPercent ?? 20) / 100
  const base = Math.min(max, min * 2 ** attempt)
  return Math.round(Math.min(max, Math.max(min, base * (1 - jitter + 2 * jitter * random()))))
}

export class Agent {
  constructor(options, log = (line) => console.log(`${new Date().toISOString()} ${line}`)) {
    this.o = options
    this.log = log
    this.cookie = undefined
    this.state = existsSync(options.state) ? JSON.parse(readFileSync(options.state, 'utf8')) : {}
    this.pending = new Map()
    this.settings = {}
    this.desired = {}
    this.ready = false
    this.wake = false
    this.draining = false
    this.printed = 0
    this.attempt = 0
    this.stopped = false
  }

  saveState() {
    mkdirSync(resolve(this.o.state, '..'), { recursive: true })
    writeFileSync(this.o.state, JSON.stringify(this.state, null, 2) + '\n', { mode: 0o600 })
    chmodSync(this.o.state, 0o600)
  }

  headers(device = false, extra = {}) {
    const h = { 'X-QQQ-Client': CLIENT_CHANNEL, accept: 'application/json', ...extra }
    if (this.cookie) h.Cookie = this.cookie
    if (device) {
      h['X-QQQ-Device-Id'] = this.state.deviceId
      h['X-QQQ-Device-Token'] = this.state.deviceToken
    }
    return h
  }

  async request(method, path, { body, device = false, raw = false, form } = {}) {
    const origin = new URL(this.o.server).origin
    const url = new URL(path, origin)
    if (url.origin !== origin || url.username || url.password) throw new Error('request target must remain on the configured server origin')
    const verb = method.toUpperCase()
    const init = { method: verb, headers: this.headers(device), redirect: 'error' }
    if (this.cookie && !['GET', 'HEAD', 'OPTIONS'].includes(verb)) init.headers.Origin = origin
    if (form) {
      const data = new FormData()
      for (const [k, v] of Object.entries(form)) data.append(k, v)
      init.body = data
    } else if (body !== undefined) {
      init.headers['content-type'] = 'application/json'
      init.body = JSON.stringify(body)
    }
    const response = await fetch(url, init)
    if (!response.ok) {
      const text = await response.text().catch(() => '')
      let message = text
      try { message = JSON.parse(text).message ?? JSON.parse(text).error ?? text } catch { /* not JSON */ }
      const error = new Error(`${method} ${path}: HTTP ${response.status} ${String(message).slice(0, 300)}`)
      error.status = response.status
      throw error
    }
    if (raw) return Buffer.from(await response.arrayBuffer())
    const text = await response.text()
    return text ? JSON.parse(text) : {}
  }

  async signIn() {
    const version = await this.request('GET', '/qqq/v1/version')
    const printing = requirePrinting(version)
    this.log(`server ${this.o.server}: UI API ${version.apiVersion}, printing ${printing.version}`)
    const auth = await this.request('GET', '/qqq/v1/metaData/authentication')
    if (auth.type === 'MOCK' || auth.type === 'FULLY_ANONYMOUS') {
      const response = await fetch(new URL('/qqq/v1/manageSession', this.o.server), { method: 'POST', headers: { 'content-type': 'application/json', 'X-QQQ-Client': CLIENT_CHANNEL }, body: '{}' })
      const uuid = sessionCookie(response.headers.getSetCookie?.() ?? [], 'sessionUUID') ?? (await response.json().catch(() => ({}))).sessionUUID
      if (!response.ok || !uuid) throw new Error(`manageSession gave no session (HTTP ${response.status})`)
      this.cookie = `sessionUUID=${uuid}`
    } else if (auth.type === 'TABLE_BASED') {
      const user = process.env.QQQ_PRINT_USERNAME
      const password = process.env.QQQ_PRINT_PASSWORD
      if (!user || !password) throw new Error('TABLE_BASED sign-in needs QQQ_PRINT_USERNAME and QQQ_PRINT_PASSWORD in the environment')
      const response = await fetch(new URL('/qqq/v1/metaData', this.o.server), { headers: { Authorization: `Basic ${Buffer.from(`${user}:${password}`).toString('base64')}`, 'X-QQQ-Client': CLIENT_CHANNEL } })
      const cookie = sessionCookie(response.headers.getSetCookie?.() ?? [], 'sessionId')
      if (!response.ok || !cookie) throw new Error(`TABLE_BASED sign-in refused (HTTP ${response.status})`)
      this.cookie = `sessionId=${cookie}`
    } else {
      throw new Error(`${auth.type} sign-in is not supported by the virtual agent (use MOCK, FULLY_ANONYMOUS or TABLE_BASED)`)
    }
    const session = await this.request('GET', `${BASE}/session`, this.state.deviceId ? { device: true } : {})
    this.log(`signed in as ${session.principalId} (printing tenant ${session.tenantId}${session.deviceId ? `, device ${session.deviceId}` : ''})`)
  }

  /** Runs a native process to its end, answering each frontend step with the given values. */
  async runProcess(name, values, records) {
    const init = { values: JSON.stringify(values), stepTimeoutMillis: '60000' }
    if (records) Object.assign(init, { recordsParam: 'recordIds', recordIds: records.join(',') })
    let result = await this.request('POST', `/qqq/v1/processes/${name}/init`, { form: init })
    for (let guard = 0; result.nextStep && guard < 10; guard++) {
      if (result.error) throw new Error(`${name}: ${result.error}`)
      if (result.nextStep === 'result') break
      result = await this.request('POST', `/qqq/v1/processes/${name}/${result.processUUID}/step/${result.nextStep}`, { form: { values: JSON.stringify(values), stepTimeoutMillis: '60000' } })
    }
    if (result.error) throw new Error(`${name}: ${result.error}`)
    return result
  }

  async ensureDevice() {
    if (this.state.deviceId && this.state.deviceToken) return
    if (!this.o.enroll) throw new Error(`no device identity in ${this.o.state}; run with --enroll to enroll this agent`)
    this.log(`enrolling "${this.o.deviceName}" (facility ${this.o.facility})`)
    const run = await this.runProcess('printingEnrollDevice', { facilityId: this.o.facility, deviceName: this.o.deviceName })
    const code = /Enrollment code: (\S+)/.exec(String(run.values?.result ?? ''))?.[1]
    if (!code) throw new Error('the enrollment process returned no code')
    const enrolled = await this.request('POST', `${BASE}/devices/enroll`, { body: { enrollmentCode: code, deviceName: this.o.deviceName, agentVersion: AGENT_VERSION } })
    this.state = { deviceId: enrolled.deviceId, deviceToken: enrolled.deviceToken, lastAppliedRevision: 0, lastSettingsRevision: 0 }
    this.saveState()
    this.log(`enrolled as device ${enrolled.deviceId} (identity saved in ${this.o.state})`)
  }

  inventory() {
    return { printers: [{ printerId: this.o.printerId, name: this.o.name, driverName: 'QQQ virtual file printer', portName: `FILE:${this.o.out}`, status: 'READY', formats: this.o.formats, description: `Writes each job to ${this.o.out}`, location: 'This computer' }] }
  }

  send(type, payload) {
    const f = frame(type, payload)
    this.ws.send(JSON.stringify(f))
    return f.messageId
  }

  /** Sends a frame and waits for its ACK (or ERROR). */
  sendAcked(type, payload) {
    const id = this.send(type, payload)
    return new Promise((resolveAck, rejectAck) => {
      const timer = setTimeout(() => { this.pending.delete(id); rejectAck(new Error(`${type}: no ACK`)) }, this.settings.ackTimeoutMillis ?? 15000)
      this.pending.set(id, { resolve: () => { clearTimeout(timer); resolveAck() }, reject: (e) => { clearTimeout(timer); rejectAck(e) } })
    })
  }

  connect() {
    const url = this.o.server.replace(/^http/, 'ws') + REALTIME_PATH
    this.ready = false
    this.ws = new WebSocket(url, { protocols: [SUBPROTOCOL], headers: { ...this.headers(true), Origin: this.o.server } })
    this.ws.onopen = () => {
      if (this.ws.protocol !== SUBPROTOCOL) {
        this.log(`the server did not select ${SUBPROTOCOL}; closing`)
        this.ws.close()
        return
      }
      this.log('realtime connected; HELLO')
      this.send('HELLO', { agentVersion: AGENT_VERSION, capabilities: ['realtime-v1', 'REFRESH_INVENTORY'], lastAppliedRevision: this.state.lastAppliedRevision ?? 0, lastSettingsRevision: this.state.lastSettingsRevision ?? 0, limits: LIMITS })
    }
    this.ws.onmessage = (event) => this.receive(JSON.parse(String(event.data))).catch((e) => this.log(`error: ${e.message}`))
    this.ws.onerror = () => undefined
    this.ws.onclose = (event) => {
      clearInterval(this.heartbeat)
      for (const p of this.pending.values()) p.reject(new Error('connection closed'))
      this.pending.clear()
      if (this.stopped) return
      const delay = backoff(this.attempt++, this.settings)
      this.log(`realtime closed (${event.code}${event.reason ? ` ${event.reason}` : ''}); reconnecting in ${delay} ms`)
      setTimeout(() => this.start().catch((e) => { this.log(`reconnect failed: ${e.message}`); this.ws.onclose({ code: 0, reason: 'sign-in failed' }) }), delay)
    }
  }

  async receive(message) {
    const { type, payload } = message
    switch (type) {
      case 'WELCOME':
        this.settings = payload.settings
        this.desired = payload.desiredState
        await this.apply()
        this.attempt = 0
        this.ready = true
        this.log(`ready (state revision ${this.desired.revision}, settings revision ${this.settings.revision}${this.desired.dispatchPaused ? ', dispatch PAUSED' : ''})`)
        await this.sendAcked('INVENTORY', this.inventory())
        await this.sendAcked('HEARTBEAT', { agentVersion: AGENT_VERSION, profile: { name: this.o.deviceName, description: 'QQQ virtual file printer (development)', location: 'This computer' } })
        this.heartbeat = setInterval(() => { if (this.desired.connectionState !== 'DISCONNECTED') this.sendAcked('HEARTBEAT', { agentVersion: AGENT_VERSION }).catch(() => undefined) }, (this.settings.heartbeatIntervalSeconds ?? 20) * 1000)
        if (this.o.approve && !this.approved) await this.approve()
        if (this.wake) void this.drain()
        return
      case 'SETTINGS':
        this.settings = payload
        await this.apply()
        return
      case 'DESIRED_STATE':
        this.desired = payload
        await this.apply()
        this.log(`desired state ${payload.revision}: ${payload.connectionState}${payload.dispatchPaused ? ', dispatch paused' : ''}`)
        if (this.wake) void this.drain()
        return
      case 'JOB_AVAILABLE':
        this.wake = true
        if (this.ready) void this.drain()
        return
      case 'COMMAND': {
        const outcome = payload.action === 'REFRESH_INVENTORY' ? 'INVENTORY_REFRESHED' : 'UNSUPPORTED'
        if (outcome === 'INVENTORY_REFRESHED') await this.sendAcked('INVENTORY', this.inventory())
        await this.sendAcked('COMMAND_RECEIPT', { commandId: payload.commandId, receipt: { receiptId: randomUUID(), outcome, completedAt: new Date().toISOString().replace(/\.\d+Z$/, 'Z') } })
        this.log(`command ${payload.action}: ${outcome}`)
        return
      }
      case 'ACK':
        this.pending.get(payload.acknowledgedMessageId)?.resolve()
        this.pending.delete(payload.acknowledgedMessageId)
        return
      case 'ERROR': {
        const p = this.pending.get(message.messageId)
        const error = new Error(`server ERROR ${payload.code}: ${payload.message}`)
        if (p) { p.reject(error); this.pending.delete(message.messageId) } else this.log(error.message)
        return
      }
      case 'PONG': return
      default: this.log(`ignored ${type}`)
    }
  }

  async apply() {
    await this.sendAcked('STATE_APPLIED', { lastAppliedRevision: this.desired.revision, lastSettingsRevision: this.settings.revision })
    this.state.lastAppliedRevision = this.desired.revision
    this.state.lastSettingsRevision = this.settings.revision
    this.saveState()
  }

  async approve() {
    this.approved = true
    const rows = await this.request('POST', '/qqq/v1/table/printing_printer/query', { body: { filter: { criteria: [{ fieldName: 'device_id', operator: 'EQUALS', values: [this.state.deviceId] }, { fieldName: 'printer_id', operator: 'EQUALS', values: [this.o.printerId] }] } } }).catch(() => undefined)
    const row = rows?.records?.[0]
    if (!row) return this.log('could not find the printer row to approve; approve it with the "Approve printers" action')
    if (Number(row.values?.enabled) === 1) return this.log(`printer "${this.o.name}" is already approved`)
    await this.runProcess('printingEnableSelectedPrinters', {}, [row.values.id])
    this.log(`approved printer "${this.o.name}" (printer row ${row.values.id})`)
  }

  /** The finite claim drain a JOB_AVAILABLE starts. */
  async drain() {
    if (this.draining || !this.ready) return
    if (this.desired.dispatchPaused || this.desired.connectionState === 'DISCONNECTED') return
    this.draining = true
    try {
      while (!this.stopped) {
        this.wake = false
        const { job } = await this.request('POST', `${BASE}/device/jobs/claim`, { body: {}, device: true })
        if (!job) break
        await this.print(job)
        if (this.o.once) { this.stop(); return }
      }
    } catch (e) {
      this.log(`claim drain stopped: ${e.message}`)
    } finally {
      this.draining = false
    }
    if (this.wake && !this.stopped) void this.drain()
  }

  async event(job, status, extra = {}) {
    await this.sendAcked('JOB_EVENT', { jobId: job.jobId, event: { eventId: `${job.jobId}-${status.toLowerCase()}`, status, ...extra } })
  }

  async print(job) {
    this.log(`job ${job.jobId} "${job.title}" (${job.format}, ${job.contentLength} bytes, ${job.options?.copies ?? 1} cop${(job.options?.copies ?? 1) === 1 ? 'y' : 'ies'}): received`)
    await this.event(job, 'RECEIVED')
    let bytes
    try {
      bytes = await this.request('GET', `${BASE}/device/jobs/${encodeURIComponent(job.jobId)}/content`, { device: true, raw: true })
      verifyDocument(job, bytes)
    } catch (e) {
      await this.event(job, 'FAILED', { message: `download or verification failed: ${e.message}`.slice(0, 2000) })
      this.log(`job ${job.jobId} failed: ${e.message}`)
      return
    }
    await this.event(job, 'SUBMITTING')
    mkdirSync(this.o.out, { recursive: true })
    const name = outputName(job)
    const file = join(this.o.out, name)
    writeFileSync(file, bytes)
    writeFileSync(`${file}.json`, JSON.stringify({ jobId: job.jobId, title: job.title, format: job.format, options: job.options, contentSha256: job.contentSha256, printedAt: new Date().toISOString(), printer: this.o.name }, null, 2) + '\n')
    await this.event(job, 'SPOOLED', { spoolerJobId: name.slice(0, 128), message: `Written to ${file}`.slice(0, 2000) })
    await this.event(job, 'SPOOLER_COMPLETED', { spoolerJobId: name.slice(0, 128) })
    this.printed++
    this.log(`job ${job.jobId}: printed to ${file}`)
  }

  async start() {
    await this.signIn()
    await this.ensureDevice()
    this.connect()
  }

  stop() {
    this.stopped = true
    clearInterval(this.heartbeat)
    try { this.ws?.close(1000, 'agent stopping') } catch { /* closed */ }
  }
}

const USAGE = `usage: virtual-agent.mjs [--server <origin>] [--out <dir>] [--state <file>] [--enroll] [--approve]
                        [--name <printer name>] [--device-name <name>] [--printer-id <id>]
                        [--formats PDF,ZPL] [--facility <id>] [--once]`

async function main() {
  const options = parseArgs(process.argv.slice(2))
  if (options.help) return console.log(USAGE)
  checkTransport(options.server)
  mkdirSync(options.out, { recursive: true })
  const agent = new Agent(options)
  const quit = () => { agent.log('stopping'); agent.stop(); setTimeout(() => process.exit(0), 200) }
  process.on('SIGINT', quit)
  process.on('SIGTERM', quit)
  agent.log(`virtual file printer "${options.name}" writing to ${options.out}`)
  await agent.start()
}

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith('virtual-agent.mjs')) {
  main().catch((e) => { console.error(`virtual-agent: ${e.message}`); process.exit(1) })
}
