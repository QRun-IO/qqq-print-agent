# TODO

- [x] 0.1.5 source gate: 204 tests, zero warnings/errors; branding script analysis/dry-run passed.
- [x] Restore 0.1.4, then install corrected 0.1.5; all 12 native UI checks have passing results; 204 tests and exact-source CI passed.
- [x] Verify and install 0.1.6: 211 tests, clean build, passing exact-source CI, successful MSI upgrade and all 15 native UI checks across 14+1 runs.
- [ ] Diagnose the later clean service stop; normal-UAC restart restored Running, but the cause is unconfirmed.
- [x] Restore the paired local QQQ connection with credentials preserved and no pending jobs before startup; connection healthy on loopback port 18081.

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

- [x] Publish v0.1.6-rc.1 and verify its release assets.
- [x] Complete the 211-test source gate and exact-source CI; install 0.1.6 with credentials retained.
- [x] Complete all 15 installed UI checks across the initial 14 passes and one focused rerun; management, profile, pagination, log controls and branding checks passed.
- [x] Supersede the earlier 0.1.3/0.1.4 desktop and log-window rerun items with the later passing installed checks. Historical evidence remains in ACCEPTANCE.md.
- [x] Connect a Zebra ZT411 at 300 dpi with confirmed 4 × 6-inch stock; expose locally and enable in QQQ.
- [x] Submit exactly one ZPL shipping test and one PDF barcode/alignment test through the paired local QQQ server; both reached SPOOLED and the service remained Running.
- [ ] Obtain user confirmation of both Zebra outputs, alignment and barcode scanning. Spool acceptance alone does not establish physical output.
- [ ] Qualify real in-app update: consent, failed/canceled download or elevation, retained state, helper handoff, coherent versions, relaunch and restart-required reporting.
- [ ] Review completed protected update stages/logs manually; no automatic completed-stage retention policy is implemented.
- [ ] Qualify remaining input-dependent selection/focus, long-value and metadata synchronization scenarios; passing UI checks do not cover every interaction.
- [ ] Qualify installed credential recovery, queued/retry/cancel job controls, stale service state and bounded SCM recovery.
- [ ] Qualify task enable/disable, sign-in, reboot and upgrade with retained credentials/journal.
- [ ] Send a fresh explicitly authorized laser acceptance job through the final installed package; earlier laser confirmations belong to earlier builds.
- [ ] Complete signing and distribution qualification; do not describe unsigned builds as signed.

## Broader qualification

- [ ] Extend coverage toward the inherited CI target; unit-test success does not establish UI/accessibility coverage.
- [ ] Validate supported driver/network identity combinations and deployed QQQ integration.
- [ ] Qualify high contrast, Narrator, display scaling and small-screen behavior.
- [ ] Track custom media dimensions and physical-output confirmation separately from spooler acceptance.
