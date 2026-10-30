# Changelog

## 0.1.6-rc.1 — source changes, validation pending

- Expand selected printer information within its row, with actions first, labeled fields, a bounded description preview and expandable Windows diagnostics.
- Open printer settings and job details by double-click, while preserving checkbox/button actions and text selection. Equivalent Settings and Details buttons remain available.
- Use wider, window-responsive native dialogs for profile editing and structured job summary/event history.
- Add sortable headers with printer Name ascending and job Created descending defaults. Creation time is preserved independently of status updates; missing historical creation times remain explicit.
- Apply job ordering across the matching local history and bounded cloud queue preview before paging, using displayed labels and stable identifier tie-breaks. Sort indicators update only after a successful query.
- Source verification and installed 0.1.6 acceptance are pending; no physical print is claimed for this UI work.

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
