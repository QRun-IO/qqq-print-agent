# Changelog

## 0.4.0-rc.4 — unsigned prerelease candidate

Release identity only: rc.4 is strictly newer than the local, unpublished 0.4.0-rc.3
build and ships the same cohort, so the updater accepts it over an installed rc.3. The
MSI ProductVersion and `VersionPrefix` stay 0.4.0; `VersionSuffix` and the packaging
defaults move to `rc.4`, and the ordered numeric file version becomes `0.4.0.4`. There is
no code, test or dependency change. Everything described under rc.3 below (media
selection, 4 × 2 and 4 × 1 test labels, phase-timing log, ordered file version) applies.
The rc.4 head is version metadata and docs on top of pre-release product source
`5c2bfc9`, whose full hosted CI run 41381287355 passed 499 tests (166 Core + 333 CLI) with
zero warnings and errors. rc.4 itself is not yet tested or published; its tag pipeline
must pass all gates afresh before publication. Earlier operator acceptance covers 4 × 6
and Letter output, and the 4 × 1 layout was confirmed on the local rc.3 build of the same
product code; 4 × 2 is deferred. The MSI is unsigned and no GA or 1.0 readiness is
claimed.

## 0.4.0-rc.3 — local candidate (superseded by rc.4)

Release identity: this is a strictly newer semantic version than 0.4.0-rc.1 and
0.4.0-rc.2 with the same numeric core, so the MSI ProductVersion (0.4.0) and
`VersionPrefix` are unchanged, `VersionSuffix` and the packaging defaults move to `rc.3`,
and the updater (which needs a strictly newer version) and the installer's same-version
upgrade support both accept it. The rc.2 MSI was installed, but Windows Installer skipped
its executables because rc.1 and rc.2 shared the numeric file version 0.4.0.0 ("existing
file is of an equal version"). Packaging now stamps each executable with an ordered
numeric file version: `X.Y.Z-rc.N` becomes `X.Y.Z.N` (N 1..65534) and stable `X.Y.Z`
becomes `X.Y.Z.65535`, and any other prerelease label is refused. Future upgrades replace
files automatically. No behavior, dependency or lock change otherwise. The protocol
package `0.4.0rc1` is an independent wire-compatibility version and is unchanged. Older
notes below that name 0.4.0-rc.1 or rc.2 describe the same cohort.

This cohort goes beyond the older installed CI 0.4.0-rc.1 build. The rc.3 candidate
was built and installed locally with a normal MSI upgrade (exit 0), matching all three
components; no repair was needed. It is not publicly released or tagged. Hosted Windows
CI and the update-feed handoff await the migration owner.
Upgrade the server first: selected-media jobs require
printing extension 2.3.0 (wire payload v1 unchanged). Against a 2.0, 2.1 or 2.2
(rendering) server the client keeps its three-capability HELLO and offers no media
profiles, so existing behavior is unchanged.

- Add 4 × 1 and 4 × 2 in test labels (ZPL at 203/300/600 dpi, one PDF choice each) to the existing Print test choices; the 4 × 6 choices and defaults are unchanged. These PDFs carry explicit actual-size job paper (101.6 × 25.4 / 50.8 mm) applied to a job-local copy of the queue defaults (so its Direct Thermal and other settings are kept) and checked against the device context before StartDoc. Needs the matching unreleased 0.4 server; older clients reject job paper. Load the correct stock first; nothing prints until the operator confirms.
- Report each queue's machine-default paper dimensions and resolution as an opaque, revision-bound media profile, and advertise `printer-media-v1` only when the discovered server supports it. Defaults are configured facts, not physical stock sensing.
- Refuse a selected-media job before spooling when the profile is unknown or its revision has drifted, and again at the native submission boundary. A proven pre-submit refusal is recorded as FAILED and is never treated as an uncertain outcome.
- Normalize job-only driver copies and scale for selected PDFs and verify the effective device-context dimensions and resolution before StartDoc. ZPL (RAW) only rechecks the profile immediately before StartDocPrinter.
- Log one phase-timing line per processed job (inventory, claim and receipt, download, revalidation, submit gate, native submit) with the job identifier only, to attribute print latency from the service log. No new request, payload or printer access.
- Pin the printing protocol to the final reviewed contract, commit `42c0d249f81395f00408ec0f92638dcbbf5deb03` ([Gitea PR 2](https://git.kof22.com/QRun-IO/qqq-print-protocol/pulls/2)), in `.github/workflows/ci.yml`; native printing stays 2.3 with wire payload v1. The earlier pin `a26c18c9…` is historical.
- Split `PrintWorker` job processing into named submission, revalidation, dispatch-gate and native-submit steps (analyzer MA0051 rejected the first form of the timing instrumentation). Behavior, state order and lease release are unchanged.
- Document every type, method, constructor and non-auto property with how/why headers and apply the `.editorconfig` structural spacing. Each batch was token-compared as comment and blank-line only.

Evidence: this cohort includes changes beyond the installed CI 0.4 build, and the
latest release head is neither installed nor tagged. On the merged integration tree the
local gate passed: the production UI build under strict XML and analyzers with zero
warnings, and the 35 existing paper/media tests; the server's strict full verify (R31)
also passed. Core.Tests is not built locally
because of a Defender quarantine and is verified only on hosted CI. The prior protected
base `0336d5f` had 477 hosted tests; this source head still needs a normal Gitea
Core, CLI and build run. The earlier PR-head result (35ed93c) is historical. The
existing Auth/S3 qualification is accepted and no new desktop or provider proof is
claimed. Still pending: hosted CI, installation of the new label implementation, and
a small-stock paper test. No 1.0 claim.

## 0.3.2-rc.1 — 2026-11-01

- Verify that the app, service and CLI all have the requested release and identical full build versions before recording MSI update success. Report missing, unreadable or mixed builds without claiming rollback.
- Correct the Windows App SDK credit to reference its Microsoft terms and installed third-party notices.
- Give the isolated native test host the same PerMonitorV2 DPI declaration as production while retaining its unelevated execution level.

The updater regressions first failed against the earlier implementation, then
passed with the correction. The integrated client gate passed 426 tests with
zero warnings, errors or skips. Actual in-app update handoff and the historical
layout-cycle cause remain separate acceptance items.

## 0.3.1-rc.1 — 2026-11-01

- Show explicit WebSocket connection state and server authority in the footer, with live upload/download rates and service-lifetime application-payload totals. A lightweight local observation refresh adds no QQQ job/control polling.
- Recognize Windows-normalized startup account names only when they resolve to the exact expected SID; preserve the remaining task ownership checks. Rename the tray startup section to distinguish it from the automatic print service.
- Leave the exact fatal native `LayoutCycleException` unhandled after AppKit logs it, allowing normal WinUI termination instead of retaining a corrupted companion window. The service continues; the operator can reopen the companion. This is failure-handling mitigation, not a fix for the original layout trigger.
- Add an isolated native WinUI regression host using actual dialog/action/refresh code and synthetic IPC, with no printing or installed-state changes.

The installed 0.3.0 companion's post-print LayoutCycleException remains under
investigation. Nonprinting geometry and modal-refresh scenarios have not
reproduced it; these changes do not yet establish a layout-cycle fix.

## 0.3.0-rc.1 — 2026-10-31

Upgrade the server before this client: native printing API 2.0 requires a
synchronized authenticated WebSocket, with no job/control polling fallback.
Installed and physical-output acceptance remain separate from source/test gates.

- Require the server's printing 2.x extension and authenticated persistent realtime channel. Replace periodic job/control HTTP polling with pushed state, commands, settings and job notifications.
- Stream verified documents into protected staging files, with configurable aggregate bandwidth, storage, download concurrency, per-printer ordered submission and native submission deadlines.
- Preserve durable receipts and command outcomes across reconnects; keep control traffic responsive during document transfers.
- Expand job details and actions directly beneath the selected row, with consistent sortable table headers and bounded history paging.

## 0.2.0-rc.1 — 2026-10-31

Release preparation only; publication and installed qualification remain pending.

Upgrade the server first: the new client requires the semantic printing extension
and standard QQQ sign-in; older bearer-only servers need an upgrade. Existing
pairings and history are preserved, but unbound devices need explicit account association.

- Discover QQQ's semantic printing/authentication capabilities and sign in through
  standard table-based or explicitly advertised native browser authentication.
  Protect the service session with DPAPI; resume through QQQ and preserve pairing
  and journals when authorization is lost.
- Add independent device-control polling, durable desired state/command receipts
  and exact owned spool cancellation; disconnect keeps the control path available.
- Route normal CLI operations through the authenticated service; direct legacy
  diagnostics require an explicit compatibility flag.
- Add installer-owned browser callback forwarding, cancellable sign-in and
  customer-branded callback text. Installed callback/provider acceptance remains
  unqualified; no signing or stable 1.0 readiness is claimed.
- Support explicit replacement pairing after revocation and safe recovery from
  damaged saved credentials. Pending setup survives restart without presenting
  old device proof or enabling printing; previous history and choices remain intact.
- Pass 365 tests and build WinUI/service/CLI with zero warnings/errors/skips.

## 0.1.9-rc.1 — 2026-10-31

- Keep management responses available until the client closes its one-command connection or the existing deadline expires. This prevents Windows from discarding unread success and error responses during server disconnect.
- Preserve current and older newline-framed clients without a new acknowledgement protocol or a blocking pipe drain. Cancellation and service shutdown remain bounded.
- Reproduce the failure through the real runtime with delayed readers; verify successful responses, error responses, next-client reuse, deadlines and shutdown. All eight reproduction cases and 255 combined regression tests pass; the solution/UI build has zero warnings/errors.
- Complete the state-preserving CI-package upgrade and all 16 installed UI checks, including authenticated management and history queries. No physical print was requested by these checks.
- Publish the corrective prerelease through [release workflow 41190815995](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41190815995); verify all four release assets and provenance attestations. The unsigned published MSI and the installed CI MSI are distinct builds; [acceptance notes](docs/ACCEPTANCE.md) record their hashes and scope.

## 0.1.8-rc.1 — audit candidate, superseded before publication

- Bound and cancel local management frames and server responses; apply a deadline to streamed document bodies.
- Drain successful queued jobs without an extra idle delay while retaining independent heartbeats and failure backoff.
- Preserve valid long job/printer names, close failed SQLite initialization handles and guard late UI completions after exit.
- Carry explicit operator duplicate-risk consent for failed jobs that may already have reached a printer.
- Require release security/CodeQL/script gates and verified artifacts before publication; add build provenance attestations.
- Package from fresh output directories with locked restores and matching source, MSI and component versions.
- Authenticate the installed service before sending local commands, retain pipe ownership, and reject unsafe machine-state owners, access grants or links before privileged use.
- Protect sign-in task definitions through registration and validate machine state before an upgrade removes the prior product.
- Fresh regression gate: 249 tests, zero build warnings/errors. Installed audit acceptance and hosted provenance verification were pending at this superseded candidate checkpoint; the completed 0.1.9 checks are recorded above.
- The local candidate installed successfully with MSI exit 0 and preserved credentials/journal, but a subsequent management-history check exposed the response-disconnect race corrected in 0.1.9. Service heartbeat and printer readiness were confirmed independently through QQQ.

## 0.1.7-rc.1 — 2026-10-30

- Built-in QQQ-branded warehouse diagnostics use the server's bundled legible font across PDF and ZPL.
- Native test controls offer Letter, A4 and 4 × 6 labels with explicit 203/300/600-dpi ZPL choices.
- Prefer ZPL for label-capable queues and suggest resolution from an explicit driver hint; operators verify stock and resolution before printing.
- Existing API callers keep compatible defaults when testProfile is omitted.

## 0.1.6-rc.1 — 2026-10-30

- Expand selected printer information within its row, with actions first, labeled fields, a bounded description preview and expandable Windows diagnostics.
- Open printer settings and job details by double-click, while preserving checkbox/button actions and text selection. Equivalent Settings and Details buttons remain available.
- Use wider, window-responsive native dialogs for profile editing and structured job summary/event history.
- Add sortable headers with printer Name ascending and job Created descending defaults. Creation time is preserved independently of status updates; missing historical creation times remain explicit.
- Apply job ordering across the matching local history and bounded cloud queue preview before paging, using displayed labels and stable identifier tie-breaks. Sort indicators update only after a successful query.
- Installed **0.1.6, source d9033ae**, completed with exit 0 and no reboot, retaining credentials, four printers and two jobs. The source gate passed **211 tests (42 Core + 169 Windows/CLI)** with zero warnings/errors; [exact-source CI 41131558158](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41131558158) succeeded. Management, profile, logs, pagination and branding checks passed. All **15 installed UI checks have passing results across fourteen plus one runs**: the first run passed 14/15, then a test-only arbitrary-text double-click selector was corrected and the focused remaining check passed.
- The service was later observed Stopped with exit 0 and no matching crash evidence; the cause remains unknown. A normal UAC-authorized restart succeeded and the final check found it Running, with cloud connectivity offline. No new physical print or separate visual inspection was performed. These results do not establish every Narrator, keyboard, resize, long-text, scaling or high-contrast scenario.

## 0.1.5-rc.1 — 2026-10-30

Installed acceptance subsequently passed 12 interactive checks across a ten-test run and a focused two-test run. This evidence applies to 0.1.5, not the 0.1.6 changes above.

- Compact printer/job tables, stable row selection, full-text details and bounded 50-row job pages with prefix search.
- Client-managed computer/printer names, descriptions and locations synchronized to QQQ without changing physical queue identities.
- Indexed history queries, 200-event recovery batches and bounded detail maintenance; synthetic 10,000/30,000-job regression coverage.
- Shared AppKit daily diagnostic logs with configurable retention/size, visible cap state and explicit safe clearing.
- Same-MSI customer branding for the companion, installed product/service labels and Start menu shortcut; optional customer PNG/ICO artwork.
- User confirmed the 0.1.4 tray icon works. No physical print was sent for this scope.

## 0.1.4-rc.1 — 2026-10-30

- Restore the native File and Help menu bar, including Settings, Close, Exit, activity logs, help links, Check for Updates and About. Update commands share the existing AppKit coordinator and busy state.
- Keep ownership of cached tray images when sending their handles to Windows. H.NotifyIcon disposes objects assigned to its Icon property; reusing those cached objects broke returning status transitions. Failed icon updates now retry on the next refresh.
- Native WinUI diagnostic reproduced the disposed-icon failure and passed after the ownership fix, including Windows registration. Actual installed tray visibility remains a separate acceptance check.

## 0.1.3-rc.1 — 2026-10-30

- Complete shared AppKit settings: manual and optional startup update checks, splash duration, version/channel, help and release links, and dependency credits.
- Use AppKit's update coordinator with a protected machine MSI adapter so the service, desktop app and CLI upgrade together after confirmation.
- Verify release eligibility, download size and SHA-256; finish active submission before pausing claims, preserve enrollment/history, and record installer outcomes for diagnostics.
- Keep update prompts within the window lifetime and wait for existing modal dialogs.
- Retain the 0.1.2 startup correction. Locked build and 163 Core/Windows tests pass; installed UI and real updater acceptance are recorded separately in docs/APPKIT-PARITY.md.

## 0.1.2-rc.1 — 2026-10-30

- Fix installed companion startup: assign the built-in navigation Settings identifier after its template loads, instead of passing an uncreated item to WinUI.
- Preserve the original launched process in desktop tests so native startup failures report the actual exit code.
- The 0.1.1 MSI upgrade retained credentials and restarted the service, but installed UI testing found this startup crash; 0.1.1 was not published as a replacement release.
- Always rebuild the WiX package so changes to the version or payload directory cannot reuse stale incremental output.

## 0.1.1-rc.1 — 2026-10-29

- Native Windows navigation, stable printer rows, searchable job identity/history, confirmed test/reprint/cancel actions and persistent failure review.
- One shared AppKit activity log, synchronous tray icons, single-instance restoration, reconnect and explicit credential recovery.
- Service start/restart controls, opt-in installed sign-in startup, bounded SCM recovery and narrowly scoped full-uninstall task cleanup.
- Isolated native printer workers with timeout/orphan cleanup; interactive save-to-file queues excluded from unattended printing.
- Connection-scoped outbox, interrupted job recovery, safe event-detail retention and per-user CLI/service identity separation.
- Automated verification: 102 Windows tests, zero compiler/analyzer warnings. Installed acceptance is recorded separately.

- Fix desktop startup by including the generated WinUI resource index and compiled window markup in the published app and MSI.
- Fail installer packaging when required app resources are missing or empty.
- Supersedes 0.1.0-rc.1, whose desktop companion failed before showing its window. The initial build and unit tests did not detect this packaging defect.

## 0.1.0-rc.1 — 2026-10-29

Initial public prerelease for QQQ 4.1+.

### Added

- Windows AppKit/WinUI companion and background Windows service installed together by an x64 MSI.
- Single-use enrollment, protected service credentials and outbound HTTPS polling using printing protocol v1.
- Windows queue discovery with persistent local printer exposure choices.
- System-tray health and a details surface for connection, printer status, jobs and recent service logs.
- Runtime customer branding, persistent connection footer and native connection/service-log dialogs.
- PDF rendering through the Windows PDF/GDI path; RAW ZPL submission for Zebra-compatible queues.
- Local SQLite receipt/event journal, payload integrity checks and duplicate/uncertain-submission handling.
- CLI enrollment, printer selection, status/snapshot and foreground diagnostic worker.
- Shared Java/.NET contract fixtures, unit/Windows persistence tests, Windows CI and unsigned prerelease MSI automation.

### Validation and limits

Physical laser PDF output was confirmed locally, including the installed service path. Zebra hardware qualification remains pending. This release is unsigned and uses polling; it does not claim physical completion from a spool acknowledgment. Custom PDF media, automatic cancellation/reprint flows, retention, renderer isolation and production signing remain follow-up work. See docs/ACCEPTANCE.md.
