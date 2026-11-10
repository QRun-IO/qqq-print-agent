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

### A server that runs as several nodes

A QQQ server may run as several nodes behind a load balancer with cookie
stickiness. Sessions are rows in its database, so any node accepts the session;
what differs per node is which one holds this device's socket. The client keeps
the load balancer's own cookies (Application Load Balancer names, beginning
`AWSALB`) from every answer of the configured origin, including the answer to
the WebSocket upgrade, and sends them on every later request and on the next
upgrade. Discovery runs immediately before each upgrade, so the socket opens on
the node the HTTP requests already reach, and claims and downloads then go to
the node that holds the socket. The cookies are held in memory only, are never
saved with the session, and are bounded (eight values of at most 4096
characters, scoped to the origin's host and root path).

When that node stops:

| What the client sees | What it does | Needs a person |
|---|---|---|
| The socket closes or stops answering heartbeats | Marks realtime not ready, cancels the claim wave, and reconnects after the configured backoff (1 to 30 seconds by default). The load balancer routes the reconnect to a healthy node and rewrites its cookie; the client follows it. After the new socket's welcome and applied state the server announces outstanding work again and claims resume; a claimed job replays, a job already recorded locally is not submitted twice | No |
| 502, 503 or 504 (or any 5xx, or 409) on a claim, download or event | The request fails; the session is kept. The lane logs `Print lane stopped` and ends; the job stays on the server and is claimed on the next job notification. A job caught between claim and submission is recorded FAILED locally, with no bytes submitted | No (the FAILED job needs an operator retry) |
| A dropped connection with no answer | The same as a 5xx: a transport failure, session kept | No |
| 5xx on the WebSocket upgrade | A failed connection attempt; retried after backoff | No |
| 401 on any request, or 401/403 on the WebSocket upgrade | The server withdrew authorization. The session is cleared (401) and reconnecting stops until someone signs in | Yes |

A server at QQQ Printing 1.1.x refuses a claim (409) unless the socket is on
the node that received it; the cookie keeps the two together except while a
node is being drained (the load balancer already sends new requests elsewhere
while the socket is still open). A server with the several-node support in
QQQ Printing after 1.1.2 accepts the claim on any node.

The service owns separate connection, receive, send, synchronization, command
and job workers. Settings, desired state, commands, inventory, job events and
receipts use small bounded WebSocket messages. A job notification wakes a finite
HTTPS claim drain. It stops when the queue is empty or capacity is occupied and
resumes on a committed notification or released capacity. There is no periodic
HTTP job/control/receipt fetch and no polling fallback. WebSocket liveness and
normal QQQ session renewal do not query the job queue.

Documents stream over separate authenticated HTTPS requests into owned files in
the protected service staging directory. Length and SHA-256 are checked while
streaming; only a complete verified file can reach the native printer worker.
The control reader does not wait for the document body or the printer driver.
Download/print concurrency and aggregate staging/bandwidth are bounded; jobs
for one printer retain their submission order. Native printing runs in an
isolated child with a configurable deadline, retaining uncertain outcomes.

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
