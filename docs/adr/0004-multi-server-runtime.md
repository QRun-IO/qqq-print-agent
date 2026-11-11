# ADR-0004: Multi-server runtime, stored state and the management pipe contract

Status: accepted for 2.0 under the owner's direction of 2026-11-10 ("take the recommended option, record it, keep going"); not yet implemented. Refines [ADR-0003](0003-version-2.md), which decided that the service keeps up to eight server connections. Revised on 2026-11-10 after an independent review of the first draft; the revision is what is to be built. Revised again on 2026-11-10 after two retrospective reviews of merged work: what changed, and why, is in "Resolved after the retrospective reviews" below.

## Context

In 1.2.x one `AgentRuntime` object is the connection: 51 of its 65 fields are per-connection state, one loop set serves it, and any of four tasks ending stops the service. The store derives an implicit "connection scope" from the one saved pairing; job events, job summaries, control state and realtime settings are keyed by it, while printer sharing, printer profiles and the certificate exception are machine-wide. The management pipe has one instance, serves one client at a time, takes string operations with no server identity and answers with text that callers match by prefix.

## Decisions

### Servers have a local identity

Each server the operator adds gets a `serverId`: a GUID created on this computer, never sent to any server and never reused. Everything per-server is keyed by it. The registry is a `servers` table (`server_id`, normalized origin, display name, position, added time); the origin is unique, so one server cannot be added twice.

A server's name can be changed. Its address cannot: the pairing, the session, the certificate exception and the journal scope are all bound to the origin, so a different address is a different server, added with the wizard, and the old one is removed. No operation edits an address. What a name may be is under "Server names" in "Resolved after the retrospective reviews".

The limit is eight, and adding a ninth is refused with a message that says so. Eight is what one person testing against several environments needs, and it is a number at which the worst case (eight connections, each with its own claim lanes, sockets and staged documents, and a tree with eight nodes) is still tested as a whole. It is one constant; raising it is a later decision, not a redesign.

### What is per-server and what is per-computer

Per server: the pairing, the session, the certificate exception, which printers are shared, each shared printer's display name, description and location, the job journal, control state, realtime settings and the staging directory. ADR-0003 also lists media choices; nothing of that kind is stored today (the server picks media per job from what the printer advertises), so there is nothing to make per-server.

Per computer: the Windows printers that exist, the computer's own name, description and location, log settings, the Windows service, updates and appearance. The computer details describe this machine, so every server receives the same ones.

A printer may be shared with several servers.

### Stored state

Secrets move from the state directory's root to `servers/<serverId>/`: `connection.dpapi`, `qqq-session.dpapi`, the staged session of a pairing change in progress (`qqq-session.staged.dpapi`), pairings and sessions set aside as unreadable (`connection.recovery-*.dpapi`, `qqq-session.recovery-*.dpapi`) and `staging/`. All are still protected with DPAPI under the service identity. Staging a pairing and resolving a staged one work inside that folder exactly as they do in the root today, including the rule that the order of the stored and staged session is their last-write time.

The journal stays one SQLite database. One file gives one transaction across the registry and the per-server tables, one retention pass and one thing to back up, and the volume (eight writers, a few rows per job) is far inside what SQLite's write-ahead log handles. Isolation between servers is by key, and a connection's store is opened for one explicit server, so nothing a connection does can be written without one. Only the host works across servers: migration, the registry and retention.

The connection scope (a hash of origin and device) stays the key for events, jobs, control state and realtime settings, so a new pairing on the same server still starts a clean scope and nothing recorded under an old pairing can be sent with a new credential. What is new is the relation between a server and its scopes:

- A `server_scopes` table (`connection_id`, `server_id`, first used, last used, current flag, removed time) has one row for every scope in the journal. The row is written before the pairing file it belongs to, so a pairing on disk always has its row and no reader ever has to derive a scope from a pairing file that may be unreadable. The 1.x fallback that invents a new "unreadable" scope on every open is removed.
- "Last used" is written by the connection that owns the scope, when it starts and when it stops. Removing a server keeps its rows and stamps them removed. The migration creates a row with no server for every scope it finds in the 1.x journal that is not the first server's; its last use is the newest time recorded in that scope, or the time of the migration when its rows carry no time, as the oldest 1.x rows do not.
- A server's current scope is the live one: events are delivered from it, its unreviewed failures count toward the server's attention count, and its jobs can be acknowledged, retried and cancelled.
- A server's earlier scopes are history and nothing else. The Jobs page and `jobs.query` show them, marked as from an earlier pairing, so history survives a new pairing; this is a change from 1.x, where a new pairing hides it. Their failures do not count toward attention, and acknowledge, retry and cancel are refused (`job_not_current`). Their undelivered events can never be delivered, because they belong to a credential that has been replaced.
- A scope with no server, or with a removed one, is shown nowhere.

Retention is one pass run by the host, not by each store:

- Current scope, as today: details go after the retention period or beyond the detail limit, never while an event is undelivered; summaries stay.
- Earlier scope of a server that still exists: the same, except that an undelivered event does not hold anything back.
- Scope with no server or a removed one: everything (events, summaries, control state, realtime settings and the `server_scopes` row) is deleted once the retention period has passed since its last use.
- Control-state cleanup and its limits become per server. Today a store deletes other scopes' control state after 30 days, refuses to save when 128 other scopes exist and refuses a command when 4,096 receipts exist in the whole table, so with several servers one could erase or block another's. In 2.0 a store touches only its own server's scopes, and both limits count that server's rows.

Printer sharing and printer profiles get new tables keyed by `server_id` (`server_printers`, `server_printer_profiles`). The old machine-wide tables are not reused under a new meaning: an older build that somehow ran against this directory would otherwise read every server's sharing as its own.

