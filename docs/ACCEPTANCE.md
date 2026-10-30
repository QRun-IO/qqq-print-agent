# Acceptance evidence

## Installed upgrade checkpoint — 2026-10-30

The authorized 0.1.1 MSI upgrade completed with installer exit 0. Saved encrypted credential bytes were unchanged, and the Windows service restarted successfully. SCM inspection confirmed the two 30-second recovery restarts. Nine installed desktop tests then exposed a native startup failure: the constructor passed an uncreated NavigationView SettingsItem to AutomationProperties. This failed result supersedes prior compile-only UI evidence.

The 0.1.2 correction waits for Navigation.Loaded and checks the item before assigning its accessibility identifier. All 102 non-interactive regressions and the zero-warning build passed. The rebuilt unsigned MSI has SHA-256 `ADD29C41742440EC497903DCB87A3FC4A6DECE3D774CC4F8C78A74E3F097ACCA`. Corrective installation and desktop retest are awaiting the new Windows elevation prompt. No new physical print was sent.

The sections below are historical checkpoints.

## Current source corrections — installed acceptance pending

The current source includes native Printers/Jobs/Logs/Settings navigation, unified AppKit logs, stable printer rows, service controls, opt-in sign-in startup, job identity/search/details/actions, accessible status text and branding contrast validation. The last integration corrections reject premature pipe EOF as an unknown outcome, retain an offline explanation for missing printers, and discard pre-action snapshots that could overwrite newer exposure state. Ordinary pipe requests are bounded to 35 seconds (3-second connection timeout); enrollment is bounded to 150 seconds.

The latest source build passed with zero warnings/errors. The final gate passed 102 unit/regression tests (36 Core and 66 Windows/CLI). Current MSI upgrade/UAC approval and elevated interactive UI acceptance are pending. This section does not claim the installed companion, tray, startup task or new operator actions have passed acceptance. See [the current UX review](UX-REVIEW.md) for the remaining checks.

The historical evidence below includes **two user-confirmed laser pages**: the CLI path and the installed LocalSystem service path. Those confirmations remain valid for the tested earlier builds and do not qualify every subsequent UI/action change.

## v0.1.0-rc.1 — local qualification, 2026-10-29

The initial implementation was exercised on Windows x64 with the actual Java printing server and a locally installed Canon laser queue.

- CLI enrollment, printer inventory, server-side queue enablement, PDF submission, download integrity verification, native Windows rendering/spooling and status acknowledgment completed end to end.
- The user confirmed the physical one-page PDF output was correct.
- The machine MSI was installed, the LocalSystem service printed a second PDF, and the user confirmed that physical output. The MSI was uninstalled during the local acceptance session. This document does not claim a broad driver matrix.
- Core unit/contract/branding tests: 23 passing. CLI/Windows tests: 7 passing, including SQLite restart/pending-event persistence, DPAPI protection/round trip, persisted exposure, submission rejection after deselection and credential-free snapshots.
- AppKit companion, service, CLI and installer compile/package validation completed locally with warnings treated as errors.

## Not established by this acceptance

Physical Zebra/ZPL output is pending connection of the Zebra printer. Automated elevated desktop/FlaUI execution, printer-driver stress/large documents, unattended reboot/logoff scenarios, MSI upgrade compatibility, proxy authentication, long-running retention and signed production distribution require additional qualification.

A successful spool acknowledgment is distinct from the user's physical-output confirmation. The automated test reports the former; the hardware acceptance included the latter for the tested laser setup.

The added tray/exposure feature subsequently passed the full Release build and 30 automated tests, including customer branding validation/fallback. Final installer/UI acceptance for this feature is recorded by the release owner; the earlier physical laser confirmation should not be mistaken for a complete tray/color hardware matrix.

The installed tray/exposure service subsequently submitted another PDF to the laser and reported the expected green/ready, orange/no exposed queues and red/disconnected snapshots. Removing the local exposure also made the QQQ queue unavailable. The user confirmed that additional physical page, completing two confirmed laser outputs in the historical acceptance session. The final branded companion was not interactively qualified because its UAC launch was canceled; no automated elevated UI result is claimed. The published 1b7db88 release passed GitHub's locked build, 30 tests and MSI packaging; the published checksum is in the release's SHA256SUMS.
