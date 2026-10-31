# Realtime architecture and operation

This describes unreleased `0.3.0-rc.1` source. The published `0.2.0-rc.1`
transport and its acceptance evidence remain historical. The authoritative
[contract at the consumer pin](https://github.com/QRun-IO/qqq-print-protocol/blob/df3729ac632d4a34a3fb8423acef465042a624fc/docs/REALTIME.md)
defines exact fields, bounds and required cross-runtime tests.

Native printing is version 2.0 because claiming now requires a synchronized
authenticated WebSocket; published 1.0 clients could claim using HTTP alone.
This is a breaking behavioral change even though the route and domain payloads
are retained. Product version 0.3, framework `/qqq/v1`, wire payload v1 and the
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
