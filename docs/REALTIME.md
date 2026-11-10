# Realtime architecture and operation

The realtime transport was introduced in release candidate [0.3.0-rc.1](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v0.3.0-rc.1) as native printing 2.0, and the published `0.2.0-rc.1`
polling transport and its acceptance evidence remain historical. The released 1.0.1 client speaks native printing 2.3 with
wire payload v1 and the same realtime model; media negotiation is offered only to a stable 2.3+ server. The authoritative
[contract at the consumer pin](https://git.kof22.com/QRun-IO/qqq-print-protocol/src/commit/42c0d249f81395f00408ec0f92638dcbbf5deb03/docs/REALTIME.md)
(commit `42c0d249f81395f00408ec0f92638dcbbf5deb03`, [Gitea PR 2](https://git.kof22.com/QRun-IO/qqq-print-protocol/pulls/2))
defines exact fields, bounds and required cross-runtime tests. The earlier 0.3.0 pin was `f3cbc02e9b0fb4fdfefaa59d976c04df747cc589`, kept as history.

The 2.0 transport origin: native printing became version 2.0 because claiming then required a synchronized
authenticated WebSocket; clients of native printing API 1.0 could claim using HTTP alone.
This is a breaking behavioral change even though the route and domain payloads
are retained. Product version 1.0.1, framework `/qqq/v1`, wire payload v1 and the
`qqq-printing.v1` subprotocol have separate version meanings.

## Connection and work

The service discovers printing extension `>=2.0.0 <3.0.0`, resumes the standard
QQQ user session and opens `/qqq/v1/qbit/printing/device/realtime` with subprotocol
`qqq-printing.v1`. The session cookie is primary authority; device headers are
secondary bound-device proof. Current user, tenant and record permissions still
apply. No credentials are placed in URLs or messages. WSS and HTTPS share the
configured origin; redirects cannot select another document host. Plain WS/HTTP
is restricted to explicit loopback development.

The service owns separate connection, receive, send, synchronization, command
and job workers. Settings, desired state, commands, inventory, job events and
receipts use small bounded WebSocket messages. A job notification wakes a finite
HTTPS claim drain, called a wave. A wave claims jobs one at a time while a
download slot is free, waits for a slot when all are occupied, and stops
claiming at the first claim that finds the queue empty or fails. It stays open
until the jobs it claimed have finished. A notification that arrives while a
wave is open is remembered but not acted on at once: the job it announces is
claimed by the next wave, which starts only after the open wave's in-flight
jobs have finished, even when that job is for an idle printer. There is no periodic
HTTP job/control/receipt fetch and no polling fallback. WebSocket liveness and
normal QQQ session renewal do not query the job queue.

Inventory, job events and command receipts travel only on the WebSocket, each
acknowledged by the server. Version 2.0 deleted the HTTP requests the client
once made in their place when no channel was attached (inventory, heartbeat,
job events, the control poll and the HTTP command receipt): with no channel a
send fails as a closed connection. Events and receipts stay in the journal and
are replayed after the reconnect, and inventory is sent again on every new
connection. The heartbeat is a frame the runtime sends on the channel. The
printing requests that remain over HTTPS are the claim, the document, the
queued-job preview, operator actions, the printer catalog and enrollment. The
API client that makes them can be built only from the signed-in session, and
every one of them, the document included, goes through that session.

Each lane runs a worker that does four things in a pass: deliver the events
the journal still holds, claim one job, stage its document and submit it once.
Everything else belongs to the runtime and is handed to the worker, none of it
optional: whether it may dispatch, claim or submit, the limits in force, the
gates the lanes share and the leases a job holds. The runtime, not the worker,
reports at start the jobs a previous run left unfinished, and sends inventory
and heartbeats.

Documents stream over separate authenticated HTTPS requests into owned files in
the protected service staging directory. Length and SHA-256 are checked while
streaming; only a complete verified file can reach the native printer worker.
The control reader does not wait for the document body or the printer driver.
Download/print concurrency and aggregate staging/bandwidth are bounded; jobs
for one printer retain their submission order. Native printing runs in an
isolated child with a configurable deadline, retaining uncertain outcomes.

## Claimed jobs the worker does not print

A claimed job is checked against the contract before anything else handles it.
Fields the client does not read are ignored wherever they appear, to the nesting
depth the JSON reader accepts (64 levels, counted from the claim response),
because the contract tells consumers to ignore unknown fields and the server
relays a submitter's options unchanged. The response itself is parsed to 1,024
levels, so that a job nested too deep to read can still be identified.

- **Malformed job.** A job that breaks the contract (a missing or unusable
  member, a member the client reads given twice in any letter case, or nesting
  deeper than 64 levels) but still names itself with one usable job identifier
  is refused, not skipped. The worker records RECEIVED and then FAILED with the
  reason and "No bytes were submitted", delivers both events, never downloads
  or submits the document, and the lane goes on to the next job. A malformed
  replay of a job the journal already knows, in any state, changes nothing and
  is logged.
- **Claim that cannot be identified.** A claim response that names no single
  job (not JSON, no usable identifier, the identifier given twice) cannot be
  answered for: the lane stops with an error, the next wake-up claims again,
  and the server offers the same job until it is cancelled there. The one such
  response a submitter could in principle cause is a job nested deeper than the
  1,024 levels the response is parsed to. That depth is derived from the
  server's cap of 2,000 characters on a job's options: the cheapest nesting,
  an array in an array, costs two characters a level, so about 1,000 levels fit,
  plus the response, the job and the options. No options the server accepts
  today can nest deeper, so this case cannot be produced through it as it
  stands; that rests on the server's cap, and the depth must rise if the cap
  does.
- **Claimed job that is already submitting.** A job the journal holds as
  SUBMITTING, or in a final state, is never processed a second time when the
  server offers it again. For SUBMITTING the worker logs the job and the reason.
  The lane then ends its pass and the wave is marked drained, because the server
  would only offer the same job again; the next committed notification or
  released capacity starts a new wave. The outcome is reported by the lane that
  is submitting the job or, after a restart, by the runtime's recovery at start
  as OUTCOME_UNKNOWN.
- **Between SUBMITTING and Windows.** If delivery of the SUBMITTING receipt
  cannot be confirmed, or the worker is stopped after it was acknowledged and
  before the native call, Windows is never asked. The job is FAILED and its
  reason says that no bytes were submitted.
- **After spool acceptance.** Once a job is recorded as SPOOLED it stays
  SPOOLED. A failure while releasing its staging reservation or printer turn
  afterwards is logged and is not written over the record.
- **One way to print.** A printer backend lists printers and prints one
  verified staged file under a submission limit; it has no other print
  operation. Version 2.0 deleted the submission of a document from memory, and
  with it the separate interface for backends that take files, so every
  backend takes one, the exposure filter and the profile wrapper included. A
  worker can no longer be built with a backend that cannot print its staged
  file, and a wrapper can no longer refuse a file after SUBMITTING was
  recorded because the backend inside it takes none. The print helper accepts
  three operations (discover, print a staged file by its path, cancel one
  spool job); it no longer accepts a document inside a request.

## Administrator settings

In QQQ, authorized administrators use **Printing connection and limits** for
document, transfer, connection and control limits. **Printing queue and storage
limits** manages the existing tenant/device admission policy; document retention
is a separate server policy. The companion does not override these settings.

| Setting group | Initial defaults |
| --- | --- |
| Documents | PDF 100 MiB; ZPL 20 MiB; up to 100 copies |
| Execution | One download and one print lane; configurable from 1 to 16, with per-printer ordering |
| Staging and bandwidth | 512 MiB aggregate staged content; bandwidth 0 means unlimited |
| Control messages | 1 MiB per complete message; 64 queued messages and 4 MiB queued bytes |
| HTTPS requests/transfers | Request and connect 30 seconds; idle 30 seconds; total download 10 minutes |
| Native submission | 120 seconds; configurable from 1 second to 24 hours |
| Connection | Handshake 30 seconds; heartbeat 20 seconds; liveness timeout 60 seconds |
| Reconnect and ACK | 1–30 seconds backoff, 20% jitter within those bounds; ACK timeout 15 seconds |

All 23 settings, including the server-generated revision, are sent in a complete
snapshot. The client advertises its actual ceilings, rejects missing or invalid
settings and refuses incompatible policy rather than silently reducing it.
Changes are pushed and durably applied before new claims. Admitted transfers
retain a consistent policy snapshot and their staging reservations. Host
admission/retention and intrinsic OS, renderer and IPC ceilings have separate
scopes; a transport limit is not a promise of sustained printer throughput.

## Queue health and operator actions

Use the companion footer, Jobs page and QQQ device/job/command records together.
An active connection does not establish printer hardware readiness. The Jobs
page merges local history with at most 50 server-queued observations; its count
is not a complete large cloud-queue count. Offline previews remain observations,
not a fresh authoritative queue. Selected job rows expand recent history; the
full details view retains the bounded longer history.

- **Paused:** no new claims; previously admitted work and receipts retain their
  identities. Resume is separate from reconnect.
- **Disconnected by administrator:** the control channel stays available for
  reconnect and in-flight receipts; ordinary inventory and new claims are held.
- **Authorization required:** restore the QQQ session or authorized pairing;
  repeated reconnect does not repair withdrawn permissions or revocation.
- **Queued:** inspect pause/connection state, approval/exposure, current settings,
  available capacity and server admission information before resubmitting.
- **SPOOLED / OUTCOME_UNKNOWN:** inspect Windows and actual output. Neither an
  empty queue nor a transport reconnect authorizes printing the same job again.

Clearing a device's server queue cancels only currently QUEUED jobs and retains
history. Remote spool cancellation targets one owned local job; success reports
Windows accepting the cancellation request, not recalling paper. No remote
command restarts Windows, a service or a printer.

## Reconnect and shutdown

The journal persists exact events and command receipts before transmission.
After reconnect, the service applies current settings/state, waits for their
acknowledgment and reconciles assigned jobs and its outbox. Lost acknowledgments
replay the original IDs and result; they do not repeat native cancellation or
submission. A new socket generation replaces the old one without allowing late
callbacks to acknowledge the replacement's work.

Restart, update preparation and account changes retain journal evidence.
Never delete staging/journal/credential files to force a retry. A native timeout
or interruption after submission begins can mean output was accepted; preserve
OUTCOME_UNKNOWN and require operator review. See [administrator operations](ADMINISTRATION.md).

Source tests and a published contract are not installed acceptance. Required
consumer checks include controls during a stalled large HTTPS transfer, real
bounded concurrent lanes, lost-ACK replay, offline work reconciliation and
authorization withdrawal. Installed UI, provider renewal, proxy behavior and
physical output remain separately qualified.
