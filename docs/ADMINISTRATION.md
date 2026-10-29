# Administrator operations

## Service and data

| Item | Location / identity |
| --- | --- |
| Service | `QqqPrintAgent`, automatic, LocalSystem |
| Local management | Administrator-only named pipe `QqqPrintAgent` |
| Service data | `%ProgramData%\QqqPrintAgent` |
| Credential | `connection.dpapi`, Windows DPAPI CurrentUser under LocalSystem |
| Submission journal | `journal.db` and SQLite WAL/SHM files |
| Worker coordination | Exclusive `worker.lock` in the data directory |
| Desktop preferences/logs | AppKit's per-user `qqq-print-agent` directory |

The MSI applies SYSTEM/Administrators permissions to the service directory. Do not loosen those ACLs or copy its credential to another machine/account. Backups should treat the entire directory as sensitive. Do not upload the database or protected configuration to public issues.

## Operational checks

```powershell
Get-Service QqqPrintAgent, Spooler
& 'C:\Program Files\QQQ Print Agent\Cli\qqq-print-agent.exe' service-status
```

Use the actual install path if it differs. The companion's printer list reflects the service identity. Running CLI `printers` directly reflects the current CLI account instead, which may see different queues.

Windows service messages are emitted through .NET hosting logging. Review Windows Event Viewer and the desktop activity log for the relevant timestamp; record job IDs and error text without customer document contents.

## Credentials and network

QQQ issues an enrollment code and a dedicated device credential. Each device is scoped by the server; the connector may claim and download only its assigned work. Revoke the device in QQQ when decommissioning a machine. Revocation cannot recall a job already accepted by the Windows spooler.

Only outbound HTTPS is required. The initial agent polls every five seconds. HTTPS validation uses Windows/.NET trust; do not disable certificate checks. Local `http://localhost` is a development exception, not a production endpoint. Proxy-specific authentication has not been qualified.

## Restart and uncertainty

The local journal records submission state and pending cloud events. Restarting preserves those records. If an attempt was interrupted during submission, the worker avoids silently submitting it again and reports an uncertain outcome when reclaimed. Do not erase or manually edit rows to force progress. Inspect the physical queue and coordinate an explicit reprint if needed.

There is no global transaction spanning the cloud database and a printer. `SPOOLED` is the observable success boundary in this release.

## Capacity and retention

Documents are limited to 20 MiB and copies to 1–100. The worker serializes its jobs. The native renderer is bounded but still runs in the service process. Journal/event retention and renderer subprocess isolation are planned; monitor disk use and use this prerelease in a controlled pilot rather than assuming unbounded production capacity.

