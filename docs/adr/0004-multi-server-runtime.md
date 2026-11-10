# ADR-0004: Multi-server runtime, stored state and the management pipe contract

Status: accepted for 2.0 under the owner's direction of 2026-11-10 ("take the recommended option, record it, keep going"); not yet implemented. Refines [ADR-0003](0003-version-2.md), which decided that the service keeps up to eight server connections. Revised on 2026-11-10 after an independent review of the first draft; the revision is what is to be built.

## Context

In 1.2.x one `AgentRuntime` object is the connection: 51 of its 65 fields are per-connection state, one loop set serves it, and any of four tasks ending stops the service. The store derives an implicit "connection scope" from the one saved pairing; job events, job summaries, control state and realtime settings are keyed by it, while printer sharing, printer profiles and the certificate exception are machine-wide. The management pipe has one instance, serves one client at a time, takes string operations with no server identity and answers with text that callers match by prefix.

## Decisions

### Servers have a local identity

Each server the operator adds gets a `serverId`: a GUID created on this computer, never sent to any server and never reused. Everything per-server is keyed by it. The registry is a `servers` table (`server_id`, normalized origin, display name, position, added time); the origin is unique, so one server cannot be added twice.

A server's name can be changed. Its address cannot: the pairing, the session, the certificate exception and the journal scope are all bound to the origin, so a different address is a different server, added with the wizard, and the old one is removed. No operation edits an address.

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

The schema carries a version (`PRAGMA user_version`) and is migrated once, when the service starts, in a transaction. Opening a store no longer creates or alters tables. A service that finds a version newer than it knows does not start its connections and does not touch the state. It keeps serving the pipe: the snapshot says that a newer build wrote this state and that the newer build must be reinstalled; the snapshot, the log operations and the update operations work, so that it can be; and every other operation, `servers.add` included, is answered `state_newer`. It never migrates down. This matters between release candidates, because the installer allows any build with the same numeric version to replace another (`AllowSameVersionUpgrades`), so an older candidate can be installed over a newer one.

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
| A move fails (in use, access denied) | | Tried three times in that start. Then the service starts without connections and the snapshot reports the upgrade as incomplete and names the file. As with a newer schema, the snapshot, the log operations and the update operations work, and every other operation is answered `migration_incomplete`. The next start tries again. |

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
| `unknown_operation`, `invalid_request` | The operation does not exist, or its arguments are missing or malformed. |
| `server_not_found`, `server_ambiguous` | No server has this identifier; or (command line only) a name matches more than one. |
| `server_limit`, `server_exists` | Eight servers already exist; or this origin is already a server. |
| `busy` | Another operation that changes this server's session or pairing is running. |
| `not_signed_in`, `not_paired` | The operation needs a session, or a pairing, that this server does not have. |
| `server_incompatible`, `certificate_untrusted` | The compatibility check failed; `details` carries its stages and, for the second, a description of the certificate that was presented. |
| `sign_in_refused`, `bind_refused`, `identity_changed` | The server refused the session; refused to bind the pairing; or bound it for a different account than the approved one. |
| `enrollment_refused`, `enrollment_unconfirmed` | The server refused the code; or the outcome is not known and `details` says what was left behind. |
| `printer_not_found`, `job_not_found`, `job_not_current` | No such printer or job for this server; or the job belongs to an earlier pairing and is history. |
| `duplicate_risk_unconfirmed` | A retry could print twice and the request did not confirm that. |
| `state_unreadable` | Stored state for this server cannot be read; the message says which. |
| `state_newer`, `migration_incomplete` | The whole service is not running connections: a newer build wrote the state, or the upgrade from 1.x could not finish. The snapshot says which and why. |
| `update_in_progress` | The service is prepared for an update and is not accepting this. |
| `internal_error` | Anything else; the detail is in the service log, never in the reply. |

Concurrency:

- The pipe serves up to sixteen clients at once, so a sign-in that takes two minutes does not make status unavailable. A seventeenth waits for a free instance.
- Reads never wait. Operations that change one server's session, pairing or trust (`session.signIn`, `session.signOut`, `pairing.enroll`, `trust.set`, `servers.remove`) run one at a time per server; a second one is answered `busy` at once. Operations on different servers do not wait for each other.
- `servers.add` holds the registry for as long as it takes to validate and store, not for the sign-in, which has already happened in the companion.

