# Developer guide

## Architecture

Current source is version `1.0.1` (a packaging fix; see CHANGELOG.md), with native printing 2.3 (wire payload v1).
See [realtime architecture](REALTIME.md) for worker separation, policy, streaming
and reconnect rules. The service never falls back from WSS to job/control polling.

| Project | Responsibility |
| --- | --- |
| Qqq.PrintAgent.Core | QQQ authentication, HTTP/WSS clients, policy, streaming and submission state machine |
| Qqq.PrintAgent.Windows | Native spooler/PDF adapter, SQLite/DPAPI store and local management runtime |
| Qqq.PrintAgent.Service | .NET Windows Service host |
| Qqq.PrintAgent | AppKit/WinUI setup, details and tray companion |
| Qqq.PrintAgent.Cli | Authenticated service management and explicit isolated legacy diagnostics |

The service owns the credential, journal and print operations. The UI is optional at runtime and sees only credential-free snapshots. Service IPC uses the `QqqPrintAgent` named pipe and accepts administrator clients. `snapshot` returns connection health, all locally discovered printers with exposure state, the latest 50 jobs and the latest 100 in-memory log entries. `set-exposure` persists a selected queue and returns a fresh snapshot. Legacy `status`, `printers` and `enroll` operations remain available.

The cloud API lives in [qqq-print-protocol](https://git.kof22.com/QRun-IO/qqq-print-protocol). Its inventory is the complete currently exposed printer set. The server separately owns authorization/approval. Removing local exposure filters inventory and prevents new local submission to that queue. It cannot retract a job already handed to Windows.

## Reproducible checks

Check out this repository and the protocol repository as siblings, then pin the protocol checkout to the exact SHA in `.github/workflows/ci.yml`. All package versions are centralized and lock files are committed.

```powershell
./build/Verify.ps1
```

This performs locked restore, Release solution build, core tests and Windows/CLI tests, with coverage/TRX output under TestResults, and then fails if an assembly's merged line coverage is below its minimum in `build/minimum-coverage.json`. UI tests compile in the solution but are not automatically run by hosted CI. Use an elevated interactive desktop and `K22_UITEST_EXE` for those tests.

## Testing the service runtime

`AgentRuntime` reaches QQQ in one way: a signed-in session and the realtime channel opened through it. Its constructor takes no API client, so a test cannot make it do anything the installed service does not. Windows-layer tests (`tests/Qqq.PrintAgent.Cli.Tests`) use hand-written fakes at the boundaries:

- `RealtimeRuntimeFixture` runs a runtime on a temporary store, signs it in through a real `QqqSessionClient` whose HTTP goes to a scripted handler, and connects it over a loopback WebSocket to a scripted peer. A test sends the server's frames (WELCOME, DESIRED_STATE, SETTINGS, COMMAND, JOB_AVAILABLE, PRINTERS), reads what the agent sent, talks to the runtime over its management pipe, and can restart the runtime on the same store. The peer can acknowledge a message, refuse it with an ERROR frame, or drop the connection without acknowledging it; the fake printer can hold a submission open and script a spooler cancellation.
- `SessionFixture` supplies a fake session with no network, for decisions about sign-in, pairing and the protected files. Where a test needs authorization evaluated once it calls `EnsureAuthorizationAsync`, the check the reconnect loop and the heartbeat make.

Wait for the event (a recorded message, a log line, a signal from a fake) with `fixture.WaitAsync`, never for a length of time. Where the runtime itself waits on the clock, which today is the heartbeat interval, pass a `ManualTimeProvider` and let an interval pass with `fixture.HeartbeatAsync`. To show that something did not happen, find an event that can only follow it: commands are handled in arrival order, claims are made one at a time, and frames on one socket arrive in the order they were sent.

## Local server and CLI

Use a disposable native QQQ host from the sibling qbit-printing project, using qbit-printing 1.0.0 on QQQ 4.1.0-RC.21 and advertising native printing API 2.3. Sign in through the companion's advertised QQQ authentication flow before using normal CLI commands. The service owns the session, device proof and unattended printing. A bearer-only demo is not a compatible realtime host.

```powershell
$agent = '.\src\Qqq.PrintAgent.Cli\bin\x64\Debug\net10.0-windows10.0.19041.0\qqq-print-agent.exe'
& $agent service-status
& $agent snapshot
& $agent printers
& $agent run
```

`run` requests a connection refresh through the authenticated service; it does not start a second foreground worker. Device setup and explicit printer exposure use that same service. Submit a physical test only when authorized, after approving the selected exposed queue in QQQ.

Direct bearer diagnostics require explicit `--legacy-compat` and a separate private `--data-dir`; they cannot use `--service` and are never an automatic fallback. The exclusive `worker.lock` prevents simultaneous workers on one journal. Do not point a development worker at installed state.

## Virtual file printer (macOS/Linux)

`tools/virtual-agent/` holds a dependency-free Node.js agent that speaks the realtime contract and
writes each verified job to a directory instead of a Windows queue. Use it to exercise a QQQ server
with the printing QBit without Windows or hardware; it does not qualify this service. See
[its README](../tools/virtual-agent/README.md).

## Reliability boundary

The worker persists receipt and SUBMITTING before invoking Windows. It records SPOOLED and the spooler job ID afterward and replays pending cloud events. A crash between spool acceptance and recording cannot be made atomic. The worker uses OUTCOME_UNKNOWN rather than automatic reprint. Preserve that distinction when extending retry/cancellation logic.

## Packaging

```powershell
./build/Build-Installer.ps1 -ProductVersion 1.0.1 -ReleaseVersion 1.0.1
```

MSI ProductVersion is numeric; assembly/package informational version may contain the prerelease suffix. Service, companion and CLI are published into separate self-contained runtime directories and shipped in one machine MSI. Do not reintroduce the template's per-user Velopack updater.

`./build/Package-Release.ps1` (defaults `1.0.1` / `1.0.1`) is the repository entry point for the unsigned local package; the signed stable package is built by the release workflow. It runs the same packaging checks as `.github/workflows/ci.yml` (`Test-WindowsSdkInventory.ps1 -VerifyPackages`, `Test-BrandedInstaller.ps1`, `Test-PackagingInputs.ps1`, `Build-Installer.ps1`) and assembles `artifacts/release` with the versioned MSI, `Install-Branded.ps1`, `branding.example.json` and `SHA256SUMS`. It does not build or run Core.Tests.

`Qqq.PrintAgent.NativeCI.slnx` is `Qqq.PrintAgent.slnx` without the `Core.Tests` project, for hosts that must not build that assembly (for example `dotnet build Qqq.PrintAgent.NativeCI.slnx --configuration Release '-p:Platform=x64' -warnaserror`, then `dotnet test tests/Qqq.PrintAgent.Cli.Tests`). `Qqq.PrintAgent.slnx` and the test projects are unchanged, so `Verify.ps1` still covers Core.Tests; run those tests on a clean CI runner.

## Machine update integration

`MainWindow.Updates` wires AppKit `UpdateCoordinator`/`ContentDialogPrompter` to Windows `MsiUpdateService : IUpdateService`. The persisted startup-check preference, version/channel display, splash duration and help/credits use the shared shell. The UI serializes manual/startup checks; confirmation is required before apply.

`GitHubMsiReleaseClient` reads the fixed QRun-IO release feed, applies strict version/channel/minimum policy and validates bounded release assets and SHA-256. `MsiUpdateInstaller` validates protected staging, ACLs and reparse boundaries, copies the installed self-contained CLI for handoff, rechecks the MSI and uses Windows Installer for all components. The runtime's `prepare-update`/`resume-after-update` operations coordinate a bounded claim pause while preserving heartbeats. No server-supplied executable, per-user replacement package or persistent privileged updater is involved. See the [threat model](security/threat-model.md) for trust and failure boundaries.

Historical 0.1.3-rc.1 verification: locked restore, zero-warning Release build, **36 Core + 127 Windows/CLI tests (163 total)**. Synthetic release/download/hash/request/ACL/version/maintenance tests do not install an MSI. Installed shell and actual upgrade acceptance remain separate gates; use [session state](SESSION-STATE.md) for current verification. Completed protected update stages are retained for manual administrator review.
