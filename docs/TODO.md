# TODO

- [x] 0.1.5 source gate: 204 tests, zero warnings/errors; branding script analysis/dry-run passed.
- [x] Restore 0.1.4, then install corrected 0.1.5; all 12 native UI checks have passing results; 204 tests and exact-source CI passed.
- [ ] Verify and install 0.1.6 row expansion, double-click dialogs and full-history sorting (implementation complete).

## Implemented

- [x] AppKit-based Windows companion, service and CLI; outbound QQQ enrollment, discovery, claim/content/events and polling.
- [x] Native PDF/GDI and RAW ZPL printing, integrity validation, DPAPI credentials and durable journal.
- [x] Printer exposure opt-in, approval distinction, unavailable/unsupported queue handling and correct native status flags.
- [x] Native navigation, one shared persistent/redacted AppKit log viewer, status tray and single-instance source behavior.
- [x] Timeout/pipe liveness, reconnect wakeup, independent heartbeat while printing and stale/EOF status correction.
- [x] Connection-scoped outbox/history, interruption recovery without duplicate printing and explicit damaged-credential recovery with encrypted backup.
- [x] Friendly job identity, history/filter/details, persistent review acknowledgement and safe retention preserving deduplication.
- [x] Test print, retry with duplicate-risk confirmation, queued cloud-job visibility/cancellation and identity-checked spooler cancellation.
- [x] Bounded printer subprocesses, timeout/orphan cleanup and CLI/service helper integration.
- [x] Per-user foreground CLI state, mutation locks, protected service directory and nonzero timeout exit status.
- [x] Service controls/install guidance, explicit administrator opt-in sign-in task and bounded SCM recovery authoring.
- [x] Runtime branding, local bounded logo validation and white-text accent contrast policy.
- [x] Public repository/CI/security and original v0.1.0-rc.1 publication; warning records its WinUI packaging defect.
- [x] Earlier development and temporary installed LocalSystem laser output confirmed by the user; original install/start/uninstall acceptance recorded separately.

- [x] AppKit update coordinator/prompts, manual/startup checks, fixed GitHub version/channel policy, verified protected download and coherent machine MSI helper.
- [x] Persisted splash duration, shared help/release/project/support links, dependency credits and version/channel display.

## Current verification and release

- [x] User confirmed the installed 0.1.4 tray icon works.
- [x] Implement compact tables, 50-row history queries, metadata editors and synchronized labels, log limits and confirmed clearing.
- [ ] Complete the new source gate and installed acceptance for selection/focus, paging, long values, metadata synchronization and log controls.

- [x] Install0.1.4 tray/menu correction, retire obsolete debug companions, retain credentials and verify running service; source163tests and exact-commit GitHub CI passed.
- [x] Installed0.1.4 menu/About, splash, close/relaunch and other shell checks:10passed.
- [ ] Complete log-window focused rerun (initial0.1.4 suite had1failure).
- [x] Current 0.1.3-rc.1 Windows gate: 163 tests (36 Core + 127 Windows/CLI), zero build warnings/errors and locked restore. UI tests compile only.
- [x] QQQ 0.1.1-rc.1 gate: 19 tests, zero Checkstyle violations.
- [x] Package and record the final 0.1.3-rc.1 MSI/checksum from source7344914; its GitHub CI build/test/package gate passed.
- [x] Install 0.1.1 with retained encrypted credentials and successful service restart.
- [x] Install corrective 0.1.3: MSI exit0, no reboot required, encrypted credentials retained, service restart/management pipe passed and four queues discovered.
- [ ] Complete installed desktop acceptance: first run5passed/5failed; correct pinned-AppKit/test-visibility assumptions and qualify input-dependent checks on an accessible desktop. Initial cloud snapshot was offline; recheck live connection.
- [ ] Qualify real in-app update: consent, failed/canceled download or elevation, retained state, helper handoff, coherent versions, relaunch and restart-required reporting.
- [ ] Review completed protected update stages/logs manually; no automatic completed-stage retention policy is implemented.
- [ ] Verify tray/duplicate launch/close/exit, reconnect, stale service state, unified logs, job controls and keyboard/focus on the installed app.
- [ ] Qualify installed credential recovery, exposure/history persistence, service start/restart and bounded recovery.
- [ ] Qualify task enable/disable, sign-in, reboot and upgrade with retained credentials/journal.
- [ ] Send a fresh explicitly authorized laser acceptance job through the final installed package; no new physical print has occurred for these corrections.
- [ ] Attach Zebra and validate real ZPL output with intended stock/DPI.
- [ ] Complete signing and distribution qualification; do not describe unsigned builds as signed.
- [ ] Publish the reviewed replacement prerelease only after the applicable gates and acceptance evidence are recorded.

## Broader qualification

- [ ] Extend coverage toward the inherited CI target; unit-test success does not establish UI/accessibility coverage.
- [ ] Validate supported driver/network identity combinations and deployed QQQ integration.
- [ ] Qualify high contrast, Narrator, display scaling and small-screen behavior.
- [ ] Track custom media dimensions and physical-output confirmation separately from spooler acceptance.
