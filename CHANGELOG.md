# Changelog

## 0.1.1-rc.1 — 2026-10-29

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
