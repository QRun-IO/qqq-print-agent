# Developer guide

## Architecture

| Project | Responsibility |
| --- | --- |
| Qqq.PrintAgent.Core | Wire DTOs/client, submission state machine and exposure filter |
| Qqq.PrintAgent.Windows | Native spooler/PDF adapter, SQLite/DPAPI store and local management runtime |
| Qqq.PrintAgent.Service | .NET Windows Service host |
| Qqq.PrintAgent | AppKit/WinUI setup, details and tray companion |
| Qqq.PrintAgent.Cli | Scriptable setup and foreground diagnostics |

The service owns the credential, journal and print operations. The UI is optional at runtime and sees only credential-free snapshots. Service IPC uses the `QqqPrintAgent` named pipe and accepts administrator clients. `snapshot` returns connection health, all locally discovered printers with exposure state, the latest 50 jobs and the latest 100 in-memory log entries. `set-exposure` persists a selected queue and returns a fresh snapshot. Legacy `status`, `printers` and `enroll` operations remain available.

The cloud API lives in [qqq-print-protocol](https://github.com/QRun-IO/qqq-print-protocol). Its inventory is the complete currently exposed printer set. The server separately owns authorization/approval. Removing local exposure filters inventory and prevents new local submission to that queue. It cannot retract a job already handed to Windows.

## Reproducible checks

Check out this repository and the protocol repository as siblings, then pin the protocol checkout to the exact SHA in `.github/workflows/ci.yml`. All package versions are centralized and lock files are committed.

```powershell
./build/Verify.ps1
```

This performs locked restore, Release solution build, core tests and Windows/CLI tests, with coverage/TRX output under TestResults. UI tests compile in the solution but are not automatically run by hosted CI. Use an elevated interactive desktop and `K22_UITEST_EXE` for those tests.

## Local server and CLI

Start the sibling qbit-printing demo as described in its README. The default local API origin is `http://localhost:18081`. The demo's administrative credential is a local harness mechanism, not the production QQQ authentication design.

```powershell
$agent = '.\src\Qqq.PrintAgent.Cli\bin\x64\Debug\net10.0-windows10.0.19041.0\qqq-print-agent.exe'
$data = Join-Path $env:LOCALAPPDATA 'QqqPrintAgent-Development'
& $agent printers
# Enrollment code is read from QQQ_PRINT_ENROLLMENT_CODE or the prompt.
& $agent enroll --server http://localhost:18081 --data-dir $data
& $agent expose --printer '<discovered-printer-id>' --exposed true --data-dir $data
& $agent run --data-dir $data --once
```

Enable the selected queue on the server, submit a recognizable test document, then run the worker again. Without `--once`, it polls until Ctrl+C. Use a dedicated private directory; do not share it with the installed service. The exclusive worker.lock prevents simultaneous workers on one journal.

For installed service management use elevated `enroll --service`, `expose --service`, `service-status` or `snapshot`. The CLI `printers` command runs under the invoking account; the service snapshot is authoritative for service-visible queues.

## Reliability boundary

The worker persists receipt and SUBMITTING before invoking Windows. It records SPOOLED and the spooler job ID afterward and replays pending cloud events. A crash between spool acceptance and recording cannot be made atomic. The worker uses OUTCOME_UNKNOWN rather than automatic reprint. Preserve that distinction when extending retry/cancellation logic.

## Packaging

```powershell
./build/Build-Installer.ps1 -ProductVersion 0.1.0 -ReleaseVersion 0.1.0-rc.1
```

MSI ProductVersion is numeric; assembly/package informational version may contain the prerelease suffix. Service, companion and CLI are published into separate self-contained runtime directories and shipped in one machine MSI. Do not reintroduce the template's per-user Velopack updater.
