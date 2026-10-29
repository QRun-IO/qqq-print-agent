# Local development package validation

2026-10-29:

- Published service, AppKit WinUI companion and CLI self-contained for win-x64, Release, version 0.1.0. Final shared runtime/CLI `worker.lock` code included.
- WiX 6.0.2 MSI build passed with zero warnings/errors. Only documented ICE03 vendor WinUI-language metadata exception; remaining MSI ICE validation enabled and passed.
- Build script passed repository PSScriptAnalyzer settings without diagnostics.
- Output: `artifacts/installer/QqqPrintAgent-0.1.0-x64.msi`.
- SHA-256: `93C671C6D6045888DBCDA9FFB450A1F8857DE00D710AB18B31DF731E17DCE1B5`.
- Development MSI is unsigned. No production signing was performed.

Installed-service acceptance was subsequently completed by the primary agent on 2026-10-29: MSI installation/start, enrollment over the named pipe, printer discovery under LocalSystem, PDF rendering and spool submission succeeded. The user confirmed the physical laser-printed page. The temporary test installation was successfully uninstalled; its protected journal was retained by installer policy. The development CLI also printed a separately user-confirmed page.

This validates the tested local laser queue and installed service happy path. Zebra hardware remains pending, and signing, upgrades and broader driver qualification are not covered by this result. Detailed local evidence is retained separately in `cloud-printing-integration`.
