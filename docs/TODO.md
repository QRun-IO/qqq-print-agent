# TODO

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

## Current verification and release

- [x] Final Windows gate: 102 tests (36 Core + 66 Windows/CLI), zero build warnings/errors.
- [x] QQQ 0.1.1-rc.1 gate: 19 tests, zero Checkstyle violations.
- [x] Final MSI rebuilt from verified sources, zero warnings/errors; unsigned status and checksum recorded in FUNCTIONAL-AUDIT.md.
- [ ] Complete actual elevated upgrade and interactive UI acceptance after the pending user UAC approval.
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
