# Administrator operations

These instructions describe client **1.0.1** with qbit-printing **1.0.0**, native printing API **2.3** and wire payload **v1**. See [realtime architecture and settings](REALTIME.md) for the coordinated server requirement.

## Service and data

| Item | Location / identity |
| --- | --- |
| Service | `QqqPrintAgent`, automatic, LocalSystem |
| Local management | Administrator-only named pipe `QqqPrintAgent` |
| Service data | `%ProgramData%\QqqPrintAgent` |
| Credentials | Device proof and QQQ session protected separately with Windows DPAPI CurrentUser under LocalSystem |
| Submission journal | `journal.db` and SQLite WAL/SHM files |
| Worker coordination | Exclusive `worker.lock` in the data directory |
| Desktop preferences | AppKit's per-user `qqq-print-agent` directory |
| Shared service/companion logs | `%ProgramData%\QqqPrintAgent\logs` |

The MSI applies SYSTEM/Administrators permissions to the service directory. Do not loosen those ACLs or copy its credential to another machine/account. Backups should treat the entire directory as sensitive. Do not upload the database or protected configuration to public issues.

## Operational checks

```powershell
Get-Service QqqPrintAgent, Spooler
& 'C:\Program Files\QQQ Print Agent\Cli\qqq-print-agent.exe' service-status
```

Use the actual install path if it differs. CLI commands and the companion use the authenticated installed service, so the printer list reflects the service identity, not the account that runs the command.

Use the built-in AppKit activity log for shared service and companion events, with source labels and severity filtering. Windows Event Viewer can also help diagnose service-host startup failures. Record relevant timestamps, job IDs and error text without customer document contents.

## Command line

`qqq-print-agent.exe` manages the installed service and nothing else. Each command is one request over the administrator-only pipe, so run it from an elevated prompt with the service running. Sign in through the companion first: enrollment and the connection commands act on the service's QQQ session.

| Command | What the service does |
| --- | --- |
| `printers` | Lists the printers it can see, as JSON |
| `enroll --server URL` | Pairs this computer with a QQQ server using a one-time enrollment code |
| `expose --printer ID --exposed true\|false` | Shares a printer with QQQ, or stops sharing it |
| `run` | Refreshes its connection to QQQ |
| `sign-out` | Ends its QQQ session (the identity provider's browser session is ended only by Sign out in the companion) |
| `service-status` | Reports its connection status |
| `snapshot` | Reports its state as JSON: connection, printers, recent jobs and logs |
| `help`, `--help`, `-h`, or no arguments; `--help` or `-h` after a command | Nothing; the usage is printed |

The enrollment code is read from the `QQQ_PRINT_ENROLLMENT_CODE` environment variable. When the variable is not set it is read from standard input, after the prompt `Enrollment code: ` on standard error: at a console it is typed without being shown (Backspace corrects, Enter ends), and when input is redirected one line is read, so a script can pipe the code in and standard output holds only the service's reply. White space around the code is dropped. The code is never accepted as an argument and never printed.

The exit code is 0 on success and 1 otherwise: wrong arguments, a service that cannot be reached or is not the installed one, a timeout, or a reply from the service that begins with `Error:`. Standard output carries what the service replied, including an `Error:` reply (the exit code, not the stream, tells a script it failed), and usage that was asked for. Standard error carries the command line's own failures (the service unreachable, a timeout, no enrollment code), the enrollment prompt, and the reason and usage for a refused command line.

A command accepts exactly the options shown, each once, in any order, each with a value that is not blank and does not begin with `--`. `--exposed` takes the word `true` or `false` in any letter case. Anything else is refused before the service is contacted and before a code is asked for, with one line saying what is wrong (`--server is required.`, `--printer needs a value.`, `'run' takes no arguments.` and so on) followed by the usage. The refusal names only the tool's own commands and options and never repeats what was typed, because a mistyped command line can contain an enrollment code.

Version 2.0 removed `--legacy-compat` and the options that existed for it (`--data-dir`, `--once`, `--service`); a command line that uses one is refused with a sentence saying so. The executable is also no longer a printer worker (`--printer-worker`): the service starts its own executable for that. If that mode was ever used on a machine, its separate per-user state is still in `%LOCALAPPDATA%\QqqPrintAgent` (`connection.dpapi`, `journal.db`, `worker.lock`). Nothing reads it any more; delete it once the device it enrolled has been revoked in QQQ. Leave `auth-callback.log` in that folder: the browser sign-in helper still writes it.

## Credentials and network

Sign in through standard QQQ authentication, then associate an existing unbound pairing or enroll a new device explicitly. Every normal printing request requires the current QQQ user session and secondary device proof; server user, tenant and record permissions still apply. Session renewal does not change the journal's device identity. Revoke the device in QQQ when decommissioning a machine. Revocation cannot recall a job already accepted by the Windows spooler.

Allow outbound WSS upgrades and HTTPS to the configured QQQ origin. The service maintains one authenticated realtime connection for small controls/status/receipts and uses independent HTTPS streams for documents. There is no periodic job/control polling or fallback. Certificate validation uses Windows/.NET trust; do not disable it. Plain HTTP/WS is an explicit loopback development exception. Proxy-specific authentication and long-running production connections require deployment qualification.

## Queue and control health

The companion displays observed connection health, desired pause/disconnect state and the last remote action/outcome. The Jobs page includes a bounded preview of at most 50 server-queued jobs, not the entire cloud backlog. Inspect server job and command records for authoritative status. A green connection or READY printer observation does not prove physical output.

Use QQQ's device actions to pause/resume dispatch, disconnect/reconnect operation, refresh inventory or revoke credentials. Disconnect retains the realtime control path for reconnect and in-flight receipts. Queue clearing cancels only currently QUEUED server jobs and retains history. Spool cancellation targets an exact locally owned job; a success cannot recall paper. Reconnect never restarts Windows or the service and cannot restore revoked credentials.

## Restart and uncertainty

The local journal records submission state and pending cloud events. Restarting preserves those records. If an attempt was interrupted during submission, the worker avoids silently submitting it again and reports an uncertain outcome when reclaimed. Do not erase or manually edit rows to force progress. Inspect the physical queue and coordinate an explicit reprint if needed.

There is no global transaction spanning the cloud database and a printer. `SPOOLED` is the observable success boundary in this release.

## Capacity and retention

Authorized QQQ administrators use **Printing connection and limits** to set document sizes, copies, bounded transfer/print concurrency, staging, bandwidth, control queues, liveness, reconnect and timeouts. Defaults are 100 MiB PDF, 20 MiB ZPL, 100 copies and one download/print lane. The configurable native submission timeout defaults to 120 seconds. Invalid policy or values beyond the client's declared ceiling stop new work; the agent does not silently clamp them.

**Printing queue and storage limits** controls the separate server admission policy. Server document retention and local journal detail retention are distinct. The service streams into protected temporary files, verifies length/hash and submits through a bounded isolated child. Per-printer ordering remains serialized even when different printers run concurrently. See [settings and architecture](REALTIME.md) and [measured storage scope](SCALABILITY.md). Monitor disk use; retained deduplication summaries have no fixed lifetime size ceiling.
