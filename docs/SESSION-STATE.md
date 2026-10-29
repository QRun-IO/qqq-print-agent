# Session state — 2026-10-29

Initial public prerelease prepared for QQQ 4.1+: AppKit companion with system tray, printer exposure selection, credential-free connection/printer/job/log snapshots, Windows service and diagnostic CLI. Printing uses outbound HTTPS polling, native Windows PDF/GDI and RAW ZPL adapters, DPAPI credentials and SQLite receipts/events.

Verification: full Release solution builds with zero warnings/errors; 12 core tests and 7 CLI/Windows tests pass (19 total). Shared protocol fixtures are pinned to qqq-print-protocol commit f977bd01b62ceb7539190fd2861809537fcf504c. Locked restore/build/test and coverage run through build/Verify.ps1. The prerelease MSI uses numeric product version 0.1.0 and assembly release version 0.1.0-rc.1.

Earlier hardware acceptance confirmed two physical PDF pages on a local Canon laser printer: one through the development CLI and one through the installed LocalSystem service. MSI installation/start and uninstall passed. Exact local queue/job identifiers and private diagnostic artifacts are retained outside this public repository.

The new tray/details UI was visually checked in disconnected/red state. Printer selection, history and snapshot behavior have automated coverage; final installed tray/exposure smoke is coordinated by the release owner. UI test project compiles; full automated desktop acceptance requires an elevated interactive session.

Publication and final artifact checks are in progress. Production signing, Zebra hardware, upgrades/reboot qualification, broad driver compatibility and full coverage remain open. See TODO.md, ACCEPTANCE.md and RELEASING.md.
