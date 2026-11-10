# ADR-0004: Multi-server runtime, stored state and the management pipe contract

Status: accepted for 2.0 under the owner's direction of 2026-11-10 ("take the recommended option, record it, keep going"); not yet implemented. Refines [ADR-0003](0003-version-2.md), which decided that the service keeps up to eight server connections.

## Context

In 1.2.x one `AgentRuntime` object is the connection: 51 of its 65 fields are per-connection state, one loop set serves it, and any of four tasks ending stops the service. The store derives an implicit "connection scope" from the one saved pairing; job events, job summaries, control state and realtime settings are keyed by it, while printer sharing, printer profiles and the certificate exception are machine-wide. The management pipe has one instance, serves one client at a time, takes string operations with no server identity and answers with text that callers match by prefix.

## Decisions

### Servers have a local identity

Each server the operator adds gets a `serverId`: a GUID created on this computer, never sent to any server and never reused. Everything per-server is keyed by it. The server's address can be corrected and its name changed without losing its history, and two entries can never be confused because their addresses look alike. The registry is a `servers` table (`server_id`, normalized origin, display name, position, added time); the origin is unique, so one server cannot be added twice. The limit is eight; adding a ninth is refused with a message that says so.

### What is per-server and what is per-computer

Per server: the pairing, the session, the certificate exception, which printers are shared, each shared printer's display name, description, location and media selection, the job journal, control state, realtime settings and the staging directory.

Per computer: the Windows printers that exist, the computer's own name, description and location, log settings, the Windows service, updates and appearance. The computer details describe this machine, so every server receives the same ones.

A printer may be shared with several servers. Removing a server stops its connection and deletes its pairing and session; its job history stays in the journal until retention prunes it, because history is evidence.

### Stored state

Secrets move from the state directory's root to `servers/<serverId>/` (`connection.dpapi`, `qqq-session.dpapi`, `staging/`), still protected with DPAPI under the service identity. The journal stays one SQLite database. The existing connection scope (a hash of origin and device) is kept as the key for events, jobs, control state and realtime settings, so a new pairing on the same server still starts a clean scope while the old history remains; a store is opened for an explicit server rather than discovering its scope from whichever pairing file is on disk.

Printer sharing and printer profiles get new tables keyed by `server_id` (`server_printers`, `server_printer_profiles`). The old machine-wide tables are not reused under a new meaning: an older build that somehow ran against this directory would otherwise read every server's sharing as its own.

The schema carries a version (`PRAGMA user_version`) and is migrated once, when the service starts, in a transaction. Opening a store no longer creates or alters tables.

### Upgrade from 1.x

The first start of 2.0 turns the existing connection into the first server: it creates the `servers` row from the saved pairing's origin, moves the two protected files and the staging directory under `servers/<serverId>/`, and copies the sharing choices and printer profiles to that server. File moves are recorded before they happen and completed on the next start if interrupted. When the pairing cannot be decrypted but the session can, the session's origin names the server; when neither can, the unreadable files are kept aside untouched, the journal rows keep their scope, and the computer starts with no servers and says why. Nothing is deleted by the upgrade. The installer already refuses a downgrade.

### Runtime structure

- **Host.** Owns the pipe server, the server registry, the per-printer queues and one connection per server. It supervises them: a connection that faults is restarted with backoff, and neither a faulting connection nor a faulting pipe client stops anything else.
- **Server connection.** One per server: the session client, the realtime channel, authorization state, control state, settings and its claim pipeline. This is what the per-connection fields of `AgentRuntime` become.
- **Printer queue.** One per Windows printer, shared by all servers. A job is identified by server and job together. The queue hands the device to one submission at a time, in arrival order, so two servers cannot interleave on one printer; each server's own policy (concurrency, bandwidth, deadlines) still applies inside its connection. The spooler document name carries a short server tag so an operator can tell jobs apart in the Windows queue.

`PrintWorker`'s reliability boundary does not change: SUBMITTING is recorded before the spooler call, and a submission interrupted after that is OUTCOME_UNKNOWN, never reprinted automatically.

