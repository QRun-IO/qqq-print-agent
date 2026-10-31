# Changelog

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
