# Acceptance evidence

## Installed 0.1.9 audit correction — 2026-10-31

Reviewed source `13aa84c` passed all ten required hosted checks, including the 255-test build, security scans, both CodeQL languages, 12 PowerShell tests and script analysis. Exact CI MSI SHA256: `6DDA81FD515D33E2D27499AC9774D772B1C78094620CDCE098C2D28738D12EC1`.

The authorized machine upgrade completed with MSI exit 0 and no restart requirement. Old and new service peers authenticated; protected credential bytes and all journal history were retained. Read-only inspection of a protected closed backup verified integrity and no active or newly uncertain submission before installation. Post-install service, connection and history queries succeeded. All **16 installed UI checks passed in one run**, and the installed companion opened successfully. The package reports `0.1.9-rc.1+4602769b00ac76a6dbded3b4e63873f82e8ad287`, identifying the CI merge revision for the reviewed branch.

This verifies the corrected management transport and local installed service/app upgrade. It does not establish fresh physical output, Windows reboot behavior, updater-initiated elevation, hostile-tree rollback, remote deployment or publisher signing. No print was submitted by the installer or UI tests. Public release publication remains pending.

## Zebra PDF configuration correction and confirmed output — 2026-10-30

The user confirmed the laser PDF and Zebra ZPL output. The original Zebra PDF reached SPOOLED but produced no label. Windows machine **Printer properties → Advanced → Printing Defaults** was configured for **4.09 × 2 inches / Thermal Transfer**, while the installed stock is **4 × 6 inches and direct thermal, without ribbon**. The defaults were changed to **4.00 × 6.00 inches / Thermal Direct**, preserving other options; the saved machine DEVMODE dimensions were verified as 1016 × 1524 tenths of a millimeter. The printer was also returned to direct-thermal mode and unpaused without producing output during that correction.

One fresh reviewed PDF was then submitted through QQQ and the installed service. It reached spool receipt 9, the hardware counter advanced from 492 to 493, and the printer remained direct thermal and unpaused. **The user confirmed the replacement PDF label printed fully and correctly.** No application-code fix was needed. Barcode scanning and measured alignment remain unverified; the earlier spool-only result is not retroactively counted as successful physical output.

[Release workflow 41140882339](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41140882339) succeeded. [v0.1.7-rc.1](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v0.1.7-rc.1) is published, and its MSI, SHA256SUMS and two branding assets were verified. The MSI remains unsigned. This new evidence supersedes the physical-confirmation and release-pending statements in the initial 0.1.7 checkpoint below. It does not qualify unrelated printer drivers or a remote cloud deployment.

## Initial installed 0.1.7 and built-in diagnostic checkpoint — 2026-10-30

Installed **0.1.7, source 93dd5fc**, completed with MSI exit 0 and no reboot, retaining credentials. Management, computer profile, paged history, log settings, branded shortcut and connected-state checks passed. The source gate passed **227 tests (57 Core + 170 Windows/CLI)** with zero warnings/errors; [exact-source CI 41139885687](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41139885687) passed. All **16 installed UI checks passed in one run**, including the diagnostic media/DPI dialog, which was closed without printing during the UI test.

| Authorized built-in test | Generator profile | Recorded result |
| --- | --- | --- |
| Local Canon laser, one PDF | LETTER | SPOOLED; hash matches reviewed preview |
| Zebra, one ZPL label | LABEL_4X6_300 | SPOOLED; hash matches reviewed preview |
| Zebra, one PDF label | LABEL_4X6_300 | SPOOLED; hash matches reviewed preview |

These requests used the installed management pipe, QQQ generator and installed Windows service. **Physical paper/label output, alignment and barcode/scanner confirmation remain pending.** The UI suite separately inspected the native format/media choices and canceled without submitting a job. No extra job or output is inferred from UI success. QQQ branding, embedded warehouse-readable characters and bounded ZPL graphic fields were reviewed in generated previews before these submissions.

The local paired server was connected; this is loopback integration evidence, not remote-cloud deployment qualification. The v0.1.7-rc.1 tag is pushed and release automation is running; publication and release asset verification are still pending at this checkpoint. Earlier physical confirmations and historical failures below retain their original scope.

## Zebra submission checkpoint — 2026-10-30

Published Windows v0.1.6-rc.1 assets are verified. The existing paired QQQ database was restarted on loopback port 18081 with credentials preserved. Read-only inspection found no pending jobs before startup, and the connection became healthy. The locally exposed and server-enabled Zebra ZT411 reported READY at 300 dpi with user-confirmed 4 × 6-inch stock.

Exactly one ZPL shipping test and one PDF barcode/alignment test completed the server-to-service path and reached SPOOLED. Physical output, alignment and barcode scanning await user confirmation. The service remained Running during this session. This exercises the actual printing protocol against a local QQQ server; remote cloud deployment and broader driver qualification remain unproven. No production code changed.

## Installed 0.1.6 checkpoint — 2026-10-30

Current source adds inline expansion of selected printer rows, double-click printer settings/job details, wider responsive native dialogs and sortable table headers. Printers default to Name ascending. Jobs default to immutable Created descending, with whole-result ordering before paging and explicit missing historical creation times. Status updates do not redefine a job's creation time.

Installed **0.1.6, source d9033ae**, completed with exit 0 and no reboot, retaining credentials, four printers and two jobs. The source gate passed **211 tests (42 Core + 169 Windows/CLI)** with zero warnings/errors; [exact-source CI 41131558158](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41131558158) succeeded. Management, profile, logs, pagination and branding checks passed. All **15 installed UI checks have passing results across fourteen plus one runs**: the first run passed 14/15, then a test-only arbitrary-text double-click selector was corrected and the focused remaining check passed.

