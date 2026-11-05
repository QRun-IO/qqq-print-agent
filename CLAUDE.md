# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Policy lives in [AGENTS.md](AGENTS.md) (repository rules: hosting split, authorization, logging, printing) and [CONTRIBUTING.md](CONTRIBUTING.md) (conventions, script quality). Read both; this file adds the commands and architecture they leave implicit.

## Prerequisites

- Windows x64, PowerShell 7.4+, .NET SDK pinned by `global.json` (10.0.x).
- `qqq-print-protocol` checked out as a **sibling directory** at the SHA pinned in `.github/workflows/ci.yml`. Only `Core.Tests` consumes it (it links `../qqq-print-protocol/fixtures/*.json` at build time; override with `-p:PrintingProtocolFixtures=<dir>` or `Verify.ps1 -ProtocolFixtures`), so the NativeCI solution builds without it.
- Scripts tooling: `uv sync --locked` and `npm ci --ignore-scripts` (Node 24+).

## Commands

**Host restriction first.** `Qqq.PrintAgent.Core.Tests` is hosted-CI-only. On this development PC, Windows Defender quarantines the Core test assembly, so that project must not be built or run locally (see `docs/SESSION-STATE.md`; do not add exclusions or restore quarantine to work around it). Local work uses `Qqq.PrintAgent.NativeCI.slnx`, which is the full solution minus `Core.Tests`. Core test evidence comes from the hosted Windows CI run on the pushed commit.

Local default (locked restore, Release x64 build with `-warnaserror`, Windows/CLI tests), always with `-p:Platform=x64` because the solution maps test projects to AnyCPU and a direct `dotnet test` without it picks stale output. Never pass `--no-build` to `dotnet test`: the solution build writes the test DLL to the AnyCPU folder, so an x64 `--no-build` run executes whatever old x64 DLL is lying around and reports a pass against stale code.

```powershell
dotnet restore Qqq.PrintAgent.NativeCI.slnx -p:Platform=x64 --locked-mode
dotnet build Qqq.PrintAgent.NativeCI.slnx -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet test tests/Qqq.PrintAgent.Cli.Tests -c Release -p:Platform=x64 --no-restore
```

Single test (xunit):

```powershell
dotnet test tests/Qqq.PrintAgent.Cli.Tests -p:Platform=x64 --filter "FullyQualifiedName~AgentPipeSecurityTests"
```

Full verification, **only on a permitted host or hosted CI** (adds Core.Tests, coverage and TRX output under `TestResults/verify-<guid>`; requires the sibling protocol checkout):

```powershell
./build/Verify.ps1
```

Packaging (unsigned self-contained x64 MSI; never installs or prints):

```powershell
./build/Build-Installer.ps1 -ProductVersion 1.0.1 -ReleaseVersion 1.0.1
./build/Package-Release.ps1            # runs the CI packaging checks and assembles artifacts/release
```

Script quality gates (CI runs the same):

```powershell
./build/Analyze-Scripts.ps1            # PSScriptAnalyzer 1.25 over build/*.ps1, one rule at a time
uv run --locked --no-sync python .github/scripts/test-script-quality.py
uv run --locked --no-sync python .github/scripts/check-script-quality.py
python .github/scripts/check-source-headers.py   # owned-file copyright/SPDX notices
npm run typecheck; npm run lint; npm test        # tools/virtual-agent (Node, ESLint Allman)
```

Interactive-only suites (not run by hosted CI):

- **UI tests**: elevated interactive desktop; build the companion, optionally set `K22_UITEST_EXE`, then `dotnet test tests/Qqq.PrintAgent.UiTests -p:Platform=x64`.
- **Layout tests**: `tests/Qqq.PrintAgent.LayoutTests/Run.ps1 -EvidenceDirectory <new dir>`; synthetic service only, not in the solution file.

Pre-commit hooks (`pre-commit install --hook-type pre-commit --hook-type commit-msg`) enforce Conventional Commit types, DCO sign-off (`git commit -s`), gitleaks, and no direct commits to `main`.

## Architecture

Five projects, strict layering (Core → Windows → Service/UI/CLI). Business logic never lives above Core; OS calls never live in Core.

