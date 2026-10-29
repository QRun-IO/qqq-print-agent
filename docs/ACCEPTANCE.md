# Acceptance evidence

## v0.1.0-rc.1 — local qualification, 2026-10-29

The initial implementation was exercised on Windows x64 with the actual Java printing server and a locally installed Canon laser queue.

- CLI enrollment, printer inventory, server-side queue enablement, PDF submission, download integrity verification, native Windows rendering/spooling and status acknowledgment completed end to end.
- The user confirmed the physical one-page PDF output was correct.
- The machine MSI was installed, the LocalSystem service path was exercised, and the MSI was uninstalled during the local acceptance session. Refer to the release notes for the final installed-service result; this document does not claim a broad driver matrix.
- Core unit/contract tests: 12 passing. CLI/Windows tests: 7 passing, including SQLite restart/pending-event persistence, DPAPI protection/round trip, persisted exposure, submission rejection after deselection and credential-free snapshots.
- AppKit companion, service, CLI and installer compile/package validation completed locally with warnings treated as errors.

## Not established by this acceptance

Physical Zebra/ZPL output is pending connection of the Zebra printer. Automated elevated desktop/FlaUI execution, printer-driver stress/large documents, unattended reboot/logoff scenarios, MSI upgrade compatibility, proxy authentication, long-running retention and signed production distribution require additional qualification.

A successful spool acknowledgment is distinct from the user's physical-output confirmation. The automated test reports the former; the hardware acceptance included the latter for the tested laser setup.

The added tray/exposure feature subsequently passed the full Release build and 19 automated tests. Final installer/UI acceptance for this feature is recorded by the release owner; the earlier physical laser confirmation should not be mistaken for a complete tray/color hardware matrix.

