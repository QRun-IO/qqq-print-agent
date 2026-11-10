# Developer guide

## Architecture

The source version is `VersionPrefix` in `Directory.Build.props` (see CHANGELOG.md for what each version changed), with native printing 2.3 (wire payload v1).
See [realtime architecture](REALTIME.md) for worker separation, policy, streaming
and reconnect rules. The service never falls back from WSS to job/control polling.

| Project | Responsibility |
| --- | --- |
| Qqq.PrintAgent.Core | QQQ authentication, HTTP/WSS clients, policy, streaming and submission state machine |
| Qqq.PrintAgent.Windows | Native spooler/PDF adapter, SQLite/DPAPI store and local management runtime |
| Qqq.PrintAgent.Service | .NET Windows Service host |
| Qqq.PrintAgent.Presentation | The companion's presentation logic without a window: typed service client, texts, enablement rules, paging and sorting state, sign-in state machine |
| Qqq.PrintAgent | AppKit/WinUI setup, details and tray companion; views call the presentation assembly |
| Qqq.PrintAgent.Cli | Management of the installed service over its pipe; also the MSI update helper and browser sign-in callback forwarder |

The service owns the credential, journal and print operations. The UI is optional at runtime and sees only credential-free snapshots. Service IPC uses the `QqqPrintAgent` named pipe and accepts administrator clients. `snapshot` returns connection health, all locally discovered printers with exposure state, the latest 50 jobs and the latest 100 in-memory log entries. `set-exposure` persists a selected queue and returns a fresh snapshot. Legacy `status`, `printers` and `enroll` operations remain available.

That paragraph describes version 1 of the pipe, which the service still speaks. Version 2 ([ADR-0004](adr/0004-multi-server-runtime.md)) exists so far as a contract only, in `src/Qqq.PrintAgent.Windows/Management`: the envelope (`ManagementRequest`, `ManagementReply`, `ManagementError`, with arguments, results and details held as a `ManagementPayload` whose text form is fixed so none of them can land in a log line), the one serializer (`ManagementJson`), the error codes (`ManagementErrorCode`), the operation table (`ManagementOperations`: scope, kind, time limit, argument and result record for each of the 30 operations), one argument record per operation that takes arguments, the result records, and `ServerSelector`, which resolves the command line's `--server`. Nothing serves or sends it yet. Its wire form is held by golden files in `tests/Qqq.PrintAgent.Cli.Tests/Fixtures/management`: one request and one reply per operation, one failure per error code that carries details, and two further snapshots as a client will really see them (two healthy servers; a service whose stored data is not ready). A change to a record changes its golden file and the matching example in `ManagementSamples.cs` in the same commit; the tests fail when an operation or such a code has no golden file, or a file has no operation. Additions to a result are compatible (readers skip unknown members, and treat an unknown value of a vocabulary as unknown); a request with an unknown member is refused; a member repeated at any depth, a missing required member and a null inside a list the record knows are refused in both directions. An envelope is read only by `ManagementJson.ParseRequest` and `ManagementJson.ParseReply`; the envelope types refuse the serializer. Every error message is made printable and cut at 512 characters by the reply factories, with Core's `QqqCompatibilityProblem.Printable`.