Access, as the code has it:

- The service authenticates a client by the pipe's access list only: LocalSystem and Administrators. It checks nothing else about a client.
- The client verifies, before it sends anything, that the process serving the pipe is the installed service.
- Replies never carry a session credential, a device token or document content. One reply is an exception and is named here so it stays one: `session.signOut` returns the identity provider's sign-out request, which includes the provider's ID-token hint, for the companion to open in the browser. It is never logged.

The snapshot is one aggregated document, so the companion asks once for everything instead of once per server and per view:

- Once: the computer's printers, service state, update state, log settings, and the state of the stored data (ready, upgrade incomplete with the file it stopped at, or written by a newer build).
- Per server: identifier, name, address, position; health and its status text; the attention count that drives health; authentication (signed in, as whom, authorization blocked and why); paired flag and device identifier; transport state and the realtime meter; control state (desired state, last command); shared printers with each one's server-side approval; whether the certificate exception is on and the two origins it covers; and whether stored state is unreadable.
- Jobs stay paged and are queried per server.

The companion keeps polling the snapshot, every five seconds as today. A change notification pushed by the service is not part of 2.0: one aggregated request already removes the cost that made polling a problem, and a push channel is a second protocol to secure and test.

Clients older than the service:

- A request without `v` gets the one legacy-format reply that 1.x clients already display as an error, telling the operator to restart the companion.
- The one 1.x client that is certain to reach a 2.0 service is the update helper, which sends `resume-after-update` after the installer finishes. It is answered with that same error, and that is harmless: the update pause lives in memory, and the service the installer just started is not paused.
- From 2.0 on, the helper is always one version behind the service it resumes. `update.prepare` and `update.resume` therefore keep working in every pipe version a later service accepts; a later version that changes them keeps accepting the old form.

Operations:

- On the computer or the list of servers (no `server`): `snapshot`, `servers.list`, `servers.check`, `servers.add`, `servers.reorder` (the identifiers in their new order, in `args`), `computer.getProfile`, `computer.setProfile`, `logs.getSettings`, `logs.setSettings`, `logs.clear`, `update.prepare`, `update.resume`.
- On one server (`server` required): `servers.rename`, `servers.remove`, `session.signIn`, `session.signOut`, `pairing.enroll`, `connection.reconnect`, `trust.get`, `trust.set`, `printers.share`, `printers.setProfile`, `printers.testPrint`, `jobs.query`, `jobs.details`, `jobs.retry`, `jobs.cancel`, `jobs.acknowledge`.

When a server comes to exist:

- `servers.check` takes an address and, optionally, the operator's acceptance of the certificate exception for it. It also works for an address that is already a server, using that server's stored exception; that is how a repair re-checks. The service runs the compatibility check itself, under its own identity and certificate store, and returns the typed stages. This is the check that matters, because the service is what connects; in 1.x the companion checked under the user's certificate store while the service validated as LocalSystem, so the two could disagree.
- `servers.add` takes the address, the name, the certificate exception if the operator accepted one, and the session credential from a completed sign-in. It is atomic: the server, its certificate exception and its session are stored together, or nothing is. `servers.add` never creates a server without a session, so there is no provisional server to clean up. A server can lose its session later (sign-out, expiry, an upgrade that found only a pairing); that is what `not_signed_in` is for.
- `trust.set` and `session.signIn` are for servers that already exist.

Certificate exception:

- It stays what it is in 1.x, a development-only switch that accepts a certificate Windows does not trust, and it gets a boundary it lacks today. In 1.x, once switched on, it applies to every host the client contacts. In 2.0 it applies to two origins and no others: the server's own, and the sign-in provider that server names. Both are recorded with the server and shown to the operator in the warning.
- Pinning the accepted certificate's fingerprint was considered and rejected. The exception exists for self-signed certificates and private authorities such as a local Caddy root, and a private authority reissues its certificates on a short cycle; a pin would stop matching at each renewal and unattended printing would stop with it. The durable way to trust a private authority is to install its root in the Local Machine trusted root store, which needs no exception at all. The wizard and the Connection page say so wherever the exception is offered.
- When the trust stage fails, `servers.check` returns a description of the presented certificate (subject, issuer, expiry, fingerprint, and why Windows refused it), so the operator sees what the exception would accept. `trust.get` returns the same for an existing server, together with the two origins the exception covers.
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
- What is shared is bounded so that one server cannot use it up. A printer takes waiting servers in turn. Staged bytes have a machine-wide ceiling, of which each server is guaranteed one eighth; the rest goes to whoever asks first. The pipe has sixteen instances.
- Text a server supplies (titles, names, messages) stays bounded and is never used to build a path, a key or a command.

