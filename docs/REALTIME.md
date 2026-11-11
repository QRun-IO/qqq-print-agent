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
the WebSocket upgrade, accepted or refused, and sends them on every later
request to that origin and on the next upgrade. Discovery runs immediately
before each upgrade, so the socket opens on the node the HTTP requests already
reach, and claims and downloads then go to the node that holds the socket. The
cookies are held in memory only, for the life of one session client, are never
saved with the session, are kept across sign-out, are never sent to the sign-in
provider, and are bounded (eight values of at most 4096 characters, names of at
most 64, scoped to the origin's host and root path). The log counts them and
never shows a name or a value.

When that node stops:

| What the client sees | What it does | Needs a person |
|---|---|---|
| The socket closes or stops answering heartbeats | Marks realtime not ready, cancels the claim wave, and reconnects after the configured backoff (1 to 30 seconds by default). The load balancer routes the reconnect to a healthy node and rewrites its cookie; the client follows it. Job events and receipts travel on the socket only, so one that was not acknowledged stays in the journal and is sent again, under the same identifier, on the new socket. After its welcome and applied state the server announces outstanding work again and claims resume; a claimed job replays, a job already recorded locally is not submitted twice | No |
| 502, 503 or 504 (or any 5xx, or 409) on a claim | The request fails; the session is kept. The claim wave ends with one `Print lane stopped` line; the job stays on the server and is claimed by the next wave | No |
| The same on a document download, after the claim | The session is kept. Windows was never asked, so the job is recorded FAILED locally with nothing submitted, and the lane goes on | No (the FAILED job needs an operator retry) |
| A dropped connection with no answer | The same as a 5xx: a transport failure, session kept | No |
| 5xx on the WebSocket upgrade | A failed connection attempt; retried after backoff, with the load balancer's cookie from that refusal | No |
| 401 on any request, or 401/403 on the WebSocket upgrade | The server withdrew authorization. A 401 to a request clears the session; a refused upgrade makes the service block printing. Either way reconnecting stops until someone signs in | Yes |

A server at QQQ Printing 1.1.x refuses a claim (409) unless the socket is on
the node that received it. The cookie keeps the two together until the load
balancer chooses another node for this client's requests while its socket is
still open on the first. That happens while a node is being drained, and
equally after a failed health check or whenever else the load balancer
chooses again. From then on every claim is refused, and it stays so until the
socket closes for any reason and the reconnect follows the new cookie: the
client does not yet close the socket on this account. A server with the
several-node support in QQQ Printing after 1.1.2 accepts the claim on any
node.

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

A printer backend has three reads, and a job uses the two that do not read
every printer's settings. The full discovery reads the settings of every queue
through its driver; it is for the inventory report, the companion's list and
sharing changes. The list without media names every queue from the spooler's
enumeration alone: the service opens no queue for it and asks no driver. The
lookup is the same enumeration and then the settings of one queue. A lane lists
before it claims (the list names the job's printer in the journal), looks up
the claimed job's own printer once after the download and before SUBMITTING is
recorded, and the print helper looks the same printer up again before it
prints. So for one job the service reads the settings of that job's queue and
of no other; a queue whose driver hangs or fails stops only its own jobs,
which are refused before submission. Each read through the service is a helper
of its own with a 20 second limit.

What still asks every queue is the inventory report. A report that cannot be
read (one queue's driver is enough) sends nothing and is logged once per
reason; the connection stays up and the inventory it already delivered stays
in force, so this failure by itself holds no job. Jobs continue when the cause
is one queue's settings. A cause the lanes share, the spooler's list or the
service's stored data, stops them too, with "Print lane stopped" in the log.
While it lasts the server is told nothing new about any printer, so
a change in the status or the media of a healthy printer is not reported
either. A connection that has delivered no inventory yet claims nothing until
one full discovery succeeds and the server accepted it, so after a restart or
a reconnect a driver that fails every time still stops printing.

Known limit: a print server that is switched off or cannot be reached. The
enumeration is Windows' own work, and all three reads begin with it. It asks
the spooler for the queues installed on the station and for the connections
the service's identity has to printers that another computer, a print server,
shares. Microsoft documents that this enumeration opens every such connection
and waits for a timeout when the connection's server is down or gone, or the
printer on it no longer exists. So one connection to a print server that does
not answer can still stall the list, the lookup and the print, for every
printer on the station. The list runs before every claim with its 20 second
limit. If Windows waits longer than that, the pass ends before it claims (the
log has "Print lane stopped: The printer operation timed out") and no job is
taken for as long as it lasts; a lookup that runs out of time fails its job
before SUBMITTING, and a print that runs out of time ends as OUTCOME_UNKNOWN.
The three reads therefore cover a broken driver on the station and do not
cover a dead print server, which stalls printing as it did before them. The
remedy is to bring the server back or to remove that printer connection from
Windows. This comes from Microsoft's documentation and from the code; it has
not been measured on Windows. Listing in a way that opens no connection would
change what reaches Windows, so it waits for the check on real printers (W70
in [the audit](AUDIT-2026-11-10.md)). A spooler that cannot enumerate at all
also stops every pass before it claims.

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
  member, a member the client reads given twice in any letter case, nesting
  deeper than 64 levels, or text that cannot be read) but still names itself
  with one usable job identifier is refused, not skipped. The worker records
  RECEIVED and then FAILED with the reason and "No bytes were submitted",
  delivers both events, never downloads or submits the document, and the lane
  goes on to the next job. A malformed replay of a job the journal already
  knows, in any state, changes nothing and is logged. Text that cannot be read
  is a surrogate escape without its partner or bytes that are not UTF-8, both
  of which the JSON parser accepts. It is refused in a text member the client
  reads and in a member name beside the job, in the job, in its options or in
  its media selection; inside a field the client does not read it is ignored.
- **Claim that cannot be identified.** A claim response that names no single
  job (not JSON, no usable identifier, the identifier not readable text or
  given twice) cannot be answered for: the lane stops with an error, the next wake-up claims again,
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
- **Claimed job that is already in hand.** A job a lane of this computer
  holds is left to that lane when the server offers it again. A lane holds a
  job from its claim until its pass for that job ends. Normally that is after
  the final state was recorded and delivered; the delivery is skipped when
  the pass is cancelled, as when the connection is replaced or the service
  stops, and a delivery can fail, and the lane lets go all the same. So a job
  in hand is being worked on or has just finished. The worker asks the
  runtime whether the job is in hand before it does anything else with the
  claim: nothing is recorded or reported, the
  job's title and printer in history are not rewritten, the log says once
  that the job was offered again while a lane of this computer still holds
  it, and the lane ends its pass as it does for a job that is submitting.
- **Printer changed before the download finished.** The assigned printer is
  looked up once, after the download and before the job asks for the
  printer's turn; no other queue's settings are read. A printer the operator stopped sharing, a queue that is
  gone, or a selected media default that changed by then is FAILED with its
  reason before SUBMITTING is recorded, and Windows is never asked. The
  timing line reports this one check as `revalidate`.
- **Printer changed while the job waits for its turn.** A job can wait a
  long time behind a print. The worker does not look the printer up again
  when the turn is held: that would start a print helper once more for
  every job, while the job holds the printer's turn and a print slot, and
  would add a second wait on that queue's driver. What
  changed during the wait is settled after SUBMITTING is recorded, by
  whoever can tell whether anything was sent:
  - *No longer shared.* The exposure filter in front of the backend checks
    sharing at the moment of submission. Its refusal is made before the
    backend behind it is called and has a type only the filter can raise, so
    the worker knows nothing was sent: the job is FAILED, not
    OUTCOME_UNKNOWN, and its reason ends "no bytes were submitted".
  - *Media default changed.* The print helper reads the queue's default
    again before it starts the document and refuses a mismatch with its own
    error code. The job is FAILED.
  - *Queue removed from Windows.* The print helper finds no queue before it
    sends anything, but reports that as an ordinary failure, which the worker
    cannot tell from one that happened part-way. The job is OUTCOME_UNKNOWN
    and needs an operator's review, although nothing was printed. An error
    code for a missing queue would let the worker record FAILED; it is not
    implemented.
- **Between SUBMITTING and Windows.** If delivery of the SUBMITTING receipt
  cannot be confirmed, or the worker is stopped after it was acknowledged and
  before the native call, Windows is never asked. The job is FAILED and its
  reason says that no bytes were submitted.
- **After spool acceptance.** Once a job is recorded as SPOOLED it stays
  SPOOLED. A failure while releasing its staging reservation or printer turn
  afterwards is logged and is not written over the record.
- **One way to print.** A printer backend reads printers and prints one
  verified staged file under a submission limit; it has no other print
  operation. Version 2.0 deleted the submission of a document from memory, and
  with it the separate interface for backends that take files, so every
  backend takes one, the exposure filter and the profile wrapper included. A
  worker can no longer be built with a backend that cannot print its staged
  file, and a wrapper can no longer refuse a file after SUBMITTING was
  recorded because the backend inside it takes none. The print helper accepts
  five operations (the three reads: discover, list without media, find one
  queue with its media; print a staged file by its path; cancel one spool
  job); it no longer accepts a document inside a request. The queue to find
  is named by its identifier, and a request whose identifier is not 24
  hexadecimal digits in lower case is refused before any queue is asked.

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
