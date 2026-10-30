# Session state — 2026-10-30

Current target: **0.1.3-rc.1**, including the startup correction, functional audit fixes and shared AppKit parity. The full source gate passed **36 Core + 127 Windows/CLI tests (163 total)**, locked restore and a zero-warning/error Release build. UI tests compile only. The new unsigned MSI is being packaged; installed startup/UI and real in-app upgrade acceptance remain pending. Root owns packaging, installation, hardware tests and publication.

## Implemented source

- AppKit `UpdateCoordinator` and native prompts use `MsiUpdateService` for manual checks and the persisted startup-check preference. Version/channel, splash duration, theme, project/help/release/support links and dependency credits are present.
- The fixed QRun-IO GitHub feed permits newer stable versions for stable builds and newer RC/stable versions for prerelease builds, with a minimum core version of 0.1.3. Same-release SHA-256 checks establish consistency, not publisher signing.
- Protected ProgramData staging, path/reparse/ACL checks and a copied self-contained CLI helper apply one MSI to service, companion and CLI with administrator rights and explicit confirmation. No privileged persistent updater or UAC bypass is added. Claim pause preserves heartbeats; results/install logs persist and relaunch is attempted. Completed stages remain for administrator review.
- Native Printers/Jobs/Settings navigation, one shared persistent redacted AppKit log viewer, status tray, single-instance activation, close-to-tray and explicit exit.
- Service controls/install guidance, explicit administrator opt-in sign-in task, bounded SCM recovery, reconnect and timeout/pipe recovery. Independent heartbeats continue during slow printing; stale/EOF snapshots do not imply healthy service state.
- Connection-scoped credentials/outbox/history, restart recovery without duplicate printing and explicit unreadable-credential repair preserving encrypted backup/evidence.
- Friendly job identity, searchable/filterable history, event details, persistent failure review and safe detail retention preserving deduplication summaries. Server-mediated test/retry/cancel actions include cloud-queued jobs before local receipt and identity-checked spooler cancellation.
- Bounded isolated native helpers and orphan cleanup, supported-queue filtering, correct health flags, foreground CLI identity/locks and runtime branding contrast validation.

See [APPKIT-PARITY.md](APPKIT-PARITY.md), [FUNCTIONAL-AUDIT.md](FUNCTIONAL-AUDIT.md), [UX-REVIEW.md](UX-REVIEW.md) and [TODO.md](TODO.md) for source evidence and acceptance scope.

## Historical evidence and installed state

Public v0.1.0-rc.1 has a known WinUI resource packaging defect and warning. The later 0.1.1 MSI installed successfully, retained the exact encrypted credential bytes and restarted the service, but installed desktop tests exposed a native startup failure: NavigationView SettingsItem was accessed before its template created it. The correction defers that access until Navigation.Loaded and guards it. FlaUI launch handling now retains the process handle for native exit diagnostics.

The corrective 0.1.2 source passed 102 tests and built an unsigned MSI; its UAC installation was canceled. Installed 0.1.1 therefore remains the last reported installation and is not a working companion acceptance result. The 0.1.3 target includes the correction and additional update/parity work. Inspect current local processes/service state before further actions; do not infer installed versions from source or old artifact hashes.

Earlier development and temporary installed-service laser pages were confirmed by the user. No fresh physical print has been performed for the latest corrections. Zebra is deferred. Reboot/sign-in/recovery, real update cancellation/relaunch and publisher signing remain pending. Do not report a canceled/pending elevation as a completed upgrade.

QQQ `qbit-printing` 0.1.1-rc.1 verification passed **19 tests with zero Checkstyle violations**, including native record/table job controls. Synthetic Windows update/native-child tests do not invoke an installer or access physical printers.