| Project | Owns | Key entry points |
| --- | --- | --- |
| `Qqq.PrintAgent.Core` | QQQ session auth, HTTP/WSS clients, realtime settings/policy, document streaming, job state machine | `Authentication/QqqSessionClient*`, `Printing/PrintApiClient*`, `Printing/PrintWorker`, `Printing/PrintingRealtimeChannel` |
| `Qqq.PrintAgent.Windows` | Spooler/PDF/ZPL backends, SQLite + DPAPI store, named-pipe IPC, the long-running runtime | `AgentRuntime.*.cs` (partial class split by concern), `AgentPipe*`, `AgentStore` |
| `Qqq.PrintAgent.Service` | Windows Service host only | `PrintService.cs` |
| `Qqq.PrintAgent` | WinUI/AppKit companion: tray, Printers/Jobs/Logs/Settings views, MSI update UX | `MainWindow.*.cs` (partial class split by concern) |
| `Qqq.PrintAgent.Cli` | Authenticated service management over the pipe; isolated legacy diagnostics | `Program.cs` |

Large classes are deliberately split into partial files by concern (`AgentRuntime.Realtime.cs`, `AgentRuntime.Jobs.cs`, `MainWindow.Updates.cs`, ...). Add new behavior to the matching partial rather than growing `AgentRuntime.cs` / `MainWindow.xaml.cs`.

**Runtime model.** The service is the only process holding credentials, the journal and printer access. It opens an outbound authenticated WebSocket (`/qqq/v1/qbit/printing/device/realtime`, subprotocol `qqq-printing.v1`) for commands, status and job notifications; documents stream separately over HTTPS into a protected staging directory with length + SHA-256 verification. There is intentionally **no polling fallback**. The companion and CLI talk to the service only through the `QqqPrintAgent` named pipe (administrator clients) and receive credential-free snapshots. Printing runs in an isolated child process with a deadline. See `docs/REALTIME.md` for the worker split, bounds and reconnect rules.

**Reliability boundary.** `PrintWorker` persists receipt and `SUBMITTING` before calling Windows, records `SPOOLED` plus the spooler job ID afterward, and replays pending cloud events. A crash between spool acceptance and recording is not atomic; the worker reports `OUTCOME_UNKNOWN` rather than auto-reprinting. Preserve this distinction in any retry/cancel change, and keep "spool accepted" and "paper came out" separate in docs and messages.

**Versions.** Product version (`VersionPrefix` in `Directory.Build.props`, release workflow overrides with `-p:Version`), native printing API (2.3), wire payload (v1) and the WebSocket subprotocol are independent. Do not conflate them when bumping.

**Updates.** `MsiUpdateService` + `GitHubMsiReleaseClient` read the GitHub release feed and apply a machine MSI through Windows Installer, updating service, companion and CLI together. That GitHub feed is why the GitHub shadow must stay even though Gitea is primary.

## Test layout

- `tests/Qqq.PrintAgent.Core.Tests`: protocol fixtures (`ProtocolFixtureTests`), loopback WebSocket peer, worker state machine, media contracts. Hosted CI only on this PC (see Commands); new Core tests are written here and validated by the CI run.
- `tests/Qqq.PrintAgent.Cli.Tests`: despite the name, this is the Windows runtime suite (`AgentRuntime*`, `AgentRealtime*`, `AgentPipe*`, `AgentStore*`). Put Windows-layer tests here.
- `tests/Qqq.PrintAgent.WorkerFixture`: child-process fixture used by runtime tests.

## Conventions worth knowing

- `.editorconfig`: 3-space indentation, Allman braces, everywhere (C#, PowerShell, scripts). Analyzers run in build; zero warnings is enforced by `TreatWarningsAsErrors`.
- Every owned source file starts with the `Copyright (c) 2026 James Maes` / `SPDX-License-Identifier: MIT` header; `check-source-headers.py` fails otherwise. `tools/virtual-agent` keeps its QRun.IO attribution.
- Package versions are central (`Directory.Packages.props`); after changing them, regenerate each affected `packages.lock.json` (`dotnet restore --force-evaluate`).
- Coverage is reported, not gated, per `docs/adr/0002-prerelease-quality-and-packaging.md`.
- `docs/SESSION-STATE.md` records the current release/verification state; consult it before claiming anything is verified.