### Management pipe, version 2

Requests and replies are JSON objects on the existing framing.

```json
{ "v": 2, "op": "printers.share", "server": "<serverId>", "args": { "printerId": "...", "shared": true } }
{ "v": 2, "ok": true, "result": { } }
{ "v": 2, "ok": false, "error": { "code": "server_not_found", "message": "..." } }
```

- `server` is required for per-server operations and absent for per-computer ones; an identifier that is not in the registry is `server_not_found`.
- Error codes are a closed set that clients switch on; `message` is for people. No client decides anything by matching text.
- A request without `v`, from a 1.x companion or command line during an update, gets the one legacy-format reply those clients already display as an error, telling the operator to restart the companion. A `v` the service does not know is `unsupported_version`.
- The pipe serves several clients at once, so a sign-in that takes two minutes does not make status unavailable.
- The snapshot is one aggregated document: the computer's printers and service state once, then one entry per server with its health, authentication, transport and shared printers. Jobs stay paged and are queried per server.
- Access is unchanged: LocalSystem and Administrators only, with the installed peer verified before a command is accepted. Replies never carry a credential, a token or document content.

Operations: `servers.list`, `servers.add`, `servers.rename`, `servers.reorder`, `servers.remove`; per server `session.signIn`, `session.signOut`, `pairing.enroll`, `connection.reconnect`, `trust.get`, `trust.set`, `printers.share`, `printers.setProfile`, `printers.testPrint`, `jobs.query`, `jobs.details`, `jobs.retry`, `jobs.cancel`, `jobs.acknowledge`; per computer `snapshot`, `computer.getProfile`, `computer.setProfile`, `logs.getSettings`, `logs.setSettings`, `logs.clear`, `update.prepare`, `update.resume`.

### Command line

`servers` lists them. Every per-server command takes `--server <name or id>`; it may be omitted while exactly one server exists. `enroll --server URL` from 1.x becomes `servers add URL` followed by `enroll --server <name>`: `--server` now always selects, never names an address.

### Companion

The left pane is a tree: one node per server showing its name and health, with Printers, Jobs and Connection beneath it; then Add server, which opens the wizard; then the per-computer items (Computer, Logs, Settings). The footer and the tray icon show the worst health across servers, and the tooltip names each server. Busy state is per server, so a sign-in on one does not freeze the others.

The wizard validates the address first and creates the server only when the operator has signed in; cancelling earlier leaves nothing behind.

## Security consequences

One LocalSystem service now holds credentials for several servers, so the boundary between connections is a security boundary:

- A server can address only the printers shared with it, and sees only its own jobs.
- Each connection has its own protected files, staging directory, journal scope and certificate exception; none is derived from another's.
- A protocol violation, an authorization loss or a hostile payload ends or blocks that one connection.
- Bounds are per server by policy and also per computer: eight servers, one submission per printer at a time, and a machine-wide ceiling on staged bytes so eight servers cannot together exhaust the disk.
- Text a server supplies (titles, names, messages) stays bounded and is never used to build a path, a key or a command.

`docs/security/threat-model.md` gets these as boundary rows before the multi-server service merges.

## Decisions taken while working, per the owner's direction

- The command-line assembly's release bar is 100% line coverage, like Core, because it is UI-free logic (K22-TEST-10).
- Coverage minimums are the measured value rounded down, less one point, because a few timing-dependent lines differ between runs.
- Pull requests merge into `release/2.0` by squash (K22-REPO-22), so the branch gets one commit per reviewed change.
- Until Gitea runs checks for this repository, a pull request merges into `release/2.0` on full local verification plus an independent review with no blocker or unresolved should-fix item (owner's rule, 2026-11-10).
- The computer details stay per-computer rather than per-server.
- The command line's `--legacy-compat` bearer diagnostics are removed in 2.0. They talk to servers older than the realtime printing API, which the service itself cannot use, and they are the last code that sends a device token outside a session.
- The owner approved, on 2026-11-10, running the 1.2.x upgrade install and the elevated desktop tests from the working session; Windows' own consent prompt is still answered by the owner each time.
