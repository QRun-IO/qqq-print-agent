# Installation checkpoint — 2026-10-30

User approved Windows elevation. The 0.1.1 MSI installed successfully, preserved the exact encrypted credential bytes, and restarted the service. Nine installed desktop tests then exposed a startup access violation in MainWindow: the built-in NavigationView SettingsItem was passed to AutomationProperties before its template created it. Windows Application events identify that native call; this is a product startup defect, not merely a test failure.

The correction assigns the identifier only after Navigation.Loaded and guards the item. FlaUI test launch detection now preserves the original process handle so startup failures report the native exit code. The corrected 0.1.2-rc.1 tree passed 102 tests and a zero-warning Release build; corrective MSI packaging and installed desktop verification are in progress. Do not publish the 0.1.1 package as a working companion release.

Auto-update is not enabled. GCLO/MarkdownViewr use Velopack; this machine-service variant follows AppKit ADR-0002 and uses a coherent MSI for service, companion and CLI. No update check/download/install UI is present. User asked whether it exists; no separate updater implementation has been requested or claimed.
# Session state — 2026-10-29

Current task: finish the full functional audit corrections and qualify the replacement **0.1.1-rc.1** Windows package. Source corrections are implemented; installed acceptance remains pending. See [FUNCTIONAL-AUDIT.md](FUNCTIONAL-AUDIT.md) and [UX-REVIEW.md](UX-REVIEW.md) for evidence and acceptance scope.

## Implemented source

- Native Printers/Jobs/Settings navigation, one shared AppKit log viewer, persistent redacted service/companion logs, status tray, single-instance activation, close-to-tray and explicit exit.
- Service start/restart/install guidance; explicit administrator opt-in sign-in task with a fixed installed executable; bounded MSI-authored SCM recovery.
- Reconnect and timeout/pipe recovery; independent heartbeat during slow printing so real cloud contact stays fresh; stale snapshots and EOF/disconnected pipe responses no longer imply healthy service state.
- Scoped credentials/outbox/history across re-enrollment; restart recovery without duplicate printing; explicit unreadable-credential repair preserving encrypted backup and evidence.
- Friendly job/printer identity, searchable/filterable history and event details, persistent failure review, safe terminal/delivered/reviewed detail retention with deduplication summaries retained.
- Server-mediated test print/retry and queued cancellation, identity-checked spooler cancellation and explicit duplicate-risk warnings. Cloud-queued jobs are visible and cancellable before local receipt.
- Isolated native discovery/print/cancel helpers with bounded timeouts and orphan cleanup; interactive/file-output queues excluded; correct printer health flags and discovery failure handling.
- Foreground CLI per-user state and worker locks protect service identity; request timeouts return failure. Runtime branding validates white-text accent contrast and retains theme-aware content headings.

## Verification and package state

Final Windows verification passed **36 Core + 66 Windows/CLI tests (102 total)** with zero build warnings/errors, including independent heartbeats during blocked submission and cloud-queued cancellation before local receipt. Synthetic child timeout, process termination/orphan cleanup, job/store and pipe tests do not access physical printers. UI tests compile; their compilation is not installed UI acceptance.

QQQ `qbit-printing` **0.1.1-rc.1** verification passed **19 tests with zero Checkstyle violations**, including native record/table job controls. Root owns final integration evidence and publication.

The final unsigned MSI was rebuilt from the verified sources with zero warnings/errors. SHA-256: `AC4BD6B8AA3D563BF3E6F09CC292E6E571C167C6D479287F7F8F25859D6BAB29`. Fresh staging avoided file locks held by older running apps. Actual elevated upgrade and interactive UI acceptance are **waiting for the user's UAC approval**. Do not bypass elevation or report an install/upgrade as completed while that prompt is pending.

No fresh physical print has been performed for these latest corrections. Earlier development and temporary installed-service laser pages were confirmed by the user, and remain evidence only for those builds. Zebra remains deferred. Final reboot/sign-in/recovery acceptance and publisher signing remain pending.

Public **v0.1.0-rc.1** has a known broken WinUI resource package and carries a warning. Main contains the resource packaging fix; the replacement **0.1.1-rc.1** is not yet qualified/released. Inspect current local processes/service state before continuing; older statements that the service is uninstalled may be stale. Root owns builds, installation, hardware tests, commits and publication.
