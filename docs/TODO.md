# TODO

- [x] Reproduce and correct management-response loss during immediate named-pipe disconnect; current/legacy delayed readers and bounded shutdown/deadline regressions pass.
- [x] Install 0.1.8 with preserved credentials/journal and authenticated peers; independently confirm current QQQ heartbeat, two ready printers and no pending or uncertain jobs. The history-check failure requires the correction above.
- [x] Pass the combined 0.1.9 solution/UI build with zero warnings/errors and all 255 tests (66 Core + 189 Windows/CLI); verify source/fixture bytes before and after the isolated run.
- [x] Complete 0.1.9 hosted gates, state-preserving upgrade and installed app/service qualification: required checks, retained credentials/history, connected service and 16 installed UI checks pass.
- [ ] Publish and verify the protected 0.1.9 corrective prerelease after recording installed acceptance.
- [x] Audit source regression gate: 249 tests and zero build warnings/errors, including authenticated/bounded IPC, machine-state ownership, metadata preservation, backlog scheduling and SQLite initialization cleanup.
- [x] Complete independent deep security scan, investigate all five findings, review fixes and record canonical findings/coverage.
- [x] Qualify bounded production store/merge queries at 375,000 synthetic jobs and append 10,000 through journal APIs; integrity and retention checks pass. See SCALABILITY.md for measured scope; sustained service/network/printer throughput remains separate.
- [ ] Verify audit packaging, installed UI/updater handoff and hosted security/provenance gates before publishing the next prerelease.
- [x] Record user decision: prereleases remain unsigned for now; do not describe hashes or build attestations as code signing.
- [x] Publish audit draft PR #2; final executable source 2e6a2e5 passes all hosted checks. Download and verify CI MSI/branding checksums and actual installer formatting/sequence/component identities. New installation and tagged-release attestations remain pending.
- [x] Enforce all main-branch checks for administrators too, following explicit user approval; establish acknowledged R-drive coordination with Mobile Scanner and Record Files masters.

- [x] Install 0.1.7 from 93dd5fc: MSI exit 0, no reboot, credentials retained; management/profile/history/log settings/branding/connected-state checks passed.
- [x] Pass 227 unit/regression tests (57 Core + 170 Windows/CLI), zero warnings/errors and exact-source CI 41139885687.
- [x] Pass all 16 installed UI checks in one run, including read-only diagnostic media/DPI choices.
- [x] Submit one built-in PDF Letter laser job, one ZPL 4 × 6/300-dpi Zebra job and one PDF 4 × 6 Zebra job; all SPOOLED and content hashes match reviewed previews.
- [x] User confirmed laser PDF and Zebra ZPL output. Diagnose the missing Zebra PDF as machine media/method settings, correct to 4 × 6-inch Thermal Direct and confirm one replacement PDF printed fully and correctly.
- [ ] Verify barcode scanning and measured alignment; successful appearance/counter evidence does not establish these checks.
- [x] Release workflow 41140882339 succeeded; verify published v0.1.7-rc.1 MSI, SHA256SUMS and both branding assets.
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
- [ ] Obtain user confirmation of the earlier 0.1.6 Zebra outputs, alignment and barcode scanning. Spool acceptance alone does not establish physical output.
- [ ] Qualify real in-app update: consent, failed/canceled download or elevation, retained state, helper handoff, coherent versions, relaunch and restart-required reporting.
- [ ] Review completed protected update stages/logs manually; no automatic completed-stage retention policy is implemented.
- [ ] Qualify remaining input-dependent selection/focus, long-value and metadata synchronization scenarios; passing UI checks do not cover every interaction.
- [ ] Qualify installed credential recovery, queued/retry/cancel job controls, stale service state and bounded SCM recovery.
- [ ] Qualify task enable/disable, sign-in, reboot and upgrade with retained credentials/journal.
- [x] Send the authorized built-in PDF Letter laser diagnostic through installed 0.1.7; spool acceptance and user-confirmed physical output are recorded above.
- [ ] Complete signing and distribution qualification; do not describe unsigned builds as signed.

## Broader qualification

- [ ] Extend coverage toward the inherited CI target; unit-test success does not establish UI/accessibility coverage.
- [ ] Validate supported driver/network identity combinations and deployed QQQ integration.
- [ ] Qualify high contrast, Narrator, display scaling and small-screen behavior.
- [ ] Track custom media dimensions and physical-output confirmation separately from spooler acceptance.

- [x] Complete independent review after the five baseline security findings are remediated; current details are in SESSION-STATE.md.
- [ ] Complete installed and deployment qualification; the normal-UAC upgrade request is pending, not a successful installation.