`docs/security/threat-model.md` gets these as boundary rows before the multi-server service merges.

## Resolved while planning the build

A planning pass over the code on 2026-11-10 found places where this record was silent or disagreed with the code. Each was settled as follows, under the owner's direction to take the recommended option and record it.

- **Where the pipe contract lives.** The envelope, error codes, operation names, arguments and results are one set of types in a `Management` folder of the Windows assembly, which the service, the command line and the presentation assembly already reference. A separate assembly would add a project and installer payload for no new boundary.
- **The companion between now and the tree.** The wizard and tree are a later stage, and until then the companion in this branch speaks version 1 of the pipe and knows one server. While the service can hold only one server, that server follows the stored files after each version 1 sign-in, enrollment and sign-out. Once it can hold several, an interim adapter serves version 1 requests against the first server in the list: a version 1 sign-in to a new address adds that server and moves it first, and signs the others out, because the old window cannot show a second live connection. The adapter is deleted when the companion speaks version 2. Only then does a request without `v` get the legacy error described above.
- **An unpaired server has no scope.** In 1.x an unpaired store uses one fixed scope name, which two signed-in, unpaired servers would share. An unpaired server reads defaults and cannot write to the journal. Rows the migration finds under that name, under an "unreadable" name or under no scope belong to no server.
- **When a scope becomes current.** The scope's row is inserted, not current, before any file is written. It becomes current in one transaction after the pairing file is in place. On open, a readable pairing whose row is not current is made current, which covers a stop between the two; an unreadable pairing never changes it.
- **A server's own print limit and the shared printer queue.** The queue skips a server whose head job is not yet staged, and also one that cannot take another print at that moment under its own policy. Both are decided together under the queue's lock, so a server busy on one printer neither holds another printer against everyone nor has its job for an idle printer held by someone else's long print.
- **The staging ceiling.** 8 GiB for the computer, so each server's guaranteed eighth is 1 GiB, above the default policy of 512 MiB. A server's own limit still applies to its own total. A document larger than a server could ever be given is refused before download and recorded FAILED, instead of waiting forever.
- **Removal is restartable.** The print deadline can be far longer than a pipe request may take, and removal spans files and rows with no transaction. `servers.remove` marks the server as being removed, stops its claims and answers that removal has started or has finished. The host finishes when a submission in progress ends, and at the next start if it was stopped first. The snapshot shows a server that is being removed.
- **Jobs waiting behind another printer's job** (audit finding W23) are fixed in the claim path, not by the printer queues: lanes claim while there is demand and a free slot, instead of in waves that must all finish.
- **Who names the sign-in provider.** `servers.check` returns the provider's origin whenever it could read the sign-in description, so the re-check after the operator accepts the exception can name both addresses. `servers.add` carries only that the operator accepted. The service derives and stores the provider's origin from its own read and never takes it from a client.
- **The snapshot carries no recent jobs and no log lines.** Jobs come from `jobs.query`; the service's in-memory log ring is removed. The command line's `snapshot` prints the aggregated document.
- **Ambiguous names are the command line's business.** The service takes a full server identifier only. The command line resolves a prefix or a name after listing the servers, and reports an ambiguous one itself.

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
- The owner approved, on 2026-11-10, running the 1.2.x upgrade install and the elevated desktop tests from the working session; Windows' own consent prompt is still answered by the owner each time.
- The workflow files under `.github` and `.gitea` are not changed by the 2.0 branches; what they need (no literal version for branch builds) is recorded in `docs/TODO.md` by the change that sets the version, for the CI work.