The cloud API lives in [qqq-print-protocol](https://git.kof22.com/QRun-IO/qqq-print-protocol). Its inventory is the complete currently exposed printer set. The server separately owns authorization/approval. Removing local exposure filters inventory and prevents new local submission to that queue. It cannot retract a job already handed to Windows.

## Reproducible checks

Check out this repository and the protocol repository as siblings, then pin the protocol checkout to the exact SHA in `.github/workflows/ci.yml`. All package versions are centralized and lock files are committed.

```powershell
./build/Verify.ps1
```

This performs locked restore, Release solution build, core tests, Windows/CLI tests and presentation tests, with coverage/TRX output under TestResults, and then fails if an assembly's merged line coverage is below its minimum in `build/minimum-coverage.json`. For a change to the companion's presentation logic the quick loop is `dotnet test tests/Qqq.PrintAgent.Presentation.Tests -c Release -p:Platform=x64`; that suite needs no service, pipe or desktop and its assembly is gated at 100%. UI tests compile in the solution but are not automatically run by hosted CI. Use an elevated interactive desktop and `K22_UITEST_EXE` for those tests.

## Testing the service runtime

`AgentRuntime` reaches QQQ in one way: a signed-in session and the realtime channel opened through it. Its constructor takes no API client, so a test cannot make it do anything the installed service does not. Windows-layer tests (`tests/Qqq.PrintAgent.Cli.Tests`) use hand-written fakes at the boundaries:

- `RealtimeRuntimeFixture` runs a runtime on a temporary store, signs it in through a real `QqqSessionClient` whose HTTP goes to a scripted handler, and connects it over a loopback WebSocket to a scripted peer. A test sends the server's frames (WELCOME, DESIRED_STATE, SETTINGS, COMMAND, JOB_AVAILABLE, PRINTERS), reads what the agent sent, talks to the runtime over its management pipe, and can restart the runtime on the same store. The peer can acknowledge a message, refuse it with an ERROR frame, or drop the connection without acknowledging it; the fake printer can hold a submission open, count entries into it and script a spooler cancellation. The scripted server refuses, and counts with the forbidden polls, a claim made before it acknowledged the agent's applied state.
- `SessionFixture` supplies a fake session with no network and a manual clock, for decisions about sign-in, pairing and the protected files. `EvaluateAsync` lets the renewal interval pass and evaluates authorization once, the check the reconnect loop and the heartbeat make; `RunOnePassAsync` runs the service loop for one pass, after which `AssertSentNothing` shows that nothing was sent.
- `SingleServerHarness` is the runtime as tests hold it, and the only type in the test project that names the runtime class. Both fixtures build it, and a test that needs a runtime of its own constructs the harness, never the runtime. It makes the management pipe on a private name and sends a command over it with `PipeAsync`. The constants and pure functions tests read from the runtime (the update limits, the reconnect delay, the job list merge) are read from the harness too.
- `ScriptedPrintServer` is the server `RealtimeRuntimeFixture` runs against: the realtime peer, the HTTP handler, the job queue and the accepted connections. It knows nothing about the runtime and shares nothing between instances, so several can stand beside one service; the fixture repeats the members of its one server under their old names.

Wait for the event (a recorded message, a log line, a signal from a fake) with `fixture.WaitAsync`, never for a length of time; its 30-second limit applies to that one wait, and the runtime lives until the fixture is disposed. The runtime reads these from its time provider: the heartbeat interval, session renewal, the reconnect backoff, the update pause and its preparation limit, the catalog refresh interval, command expiry and download pacing. Pass a `ManualTimeProvider` and let a heartbeat interval pass with `fixture.HeartbeatAsync`, or a backoff with `fixture.BackoffPassAsync`. The WebSocket handshake timeout, the acknowledgement timeout and pipe command timeouts still use real time. To show that something did not happen, find an event that can only follow it: commands are handled in arrival order, claims are made one at a time, and frames on one socket arrive in the order they were sent. Where no such event exists (a claim wave or a synchronization pass that decides to do nothing leaves no trace), say so in the test and give the wrong action heartbeat rounds in which to show.

A test opens a store with `TestState.OpenStore`, the only place the suite constructs one, and deletes its folder with `JournalFiles.Delete`, which empties the SQLite connection pool of that journal only. Do not call `SqliteConnection.ClearAllPools()` in this suite: the classes run in parallel, and clearing every pool disposes a connection that a store in another test has just taken from its pool, which that test sees as `ObjectDisposedException: SQLitePCL.sqlite3`.

`AgentStore` opens its SQLite connection inside the journal's schema lock, and the open must stay there. Microsoft.Data.Sqlite 10.0.0 takes a pooled connection under the pool's lock but records which `SqliteConnection` owns it only afterwards (`SqliteConnectionFactory.GetConnection`, then `SqliteConnectionInternal.Activate`). A second open of the same journal that finds the pool empty in that gap takes the connection for abandoned (`SqliteConnectionPool.ReclaimLeakedConnections`) and is handed it too, so two stores share one native handle and one store's transaction can swallow or roll back the other's writes. Every pooled open of a journal goes through the store's constructor, so the lock keeps any opener out of that gap while another asks the pool. A test that needs SQL of its own against a journal takes its connection from `TestState.OpenJournal`, which opens it with pooling off: a pooled connection opened anywhere else shares the journal's pool without that lock and can fall into the same gap. `AgentStoreOpenRaceTests` fails when the open is moved out of the lock. Read those three library methods again before changing the package version or the pooling of the journal's connection.

## Local server and CLI

Use a disposable native QQQ host from the sibling qbit-printing project, using qbit-printing 1.0.0 on QQQ 4.1.0-RC.21 and advertising native printing API 2.3. Sign in through the companion's advertised QQQ authentication flow before using normal CLI commands. The service owns the session, device proof and unattended printing. A bearer-only demo is not a compatible realtime host.

```powershell
$agent = '.\src\Qqq.PrintAgent.Cli\bin\x64\Debug\net10.0-windows10.0.19041.0\qqq-print-agent.exe'
& $agent service-status
& $agent snapshot
& $agent printers
& $agent run
```

`run` requests a connection refresh through the authenticated service; the command line never claims or prints a job itself. Device setup and explicit printer exposure use that same service. Submit a physical test only when authorized, after approving the selected exposed queue in QQQ.

The command line has no state and no server transport of its own: every command is one request to the service. The `--legacy-compat` bearer diagnostics and their `--data-dir`, `--once` and `--service` options were removed in 2.0. The service holds an exclusive `worker.lock` in its data directory, so a second service host cannot run against the same journal; do not point a development service at installed state. `CommandLine` takes the console, the environment, the pipe and the helper entry points as injected functions, so its tests need none of them.

## Virtual file printer (macOS/Linux)

`tools/virtual-agent/` holds a dependency-free Node.js agent that speaks the realtime contract and
writes each verified job to a directory instead of a Windows queue. Use it to exercise a QQQ server
with the printing QBit without Windows or hardware; it does not qualify this service. See
[its README](../tools/virtual-agent/README.md).

## Reliability boundary

The worker persists receipt and SUBMITTING before invoking Windows. It records SPOOLED and the spooler job ID afterward and replays pending cloud events. A crash between spool acceptance and recording cannot be made atomic. The worker uses OUTCOME_UNKNOWN rather than automatic reprint. Preserve that distinction when extending retry/cancellation logic.

Core has one composition, the one the service builds, and no optional paths beside it:

- `PrintWorker` is one lane. Its log, its two predicates and every member of `PrintWorkerOptions` except the clock are required. It does not report unfinished jobs from a previous run, send inventory or send heartbeats; the runtime does.
- `IPrinterBackend` is `Discover` and `PrintFileAsync(job, path, submissionTimeout, token)`. A document is always a verified staged file. A new backend, a wrapper included, implements both.
- `PrintApiClient` is built only by `PrintApiClient.ForSession`. Inventory, job events and command receipts go over the realtime channel attached with `WithRealtime`, and fail as a closed connection without one.

Core tests of the worker build it through `WorkerHarness`, which composes it the same way over hand-written fakes: a fake server that answers claims and documents and acknowledges or refuses each event frame, a journal, a printer that takes staged files, and a connection that can be dropped and reopened. A test that needs an API client over its own fake server gets one from `FakeSessionClient.ApiOver`.

## Packaging

```powershell
./build/Build-Installer.ps1
```

MSI ProductVersion is numeric; assembly/package informational version may contain the prerelease suffix. Service, companion and CLI are published into separate self-contained runtime directories and shipped in one machine MSI. Do not reintroduce the template's per-user Velopack updater.

`./build/Package-Release.ps1` (the version defaults to `VersionPrefix` in `Directory.Build.props`) is the repository entry point for the unsigned local package; the signed stable package is built by the release workflow. It runs the same packaging checks as `.github/workflows/ci.yml` (`Test-WindowsSdkInventory.ps1 -VerifyPackages`, `Test-BrandedInstaller.ps1`, `Test-PackagingInputs.ps1`, `Build-Installer.ps1`) and assembles `artifacts/release` with the versioned MSI, `Install-Branded.ps1`, `branding.example.json` and `SHA256SUMS`. It does not build or run Core.Tests.

`Qqq.PrintAgent.NativeCI.slnx` is `Qqq.PrintAgent.slnx` without the `Core.Tests` project, for hosts that must not build that assembly (for example `dotnet build Qqq.PrintAgent.NativeCI.slnx --configuration Release '-p:Platform=x64' -warnaserror`, then `dotnet test tests/Qqq.PrintAgent.Cli.Tests`). `Qqq.PrintAgent.slnx` and the test projects are unchanged, so `Verify.ps1` still covers Core.Tests; run those tests on a clean CI runner.

## Machine update integration

`MainWindow.Updates` wires AppKit `UpdateCoordinator`/`ContentDialogPrompter` to Windows `MsiUpdateService : IUpdateService`. The persisted startup-check preference, version/channel display, splash duration and help/credits use the shared shell. The UI serializes manual/startup checks; confirmation is required before apply.

`GitHubMsiReleaseClient` reads the fixed QRun-IO release feed, applies strict version/channel/minimum policy and validates bounded release assets and SHA-256. `MsiUpdateInstaller` validates protected staging, ACLs and reparse boundaries, copies the installed self-contained CLI for handoff, rechecks the MSI and uses Windows Installer for all components. The runtime's `prepare-update`/`resume-after-update` operations coordinate a bounded claim pause while preserving heartbeats: `prepare-update` stops new claims, answers "Ready for update." once every job already claimed has finished, answers that it is not ready when that takes more than 25 seconds, and answers that preparation was interrupted when printing was resumed or a sign-in, sign-out or enrollment began while it waited; the pause lifts by itself after 10 minutes. No server-supplied executable, per-user replacement package or persistent privileged updater is involved. See the [threat model](security/threat-model.md) for trust and failure boundaries.

Historical 0.1.3-rc.1 verification: locked restore, zero-warning Release build, **36 Core + 127 Windows/CLI tests (163 total)**. Synthetic release/download/hash/request/ACL/version/maintenance tests do not install an MSI. Installed shell and actual upgrade acceptance remain separate gates; use [session state](SESSION-STATE.md) for current verification. Completed protected update stages are retained for manual administrator review.