The service was later observed Stopped with exit 0 and no matching crash evidence; the cause remains unknown. A normal UAC-authorized restart succeeded and that checkpoint found it Running, with connectivity offline. No physical print or separate visual inspection was performed during those UI checks; the later Zebra submissions are recorded above. These results do not establish every Narrator, keyboard, resize, long-text, scaling or high-contrast scenario.

Source review also confirmed bounded expanded descriptions, text-selection guards, dialog resize handling, displayed sort keys and applied indicators. Additional checkbox/text-selection and service-query-failure scenarios remain to qualify; the recorded UI suite does not establish all possible interactions.

## Installed 0.1.5 checkpoint — 2026-10-30

Installed 0.1.5 passed 12 interactive checks across a ten-test run and a focused two-test run. This evidence belongs to that installed version. It does not qualify the subsequently changed 0.1.6 rows, sorting or dialogs, and it does not establish a new physical print, Zebra qualification, a general printer-driver matrix or production signing.

The older checkpoints below are retained as history; their pending approval statements and artifact hashes describe their own point in time.

## Historical 0.1.1 upgrade and 0.1.2 correction — 2026-10-30

The authorized 0.1.1 MSI upgrade completed with installer exit 0. Saved encrypted credential bytes were unchanged, and the Windows service restarted successfully. SCM inspection confirmed the two 30-second recovery restarts. Nine installed desktop tests then exposed a native startup failure: the constructor passed an uncreated NavigationView SettingsItem to AutomationProperties. This failed result supersedes prior compile-only UI evidence.

The 0.1.2 correction waits for Navigation.Loaded and checks the item before assigning its accessibility identifier. All 102 non-interactive regressions and the zero-warning build passed. The rebuilt unsigned MSI has SHA-256 `ADD29C41742440EC497903DCB87A3FC4A6DECE3D774CC4F8C78A74E3F097ACCA`. Windows canceled the second elevation request; the corrective 0.1.2 package is not installed and desktop retesting has not run. No new physical print was sent.

The sections below are historical checkpoints.

## Historical 0.1.1/0.1.2 source corrections

Source at that checkpoint included native Printers/Jobs/Logs/Settings navigation, unified AppKit logs, stable printer rows, service controls, opt-in sign-in startup, job identity/search/details/actions, accessible status text and branding contrast validation. Its integration corrections rejected premature pipe EOF as an unknown outcome, retained an offline explanation for missing printers, and discarded pre-action snapshots that could overwrite newer exposure state. Ordinary pipe requests were bounded to 35 seconds (3-second connection timeout); enrollment to 150 seconds.

That source build passed with zero warnings/errors and 102 unit/regression tests (36 Core and 66 Windows/CLI). Its MSI upgrade/UAC approval and elevated interactive UI acceptance were still pending when recorded. That unit gate did not qualify the installed companion, tray, startup task or operator actions. See [the current UX review](UX-REVIEW.md) for current checks.

The historical evidence below includes **two user-confirmed laser pages**: the CLI path and the installed LocalSystem service path. Those confirmations remain valid for the tested earlier builds and do not qualify every subsequent UI/action change.

## v0.1.0-rc.1 — local qualification, 2026-10-29

The initial implementation was exercised on Windows x64 with the actual Java printing server and a locally installed Canon laser queue.

- CLI enrollment, printer inventory, server-side queue enablement, PDF submission, download integrity verification, native Windows rendering/spooling and status acknowledgment completed end to end.
- The user confirmed the physical one-page PDF output was correct.
- The machine MSI was installed, the LocalSystem service printed a second PDF, and the user confirmed that physical output. The MSI was uninstalled during the local acceptance session. This document does not claim a broad driver matrix.
- Core unit/contract/branding tests: 23 passing. CLI/Windows tests: 7 passing, including SQLite restart/pending-event persistence, DPAPI protection/round trip, persisted exposure, submission rejection after deselection and credential-free snapshots.
- AppKit companion, service, CLI and installer compile/package validation completed locally with warnings treated as errors.

## Historical limits of the initial acceptance

Physical Zebra/ZPL output is pending connection of the Zebra printer. Automated elevated desktop/FlaUI execution, printer-driver stress/large documents, unattended reboot/logoff scenarios, MSI upgrade compatibility, proxy authentication, long-running retention and signed production distribution require additional qualification.

A successful spool acknowledgment is distinct from the user's physical-output confirmation. The automated test reports the former; the hardware acceptance included the latter for the tested laser setup.

The added tray/exposure feature subsequently passed the full Release build and 30 automated tests, including customer branding validation/fallback. Final installer/UI acceptance for this feature is recorded by the release owner; the earlier physical laser confirmation should not be mistaken for a complete tray/color hardware matrix.

The installed tray/exposure service subsequently submitted another PDF to the laser and reported the expected green/ready, orange/no exposed queues and red/disconnected snapshots. Removing the local exposure also made the QQQ queue unavailable. The user confirmed that additional physical page, completing two confirmed laser outputs in the historical acceptance session. The final branded companion was not interactively qualified because its UAC launch was canceled; no automated elevated UI result is claimed. The published 1b7db88 release passed GitHub's locked build, 30 tests and MSI packaging; the published checksum is in the release's SHA256SUMS.