The schema carries a version (`PRAGMA user_version`) and is migrated once, when the service starts, in a transaction. Opening a store no longer creates or alters tables. A service that finds a version newer than it knows does not start its connections and does not touch the state. It keeps serving the pipe: the snapshot says that a newer build wrote this state and that the newer build must be reinstalled; the snapshot, the transport read (with an empty list), the log operations and the update operations work, so that it can be; and every other operation, `servers.add` included, is answered `state_newer`. It never migrates down. This matters between release candidates, because the installer allows any build with the same numeric version to replace another (`AllowSameVersionUpgrades`), so an older candidate can be installed over a newer one.

### Upgrade from 1.x

The first start of 2.0 turns the existing connection into the first server. In order:

1. Any staged pairing is resolved in place by the existing rules, so a pairing change that 1.x left half-finished is completed or discarded before anything moves.
2. In one transaction: the schema is migrated; the `servers` row is created; the pairing's scope is recorded in `server_scopes`; sharing and printer profiles are copied to the server's tables; the certificate exception is copied if it is for the server's origin; and one row per file to move is written to a `pending_moves` table (source, destination).
3. Each move is carried out and its row deleted. The protected files, a staged session that could not be resolved in step 1 and any set-aside recovery files are moved; a move within the state directory keeps the last-write time that the staged-session rules depend on. Temporary files (`*.tmp`) are deleted, because nothing ever reads one. The staging directory is not moved: its leftovers are deleted at every start anyway.
4. No connection starts while a `pending_moves` row exists. An interrupted upgrade continues at step 3 on the next start.

Every move ends, so step 4 cannot hold the service forever:

| Source | Destination | What happens |
| --- | --- | --- |
| Present | Absent | Moved; row deleted. |
| Absent | Present | Already done; row deleted. |
| Present | Present | Something other than the upgrade wrote the destination. The destination is kept, the source is moved beside it under a recovery name, the row is deleted and the event is logged. |
| Absent | Absent | The file is gone. The row is deleted and logged; the server then simply lacks that file, and shows as needing a sign-in or a new pairing. |
| A move fails (in use, access denied) | | Tried three times in that start. Then the service starts without connections and the snapshot reports the upgrade as incomplete and names the file. As with a newer schema, the snapshot, the transport read (with an empty list), the log operations and the update operations work, and every other operation is answered `migration_incomplete`. The next start tries again. |

The service is first started by the installer before the installation commits. If the installer then rolls back, 1.x is left on state that 2.0 has already moved, and shows no pairing. This is accepted: nothing has been deleted, reinstalling 2.0 picks up where it stopped, and keeping a second copy of the secrets in the root to cover it would leave credentials behind that nothing maintains. It is read from the installer's authoring and is checked during upgrade qualification.

The cases:

| What 1.x left | Result |
| --- | --- |
| Pairing and session readable | One server, paired and signed in. |
| Session only (signed in, never enrolled) | One server named by the session's origin, signed in, not paired. |
| A pairing for one server and a session for another (1.x allows it: signed in to the second server, with printing blocked) | One server, from the pairing, needing a sign-in. The other server's session is moved into that server's folder under a recovery name and never used. |
| Pairing unreadable, session readable | One server named by the session's origin; the unreadable pairing is moved with it and reported; the journal rows of the unknown scope belong to no server. |
| Pairing readable, session missing or unreadable | One server, paired, needing a sign-in. |
| Neither readable | No servers; the files stay where they are, untouched, and the computer says why. |
| A certificate exception for the server's origin | Carried over for that origin; see "Certificate exception" below. |
| A certificate exception for another origin than the server's | Not copied, and logged: it cannot apply to this server. |
| Journal scopes from earlier pairings or other servers | Belong to no server; removed by retention. They were already invisible in 1.x. |

Nothing but temporary files is deleted by the upgrade. Going back to 1.x means uninstalling: the installer refuses a lower numeric version, and 1.x does not know the new layout.

### Runtime structure

- **Host.** Owns the pipe server, the server registry, the per-printer queues, retention and one connection per server. It supervises them: a connection that faults is restarted with backoff, and neither a faulting connection nor a faulting pipe client stops anything else.
- **Server connection.** One per server: the session client, the realtime channel, authorization state, control state, settings and its claim pipeline. This is what the per-connection fields of `AgentRuntime` become.
- **Printer queue.** One per Windows printer, shared by all servers. A job is identified by server and job together. The queue hands the device to one submission at a time. Within one server nothing changes: a job takes its place in that server's line for the printer when it is claimed, so a server's jobs for one printer are submitted in claim order. Across servers, the queue takes the job at the head of each server's line in turn, skipping a server whose head job is not yet staged, so one server's backlog cannot keep another's job waiting behind all of it. Waiting for the printer has no deadline of its own, as today: the limits that exist are the download timeout before the wait and the native submission timeout after SUBMITTING. Each server's own policy (concurrency, bandwidth) still applies inside its connection.

The spooler document name stays `QQQ <jobId>`. A server tag in it was considered and rejected: verified cancellation matches that name exactly, so changing it would make jobs spooled by 1.x impossible to cancel after the upgrade. The Jobs page already says which server a job came from.

`PrintWorker`'s reliability boundary does not change: SUBMITTING is recorded before the spooler call, and a submission interrupted after that is OUTCOME_UNKNOWN, never reprinted automatically.

### Management pipe, version 2

Requests and replies are JSON objects on the existing framing.

```json
{ "v": 2, "op": "printers.share", "server": "<serverId>", "args": { "printerId": "...", "shared": true } }
{ "v": 2, "ok": true, "result": { } }
{ "v": 2, "ok": false, "error": { "code": "server_not_found", "message": "...", "details": { } } }
```

`server` names the server an operation acts on, and is absent when the operation acts on the computer or on the list of servers. `message` is for people; no client decides anything by matching text. `details` is present only for the codes that say so below, and is typed per code. Clients switch on `code`, which is one of:

| Code | Meaning |
| --- | --- |
| `unsupported_version` | The service does not speak this `v`. |
| `unknown_operation`, `invalid_request` | The operation does not exist, or its arguments are missing or malformed. A test print in a format the printer does not offer is `invalid_request`, and so are arguments given to an operation that takes none (an empty `args` object says the same as leaving it out and is accepted). |
| `server_not_found`, `server_ambiguous` | No server has this identifier; or (command line only) a name matches more than one. |
| `server_limit`, `server_exists` | Eight servers already exist; or this origin is already a server. |
| `server_removing` | The operation names a server that is being removed. Reads still answer for it, and a repeated `servers.remove` succeeds with `removing` again, so a client that retried after a timeout is told what is true. Every other change to that server is refused with this code. Once removal has finished the server no longer exists, so a `servers.remove` repeated after that gets `server_not_found`, which a client that retried reads as removed. |
| `busy` | Another operation that changes this server's session, pairing or trust is running. |
| `not_signed_in`, `not_paired` | The operation needs a session, or a pairing, that this server does not have. |
| `not_connected` | This server's connection is not ready for the action: its administrator disconnected this device, or the connection is still waiting for authorization or for the control state to be agreed. |
| `server_incompatible`, `certificate_untrusted` | The compatibility check failed; `details` carries its stages and, for the second, a description of the certificate that was presented. |
| `server_unreachable` | The QQQ server could not be reached or did not answer in time (`session.signIn`, `servers.add`, `pairing.enroll` before the code is sent, `printers.testPrint`, `jobs.retry`, `jobs.cancel`). |
| `server_refused` | The QQQ server refused the action; `message` carries its reason. The wire enforces the form of that text, here and for every other code: before a message is sent, each control, format, line-separator, paragraph-separator, private-use and unassigned character in it becomes a space, and it is cut at 512 UTF-16 units, never inside a surrogate pair. It is the one definition Core uses for text a server supplied. |
| `sign_in_refused`, `bind_refused`, `identity_changed` | The server refused the session; refused to bind the pairing; or bound it for a different account than the approved one, in which case `details` names the approved principal and tenant. The only way to build those details puts each value through the same definition, cut at 200. |
| `enrollment_refused`, `enrollment_unconfirmed` | The server refused the code; or the outcome is not known and `details` says what was left behind. |
| `printer_not_found`, `job_not_found`, `job_not_current` | No such printer or job for this server (a test print on a printer that is not shared with that server is `printer_not_found`); or the job belongs to an earlier pairing and is history. |
| `duplicate_risk_unconfirmed` | A retry could print twice and the request did not confirm that. |
| `cancel_refused` | Windows refused to cancel a document in the spooler; `details` carries the `reason`, `documentGone` or `ownershipMismatch`. Nothing in the Windows queue was changed. |
| `state_unreadable` | Stored state for this server cannot be read. The message names the file; there are no details. |
| `state_newer`, `migration_incomplete` | The whole service is not running connections: a newer build wrote the state, or the upgrade from 1.x could not finish. The snapshot says which and why. |
| `update_in_progress` | The service is prepared for an update and is not accepting this. |
| `internal_error` | Anything else; the detail is in the service log, never in the reply. |

Concurrency:

- The pipe serves up to sixteen clients at once, so a sign-in that takes two minutes does not make status unavailable. A seventeenth waits for a free instance.
- Reads never wait for another operation. One read takes time of its own: `printers.discover` runs a discovery, and a request that arrives while a discovery is running joins it (single-flight; see "Resolved after the retrospective reviews"). Operations that change one server's session, pairing or trust (`session.signIn`, `session.signOut`, `pairing.enroll`, `trust.set`, `servers.remove`) run one at a time per server; a second one is answered `busy` at once. Operations on different servers do not wait for each other.
- `servers.add` holds the registry for as long as it takes to validate and store, not for the sign-in, which has already happened in the companion.

Access, as the code has it:

- The service authenticates a client by the pipe's access list only: LocalSystem and Administrators. It checks nothing else about a client.
- The client verifies, before it sends anything, that the process serving the pipe is the installed service.
- Replies never carry a session credential, a device token or document content. One reply is an exception and is named here so it stays one: `session.signOut` returns the identity provider's sign-out request, which includes the provider's ID-token hint, for the companion to open in the browser. It is never logged. The same reply says whether the session was also ended on the server (`serverSessionEnded`): sign-out always finishes on this computer, and when the server could not be told, the operator is asked to revoke the session there.

The snapshot is one aggregated document, so the companion asks once for everything instead of once per server and per view:

