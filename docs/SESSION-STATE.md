# Session state — 2026-10-30

Installed target: **0.1.3-rc.1**, including the startup correction, functional audit fixes and shared AppKit parity. Source commit **7344914** passed **36 Core + 127 Windows/CLI tests (163 total)**, locked restore and a zero-warning/error Release build. GitHub CI for that exact commit also passed, including MSI packaging. User renewed installation authorization; Windows elevation succeeded and MSI returned 0, with no reboot required. Enrollment credential bytes were preserved, service restart and management pipe passed, and four printer queues were returned. The initial cloud-connected snapshot was false; a live QQQ connection is not newly qualified. Root owns remaining acceptance and publication.

Final local installer: `artifacts/installer/QqqPrintAgent-0.1.3-x64.msi`, SHA-256 `808869F507D0B87E67546F41940A4A25D059E89E655E9127635299D025EBF737`. Private integration evidence is in `runs/audit-upgrade-013/result.json` and `ui-results`.

Installed desktop tests completed **5 passed / 5 failed**. Preferences/help, theme persistence, connection validation, navigation/footer and job search/service controls passed. Tray hide/relaunch, log-content identifier, splash and About checks failed; the log identifier is absent in pinned AppKit 0.1.0, hidden-window UIA needs native visibility checks, and About encountered Windows SendInput access denied. These failures are not a passing UI acceptance gate. The companion was opened after the tests and the service remains running. Computer-use capture reported a higher-integrity limitation, so it did not establish visual acceptance. Test-only corrections now compile with zero warnings/errors: HWND visibility/ownership, pinned AppKit log controls, UIA navigation selection and a longer isolated splash observation window. They have not been rerun. No new physical print or actual in-app update was attempted.

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

The corrective 0.1.2 source passed 102 tests and built an unsigned MSI; its UAC installation was canceled. That left0.1.1 installed until the successful0.1.3 upgrade recorded above; the0.1.1 result is historical and is not working companion acceptance. The 0.1.3 target includes the correction and additional update/parity work. Inspect current local processes/service state before further actions; do not infer installed versions from source or old artifact hashes.

Earlier development and temporary installed-service laser pages were confirmed by the user. No fresh physical print has been performed for the latest corrections. Zebra is deferred. Reboot/sign-in/recovery, real update cancellation/relaunch and publisher signing remain pending. Do not report a canceled/pending elevation as a completed upgrade.

QQQ `qbit-printing` 0.1.1-rc.1 verification passed **19 tests with zero Checkstyle violations**, including native record/table job controls. Synthetic Windows update/native-child tests do not invoke an installer or access physical printers.
