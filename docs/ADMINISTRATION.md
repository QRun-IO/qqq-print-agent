# Administrator operations

These transport and capacity instructions describe release candidate `0.3.0-rc.1`.
The published `0.2.0-rc.1` installer uses its earlier native HTTP transport.
See [realtime architecture and settings](REALTIME.md) for the coordinated server requirement.

## Service and data

| Item | Location / identity |
| --- | --- |
| Service | `QqqPrintAgent`, automatic, LocalSystem |
| Local management | Administrator-only named pipe `QqqPrintAgent` |
| Service data | `%ProgramData%\QqqPrintAgent` |
| Credentials | Device proof and QQQ session protected separately with Windows DPAPI CurrentUser under LocalSystem |
| Submission journal | `journal.db` and SQLite WAL/SHM files |
| Worker coordination | Exclusive `worker.lock` in the data directory |
| Desktop preferences/logs | AppKit's per-user `qqq-print-agent` directory |

The MSI applies SYSTEM/Administrators permissions to the service directory. Do not loosen those ACLs or copy its credential to another machine/account. Backups should treat the entire directory as sensitive. Do not upload the database or protected configuration to public issues.

## Operational checks

```powershell
Get-Service QqqPrintAgent, Spooler
& 'C:\Program Files\QQQ Print Agent\Cli\qqq-print-agent.exe' service-status
```

Use the actual install path if it differs. Normal CLI commands and the companion use the authenticated installed service. Its printer list reflects the service identity; explicit isolated legacy diagnostics may see different queues under their invoking account.

Windows service messages are emitted through .NET hosting logging. Review Windows Event Viewer and the desktop activity log for the relevant timestamp; record job IDs and error text without customer document contents.

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