- Once: the computer's printers and when they were discovered (`printersDiscoveredAt`, absent before the first discovery, so an empty list before then is told from a computer with no printers), service state, update state, log settings, and the state of the stored data (ready, upgrade incomplete with the file it stopped at, or written by a newer build).
- Per server: identifier, name, address, position; health and its status text; the attention count that drives health; authentication (signed in, as whom, authorization blocked and why); paired flag and device identifier; transport state and the realtime meter; when the server last answered this computer (`lastContact`; the control state has its own, which is the control channel's alone); control state (desired state, last command); shared printers with each one's server-side approval; whether the certificate exception is on and the two origins it covers; and whether stored state is unreadable.
- Jobs stay paged and are queried per server.
- The snapshot never starts printer discovery, and never contacts a server. It reports the printers the host last discovered. The host refreshes that list at least every 60 seconds and whenever a server connection synchronizes its printers, so a snapshot's list is at most that old. A snapshot is therefore always cheap, whatever a printer driver is doing. A client that needs a fresh list asks `printers.discover`, which runs a discovery now and returns the computer's printers with the time; the wizard's printer step and the command line's `printers` use it.
- While the stored data is not ready, the list of servers is empty and the computer's part says why. `transport.get` still answers in that state, with an empty list: the companion repeats it every second, and an error every second would be noise beside the snapshot that already says why.

The companion keeps polling the snapshot, every five seconds as today. Between snapshots it draws the traffic meter from `transport.get`, a light read it repeats every second as it does in 1.x: for each server its identifier, its transport state with the meter, and `lastContact`, and nothing else. A change notification pushed by the service is not part of 2.0: one aggregated request already removes the cost that made polling a problem, and a push channel is a second protocol to secure and test.

Clients older than the service:

- A request without `v` gets the one legacy-format reply that 1.x clients already display as an error, telling the operator to restart the companion.
- The one 1.x client that is certain to reach a 2.0 service is the update helper, which sends `resume-after-update` after the installer finishes. It is answered with that same error, and that is harmless: the update pause lives in memory, and the service the installer just started is not paused.
- From 2.0 on, the helper is always one version behind the service it resumes. `update.prepare` and `update.resume` therefore keep working in every pipe version a later service accepts; a later version that changes them keeps accepting the old form.

Operations:

- On the computer or the list of servers (no `server`): `snapshot`, `transport.get`, `servers.list`, `servers.check`, `servers.add`, `servers.reorder` (the identifiers in their new order, in `args`), `computer.getProfile`, `computer.setProfile`, `printers.discover`, `logs.getSettings`, `logs.setSettings`, `logs.clear`, `update.prepare`, `update.resume`. `printers.discover` is a read with the ordinary 30-second limit; it does not answer while the stored data is not ready. It is single-flight: the computer runs one discovery at a time, and a request that arrives while one is running joins it and gets its result.
- On one server (`server` required): `servers.rename`, `servers.remove`, `session.signIn`, `session.signOut`, `pairing.enroll`, `connection.reconnect`, `trust.get`, `trust.set`, `printers.share`, `printers.setProfile`, `printers.testPrint`, `jobs.query`, `jobs.details`, `jobs.retry`, `jobs.cancel`, `jobs.acknowledge`.

When a server comes to exist:

- `servers.check` takes an address and, optionally, the operator's acceptance of the certificate exception for it. It also works for an address that is already a server, using that server's stored exception; that is how a repair re-checks. The service runs the compatibility check itself, under its own identity and certificate store, and returns the typed stages. This is the check that matters, because the service is what connects; in 1.x the companion checked under the user's certificate store while the service validated as LocalSystem, so the two could disagree.
- `servers.add` takes the address, the name, the certificate exception if the operator accepted one, the sign-in provider's origin that `servers.check` reported (for comparison only; see "Who names the sign-in provider" under "Resolved after the retrospective reviews"), and the session credential from a completed sign-in. It is atomic: the server, its certificate exception and its session are stored together, or nothing is. `servers.add` never creates a server without a session, so there is no provisional server to clean up. A server can lose its session later (sign-out, expiry, an upgrade that found only a pairing); that is what `not_signed_in` is for.
- `trust.set` and `session.signIn` are for servers that already exist.

Certificate exception:

- It stays what it is in 1.x, a development-only switch that accepts a certificate Windows does not trust, and it gets a boundary it lacks today. In 1.x, once switched on, it applies to every host the client contacts. In 2.0 it applies to two origins and no others: the server's own, and the sign-in provider that server names. Both are recorded with the server and shown to the operator in the warning.
- Pinning the accepted certificate's fingerprint was considered and rejected. The exception exists for self-signed certificates and private authorities such as a local Caddy root, and a private authority reissues its certificates on a short cycle; a pin would stop matching at each renewal and unattended printing would stop with it. The durable way to trust a private authority is to install its root in the Local Machine trusted root store, which needs no exception at all. The wizard and the Connection page say so wherever the exception is offered.
- When the trust stage fails, `servers.check` returns a description of the presented certificate (subject, issuer, expiry, fingerprint, and why Windows refused it), so the operator sees what the exception would accept. `trust.get` returns, for an existing server, the two origins the exception covers and the certificate from the last refusal the service remembers, with the address that presented it and when. It never probes: it contacts no server, and returns no certificate when the service has seen no refusal since it started.
- A server with the exception switched on says so in the snapshot, and the companion shows it on the server's node and its Connection page for as long as it is on, so a development setting is not forgotten.
- If a server later names a different sign-in provider, the exception does not follow it: sign-in fails as untrusted and the operator accepts the new origin or does not.
- An exception carried over from 1.x covers the server's origin. The provider's origin is recorded from the first sign-in description the service reads after the upgrade, and logged. The companion's sign-in uses the same exception for the same two origins, from the snapshot, so the two cannot disagree about what is accepted.
- Core needs the per-origin check in place of the one switch. That closes the first half of audit finding C9; its second half, binding the discovery address to the issuer's origin, is separate Core work.

Removing a server:

1. Its connection stops claiming. A submission already handed to Windows finishes and is recorded, bounded by the print deadline; jobs claimed but not yet submitted are reported back as not printed.
2. The service ends the session with the server, as sign-out does, without waiting on a server that does not answer.
3. Its protected files and staged documents are deleted, and its rows in the registry, sharing and profile tables. Its scopes stay in the journal, stamped removed, until retention deletes them. Events that were not delivered stay undelivered.
4. Its device record on the server is not touched.

### Command line

`servers` lists them. `servers rename` and `servers remove` change them. The command line does not add a server: adding needs an interactive sign-in, which only the companion can do, so `servers add` does not exist and `enroll --server URL` from 1.x is gone. `enroll` pairs a server that the companion has already added and signed in.

Every per-server command takes `--server`, which always selects and never names an address. It accepts a server's identifier, the first eight or more characters of one, or its name. A name that matches more than one server is refused with their identifiers. It may be omitted while exactly one server exists.

### Companion

The left pane is a tree: one node per server showing its name and health, with Printers, Jobs and Connection beneath it; then Add server, which opens the wizard; then the per-computer items (Computer, Logs, Settings). The footer and the tray icon show the worst health across servers, and the tooltip names each server. Busy state is per server, so a sign-in on one does not freeze the others. [ADR-0005](0005-connection-wizard-and-server-tree.md) has the detail.

Browser sign-in has one callback inbox and one redirect address per Windows user, so it cannot be per server. One dispatcher in the companion owns the inbox and hands each callback to the sign-in whose OAuth state it carries; a callback for a sign-in that has ended is dropped. Sign-ins to different servers can then run at once.

## Security consequences

One LocalSystem service now holds credentials for several servers, so the boundary between connections is a security boundary:

- A server can address only the printers shared with it, and sees only its own jobs.
- Each connection has its own protected files, staging directory, journal scopes and certificate exception; none is derived from another's, and cleanup in one never deletes or counts another's rows.
- A protocol violation, an authorization loss or a hostile payload ends or blocks that one connection.
- What is shared is bounded so that one server cannot use it up. A printer takes waiting servers in turn. Staged bytes have a machine-wide ceiling of 16 GiB, of which one eighth is reserved for each server that exists; only what is not reserved goes to whoever asks first, and no document an accepted policy allows is larger than a server's eighth (the arithmetic is under "The staging ceiling" in "Resolved after the retrospective reviews", which replaced an earlier wording of this sentence on 2026-11-10). The pipe has sixteen instances.
- Text a server supplies (titles, names, messages) stays bounded and is never used to build a path, a key or a command.

`docs/security/threat-model.md` gets these as boundary rows before the multi-server service merges.

## Resolved while planning the build

A planning pass over the code on 2026-11-10 found places where this record was silent or disagreed with the code. Each was settled as follows, under the owner's direction to take the recommended option and record it.

- **Where the pipe contract lives.** The envelope, error codes, operation names, arguments and results are one set of types in a `Management` folder of the Windows assembly, which the service, the command line and the presentation assembly already reference. A separate assembly would add a project and installer payload for no new boundary.
- **The companion between now and the tree.** The wizard and tree are a later stage, and until then the companion in this branch speaks version 1 of the pipe and knows one server. While the service can hold only one server, that server follows the stored files after each version 1 sign-in, enrollment and sign-out. Once it can hold several, an interim adapter serves version 1 requests against the first server in the list: a version 1 sign-in to a new address adds that server and moves it first, and signs the others out, because the old window cannot show a second live connection. The adapter is deleted when the companion speaks version 2. Only then does a request without `v` get the legacy error described above. *Changed on 2026-11-10: see "The interim companion" under "Resolved after the retrospective reviews".*
- **An unpaired server has no scope.** In 1.x an unpaired store uses one fixed scope name, which two signed-in, unpaired servers would share. An unpaired server reads defaults and cannot write to the journal. Rows the migration finds under that name, under an "unreadable" name or under no scope belong to no server.
- **When a scope becomes current.** The scope's row is inserted, not current, before any file is written. It becomes current in one transaction after the pairing file is in place. On open, a readable pairing whose row is not current is made current, which covers a stop between the two; an unreadable pairing never changes it. *Changed on 2026-11-10: see "When a scope becomes current" under "Resolved after the retrospective reviews".*
- **A server's own print limit and the shared printer queue.** The queue skips a server whose head job is not yet staged, and also one that cannot take another print at that moment under its own policy. Both are decided together under the queue's lock, so a server busy on one printer neither holds another printer against everyone nor has its job for an idle printer held by someone else's long print. *Changed on 2026-11-10: see "Print capacity and the shared queue" under "Resolved after the retrospective reviews".*
- **The staging ceiling.** 8 GiB for the computer, so each server's guaranteed eighth is 1 GiB, above the default policy of 512 MiB. A server's own limit still applies to its own total. A document larger than a server could ever be given is refused before download and recorded FAILED, instead of waiting forever. *Changed on 2026-11-10: see "The staging ceiling" under "Resolved after the retrospective reviews".*
- **Removal is restartable.** The print deadline can be far longer than a pipe request may take, and removal spans files and rows with no transaction. `servers.remove` marks the server as being removed, stops its claims and answers that removal has started or has finished. The host finishes when a submission in progress ends, and at the next start if it was stopped first. The snapshot shows a server that is being removed.
- **Jobs waiting behind another printer's job** (audit finding W23) are fixed in the claim path, not by the printer queues: lanes claim while there is demand and a free slot, instead of in waves that must all finish.
- **Who names the sign-in provider.** `servers.check` returns the provider's origin whenever it could read the sign-in description, so the re-check after the operator accepts the exception can name both addresses. `servers.add` carries only that the operator accepted. The service derives and stores the provider's origin from its own read and never takes it from a client. *Changed on 2026-11-10: see "Who names the sign-in provider" under "Resolved after the retrospective reviews".*
- **The snapshot carries no recent jobs and no log lines.** Jobs come from `jobs.query`; the service's in-memory log ring is removed. The command line's `snapshot` prints the aggregated document.
- **Ambiguous names are the command line's business.** The service takes a full server identifier only. The command line resolves a prefix or a name after listing the servers, and reports an ambiguous one itself.

## Resolved while reviewing the pipe contract

The contract was built as types and reviewed on 2026-11-10 before anything served it. The review found outcomes and readings that version 1 has and this record had no place for. They were settled as follows and are written into the sections above.

- **Outcomes version 1 explains in text.** Five codes were added so that none of them becomes `internal_error`: `not_connected`, `server_unreachable`, `server_refused`, `cancel_refused` (with the reason in `details`) and `server_removing`. `identity_changed` carries the approved account in `details`. A sign-out reply says whether the server session was ended.
- **Test prints.** A printer that is not shared with the server is `printer_not_found`; a format the printer does not offer is `invalid_request`.
- **Unreadable state.** `state_unreadable` names the file in its message and has no details.
- **The remembered certificate.** `trust.get` returns the certificate from the last refusal the service remembers, with the address that presented it and when, and never probes.
- **Last contact and the traffic meter.** Each server's snapshot has a `lastContact`, and `transport.get` is the light read for the meter, so the companion keeps its one-second meter without asking for the whole snapshot each second.
- **A snapshot is always cheap.** It reports the printers the host last discovered, with the time, and never starts a discovery. The host keeps that list at most 60 seconds old, and `printers.discover` is how a client asks for a fresh one.
- **Repeated members.** A request or a reply in which any object has the same member twice is refused whole, in the envelope and at any depth beneath it, because two readers can disagree about which value counts.
- **Arguments that are left out.** A request without `args` is read as one with empty arguments, so it is valid exactly when every argument of the operation is optional. `update.prepare` and `update.resume` take none today; if either ever gains arguments, a request without them stays accepted, because the update helper is always one version behind. Arguments given to an operation that takes none are refused; an empty object is accepted.
- **What a reply must contain.** A reply that lacks a member its type requires, or has a null inside a list its type knows, is an unreadable reply and not a record with a hole in it; a member the reader does not know is still skipped, whatever it holds. A vocabulary value the reader does not know (a health, a stage, an outcome) is treated as unknown, never as a failure to read.
- **Dates.** A date on the wire is ISO 8601 with a trailing `Z` or an offset, and nothing else is read. The times inside the reused job, printer-catalog and control records are carried as the text the service stored; they are not converted.
- **The version of a reply.** A reply carries the version of the request it answers. The one exception is `unsupported_version`, which is answered in version 2 whatever version was asked for.
- **Text a server supplied.** An error's message and the approved account in the details of `identity_changed` can repeat what a server said. Both are made printable and cut by one definition, which Core owns and the contract calls, when the reply is built: 512 UTF-16 units for any message, 200 for each identity value. It is enforced where the reply is made, not asked of whoever writes a handler.
- **A server that is being removed.** Reads answer; a repeated `servers.remove` succeeds with `removing` again; every other change is refused with `server_removing`.
- **The traffic meter while the stored data is not ready.** `transport.get` answers with an empty list instead of an error.
- **One way to read an envelope.** A request or a reply is read only by the contract's two parsing functions, which make the envelope checks. The envelope types refuse to be read by the serializer directly, so those checks cannot be walked around.

## Resolved after the retrospective reviews, 2026-11-10

Two retrospective reviews were run on 2026-11-10 over work that had merged without its reviewer seeing the final state ("About the process" below says how that happened). The first covered pull requests 37, 38, 31 and 30; the second covered the last fix rounds of pull requests 29, 35 and 28. Neither found a blocker. The first found that five decisions under "Resolved while planning the build" needed settling before the steps that build on them, and that this record lacked two rules.

They are settled here. Each settlement was taken on 2026-11-10 under the owner's standing instruction for this work: when a decision comes up, take the recommended option, record it and keep going. They follow the reviewer's recommended resolutions, made concrete where the build needs a number or a rule. They are not the owner's own words, and the owner can overrule any of them. None of it is built yet. The earlier wording stays where it was written, marked as changed. The reviews' findings about code are open findings in [the audit](../AUDIT-2026-11-10.md).

### Decisions that changed

**The staging ceiling.** This replaces "The staging ceiling" under "Resolved while planning the build" and the sentence about staged bytes under "Security consequences". The machine ceiling described here is a design decision. It is not in the code yet.

- What the code does, read at `077148f`: the client advertises `maxStagedBytes` 2^53 − 1 and `maxPdfBytes` and `maxZplBytes` 2,147,483,647, accepts a policy up to those, requires the staged limit to be at least the larger document limit, and refuses any other policy as `CONFIGURATION_REQUIRED` (`src/Qqq.PrintAgent.Core/Printing/RealtimeSettings.cs:52-63` and `:82-100`; the file is unchanged since `v1.2.1`). It never lowers a policy it accepted. That is the rule in [REALTIME.md](../REALTIME.md) ("advertises its actual ceilings ... refuses incompatible policy rather than silently reducing it"), so REALTIME.md is right and this record was wrong. The code has no machine-wide ceiling: staging is bounded only by the one server's own limit.
- What was wrong: the earlier text added a ceiling of 8 GiB with a guaranteed eighth of 1 GiB and left the advertised limits as they were. With eight servers no server can then hold more than 1 GiB, so a policy the client had accepted (documents of up to 2,147,483,647 bytes) could no longer be met, and a job inside that policy would be recorded FAILED once an eighth server was added. That is a silent reduction.
- The decision, taken under the standing instruction on 2026-11-10: what 1.2.x accepts stays accepted. The per-document ceiling stays 2,147,483,647 bytes, as the code advertises today. The machine ceiling for staged documents is 16 GiB (17,179,869,184 bytes), so that each server's eighth, 2 GiB (2,147,483,648 bytes), holds the largest document the client accepts. The reason: an installation whose server policy is above 1 GiB works on 1.2.x, and the upgrade to 2.0 must not start refusing it as `CONFIGURATION_REQUIRED`.
- The arithmetic. One eighth of the ceiling, 2 GiB, is reserved for each server that exists. It is a reservation, not a minimum when asked: what a server leaves unused of its eighth is never lent to another. As one rule: the sum, over the servers, of the larger of what each holds and 2 GiB never exceeds 16 GiB. What a server holds above its eighth comes from what is not reserved (16 GiB less 2 GiB for each server that exists), first come. With eight servers nothing is unreserved. The ceiling is a bound, not an allocation: nothing is set aside on disk.
- "Could ever be given" means the eighth: 2 GiB, the same for every server, whatever the number of servers. Every document an accepted policy allows fits it, so adding a server never makes an accepted policy unattainable. The earlier sentence about refusing "a document larger than a server could ever be given" is withdrawn, because no accepted policy can produce one.
- What the client advertises does not change. `maxPdfBytes` and `maxZplBytes` stay 2,147,483,647, and the eighth is what makes that true for every server at all times. `maxStagedBytes` stays as it is: a server's staged limit is a cap on that server's own total, the client honors a cap by staying under it, and a cap above what the computer has room for is never reached. The reviewer's recommendation was to advertise the real ceiling so that a policy above it is refused instead of silently unattainable. With the eighth at least as large as the largest document, the advertised document ceilings are real; advertising 16 GiB as the staged ceiling would refuse a policy 1.2.x accepts, and no job fails for want of it.
- A server's own staged limit still applies to its own total. Above its eighth a server gets what is unreserved and free, and a transfer waits for room as transfers wait under that limit today (`src/Qqq.PrintAgent.Windows/AgentRuntime.Pipeline.cs:278`). No job is failed for it.
- A server added while others hold more than their eighths gets its eighth as the transfers already admitted finish. Nothing admitted is taken back.
- Consequence for upgrades: no policy 1.2.x accepts is refused by 2.0 on this account, and the numbers the client sends do not change. REALTIME.md gets the machine ceiling in the change that builds it, because it describes the code.
- Considered and not taken: keeping 8 GiB and lowering the advertised document ceiling to its eighth, 1 GiB. It would make 2.0 refuse a policy that 1.2.x accepts.

**The interim companion.** This corrects "The companion between now and the tree".

- While the service can hold only one server, "follows the stored files" means the server is derived again from the stored files, by the function the upgrade uses to derive the first server. When the origin is unchanged the server keeps its identifier. When the stored files name another origin, that is a different server: it gets a new identifier, and the old server's registry row goes and its scopes are stamped removed, as they are when a server is removed. The certificate exception is carried only when it is for the new server's origin, as in the upgrade; otherwise it is not carried, and that is logged. Nothing keyed by an identifier therefore moves to another origin, and "No operation edits an address" still holds. The earlier wording did not say whether the identifier was kept; had it been, an exception accepted for one server would have covered another, and the first server's scopes would have shown as the second's earlier pairings.
- In that phase the printers shared and their profiles are carried to the server derived again, as the upgrade copies them: 1.x holds them for the computer, and the version 1 window has no step that shares them again.
- Once the service can hold several, a version 1 sign-in to a new address adds a server with no shared printers, as any new server has none. The operator shares them again on the Printers page. This differs from 1.x and from the single-server phase, and is recorded as interim behavior.
- Signing the others out stops unattended printing on every other server at each version 1 sign-in, until someone signs in to that server again.
- A version 1 sign-in to a ninth address is refused (`server_limit`, shown as the adapter's error text). The version 1 window cannot remove a server, and the command line's `servers remove` arrives later in the build, so until it does an interim build that holds eight servers cannot take a ninth. That is accepted for builds from this stage only: the adapter is deleted when the companion speaks version 2, which is before 2.0 is released.

**Who names the sign-in provider.** This adds to the decision of that name and changes step 1 of the wizard in [ADR-0005](0005-connection-wizard-and-server-tree.md), which is updated with it.

- When the certificate Windows refuses is the server's own, the provider is not known until the exception is accepted, because the sign-in description cannot be read before then. So the first warning names the server's address and says that the sign-in provider's address will be shown after the check. The re-check that follows returns the provider's origin, and the step then shows both addresses the exception covers, before the operator goes on.
- The service reads the sign-in description once in `servers.check` and again in `servers.add`, and a server whose certificate is unverified could name a different provider the second time. `servers.add` therefore carries the provider's origin that `servers.check` reported, for comparison only. The service still derives the origin from its own read and stores only that. When the origin it derives differs from the one the request carries, `servers.add` is refused and nothing is stored, so a stored exception never covers an origin the operator did not see. Comparing is not taking the value from the client.
- Which error code that refusal carries is not settled. It is chosen when the argument is added to the contract; no new code is assumed here.

**When a scope becomes current.** In the decision of that name, "On open" becomes "when the server's connection starts", once, by the host. Finding a readable pairing whose row is not current means decrypting the pairing file and deriving its scope. Done at every open of a store, that is the derivation the design removed ("no reader ever has to derive a scope from a pairing file"), and it makes opening a store a writer again; stores are opened for every pass of a lane and for every command. A stop between the file write and the flag can only be seen at a start, so that is where it is repaired. The rest of the decision stands: an unreadable pairing never changes the current row.

**Print capacity and the shared queue.** This adds to "A server's own print limit and the shared printer queue". Deciding both under the queue's lock is not enough, because whether a server can take another print changes outside that lock: a print slot is freed on another printer, traffic is allowed again, a transition ends, a setting changes. A queue that skipped that server's head job must be woken by that change, or the job for an idle printer stays skipped until something unrelated wakes the queue. So: every change to a server's print capacity raises the printer queues' signal, and a waiter takes the signal's task before it looks at the state, which is the order pull request 38 gave the existing waiters in the claim pipeline.

### Decisions that were missing

**Server names.** Neither record said what a server's name may be, and the name was to go in front of every log line of that server.

- The rule, enforced where a name enters, at `servers.add` and `servers.rename`, and refused there as `invalid_request`: 1 to 64 UTF-16 units after trimming; every code point one that Core's definition of printable text shows (the definition the pipe contract uses for text a server supplied); no square brackets; and not shaped like a server identifier or like the first eight or more characters of one, which is what `--server` takes as an identifier or a prefix.
- Log lines carry the server's short identifier (the first eight characters of its identifier, which `--server` accepts), not its name. The reasoning is in finding 10 of the first retrospective review: the activity log file redacts each joined line a second time, so a name containing a credential keyword (`Cookie:`, `token:`, `password=`) hides that server's lines; square brackets in a name forge structure inside a line; and the service log's own check of a name is a second, weaker copy of Core's rule. A short identifier has none of these and survives a rename.
- The rule is in force before any per-server log prefix is used. Nothing calls the service log's per-server view today (read at `077148f`).
- The upgrade and the wizard propose the host as the name. A host can break the rule (the brackets of an IPv6 literal, a host of eight or more hexadecimal digits). The code that proposes a name must produce one that passes; how is settled in the step that builds the registry.

**`printers.discover` is single-flight.** `printers.discover` is a read, and it runs a driver discovery, which starts a helper process. Without a rule, a hung driver and a client that retries could hold several of the sixteen pipe instances for 30 seconds each, with a helper process for each request, beside the host's own refresh. So the computer runs one discovery at a time: a `printers.discover` that arrives while a discovery is running, whoever started it, joins that discovery and gets its result. Each request keeps its own 30-second limit. (Finding 6 of the first retrospective review.)

### Consequences of removal and of scopes, recorded

These follow from "Removing a server", "Removal is restartable" and the scope decisions, and were not written down.

- After `removing` is answered, the server's credentials and its live session stay on this computer until the submission in progress ends, which is bounded only by the print deadline (24 hours at most), or until the next start.
- `servers.add` for an address that is being removed is answered `server_exists`, because the origin is unique until removal has finished. `server_removing` is for an operation that names a server, and `servers.add` names none.
- A server that is being removed counts toward the limit of eight until its removal has finished. (Taken under the standing instruction: its row, its files and its connection still exist.)
- If the service stops during a removal while a submission is in progress, the host finishes the removal at the next start, before connections start. The interrupted job is recorded OUTCOME_UNKNOWN in a scope that is then shown nowhere, so that outcome is never reported to the server or shown to the operator. The job is not printed again. This is recorded as a consequence; nobody has decided that it is acceptable.
- Not settled: what "unpaired" means for a server whose pairing file is missing while one of its scope rows is still current. The record says that an unreadable pairing never changes the current row and says nothing about a missing one. It is to be settled in the step that builds scopes.

### Smaller corrections made on 2026-11-10

- The `busy` row said "session or pairing"; the concurrency rules and the code include trust. The row now says so.
- "A reply carries the version of the request it answers" cannot hold for `unsupported_version`, which is answered in version 2. The exception is now named.
- A `servers.remove` repeated after removal has finished gets `server_not_found`. The `server_removing` row now says so.

### Where the contract as built does less than this record says

The rows for `server_refused` and `identity_changed`, and "Text a server supplied" and "One way to read an envelope" under "Resolved while reviewing the pipe contract", say what the contract enforces. The types merged in pull request 37 do less in four places, which the first retrospective review showed with a probe. This record stays the design, the gaps are open findings in the audit, and they are to be closed before anything serves the contract:

- The form and the bound of a message are applied by the two reply factories only; a reply built another way is written as given (audit W31).
- A details record is not tied to its code, and any text is accepted as a code (audit W32).
- The envelope types refuse the serializer only when it is given the contract's own options (audit W33).
- A cut value carries a three-character mark after the cut, so a message can be 515 units and an identity value 203 where this record says 512 and 200; and a missing value is sent as the text `(missing)` (audit W34). Whether the limit includes the mark is not settled.

## Decisions taken while working, per the owner's direction

About the runtime:

- The computer details stay per-computer rather than per-server.
- The command line's `--legacy-compat` bearer diagnostics are removed in 2.0. They talk to servers older than the realtime printing API, which the service itself cannot use, and they are the last code that sends a device token outside a session.
- Preparing for an update drains the work in hand. Jobs claimed before the pause pass the submission gate and finish; no new job is claimed; "Ready for update" is answered when no job is in hand or the deadline is reached, and the reply says which. In 1.2.x only prints already submitted were waited for, so a job that was still downloading was lost to the update.
- 2.0 does not honor control state written by the 0.2 polling prerelease. The "authorization required" flag and the LEGACY connection mode are read from an old file without failing and otherwise ignored; whether a sign-in is required comes from the live authorization state.
- A failed claim ends the wave, not one lane: one claim and one log line per wake-up, rather than sixteen.

About the process:

- The command-line assembly's release bar is 100% line coverage, like Core, because it is UI-free logic (K22-TEST-10).
- Coverage minimums are the measured value rounded down, less one point, because a few timing-dependent lines differ between runs.
- Pull requests merge into `release/2.0` by squash (K22-REPO-22), so the branch gets one commit per reviewed change.
- Until Gitea runs checks for this repository, a pull request merges into `release/2.0` on full local verification plus an independent review with no blocker or unresolved should-fix item (owner's rule, 2026-11-10).
- The review gate is on a commit id (owner's rule, 2026-11-10, asked for after the merges described in the next item). A pull request merges only when the reviewer's last report covers the exact head being merged, or the delta from the head it last passed to that head, and leaves nothing at should-fix or above. Every fix round goes back to the reviewer for confirmation, however small. Commits added by whoever integrates the change count like any others. A replay onto a newer base needs no new review only when `git range-diff` shows the patches identical. Pull requests that change only documentation or decision records are reviewed too. The reviewed head and the verdict go in the pull request description.
- Earlier merges broke that rule, which is why it is written down. Pull requests 28, 29, 35, 37 and 38 were merged with a final fix round their reviewer had not seen, and the documentation pull requests 30 and 31 were merged with no review at all. Two retrospective reviews were then run on 2026-11-10: one of pull requests 37, 38, 31 and 30 (no blocker, 14 should-fix, 7 nits), and one of the last fix rounds of pull requests 29, 35 and 28 (no blocker, 2 should-fix, 8 nits). Their findings about code are listed in [the audit](../AUDIT-2026-11-10.md) under "Found by the retrospective reviews"; their findings about this record are settled in "Resolved after the retrospective reviews" above.
- The owner approved, on 2026-11-10, running the 1.2.x upgrade install and the elevated desktop tests from the working session; Windows' own consent prompt is still answered by the owner each time.
- The workflow files under `.github` and `.gitea` are not changed by the 2.0 branches; what they need (no literal version for branch builds) is recorded in `docs/TODO.md` by the change that sets the version, for the CI work.
