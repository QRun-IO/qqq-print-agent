# TODO

## Open for 2.0

- [ ] CI definitions still name a literal version for branch builds: `.github/workflows/ci.yml` falls back to `1.2.1`, and so does the Gitea build file proposed in pull request 17. On `release/2.0` the source version is 2.0.0, so `Build-Installer.ps1` refuses those builds ("ProductVersion must match the reviewed source VersionPrefix"). A branch build must name no version (`./build/Build-CiRelease.ps1` then packages the source version); a tag build passes the tag's version. Removing the fallback is not enough: an empty `-ProductVersion` is refused, so the parameter must be left out, and the step that locates the MSI by the same variable needs the value from `./build/Get-SourceVersion.ps1`. Owned by the CI work; the workflow files are deliberately not changed by the 2.0 branches.
- [ ] Hardware check, early (release condition, [ADR-0003](adr/0003-version-2.md)): due now, because the printing seams step it waited for merged as pull request 42 (`e2ca086`). On the development PC, by driving the built print helper without installing anything. A Zebra ZT411 at 300 dpi for ZPL and a Canon office printer for PDF; the owner approves each print at the time. Not run.
- [ ] Hardware check, final (release condition): on the release candidate, through the installed service, same two printers, each print approved by the owner. "Ready to merge to `main`" is held until it is recorded in [ACCEPTANCE.md](ACCEPTANCE.md), with "the spooler accepted the job" and "paper came out" as separate statements. Not run.
- [ ] Standards exception EX-0016 (the Windows coverage bar of 90%): in standards pull request 58, which is open; its status is "Proposed, awaiting approval".
- [ ] Open findings from the two retrospective reviews of 2026-11-10 and from the review of the printing seams step: listed in [the audit](AUDIT-2026-11-10.md) under "Found by the retrospective reviews". The should-fix findings about the pipe contract close before anything serves the contract.
- [ ] Qualify the upgrade from 1.2.x to 2.0. Not done: the 2026-11-10 snapshot (`snapshot-2.0.0-rc.1`) was not installed anywhere. The qualification also has to cover what pull request 41 left unverified: no elevated MSI run has exercised the new branding and startup code, so the real machine registry key, the installer's custom actions, and shell and folder creation as SYSTEM are unexercised, and its real-shell tests ran only on the development PC's Windows build.

## Released: 1.2.1 — 2026-11-06

Client 1.2.1 (source `3b9d75d`, GitHub release run 42223525626) is the current stable: signed generic and GG Forge installers on GitHub and, byte-identical, on Gitea. See [RELEASING](RELEASING.md).

- [x] Nonce fix PR 11 (`abc3d0d`) and the OPTIONAL/discovery nonce change (`c59814d`) shipped in `v1.2.0`/`v1.2.1`.
- [x] Release run 42128511730 on `v1.2.0` failed in `sign (gg-forge)`: `release.yml` array-splatted `-Edition`, binding it positionally to `-CertificateThumbprint`. Fixed in PR 12 (`3b9d75d`); `v1.2.0` stays immutable and unpublished.
- [x] `v1.2.1` tagged on the merge commit, mirrored to GitHub, signed, assembled and published; checksums, signatures and provenance verified; same assets attached to the Gitea release.
- [x] Post-merge main checks (ci, codeql, security, scripts, scorecard) green on `b583130`.
- [ ] Actual native sign-in/sign-out acceptance against the shared provider (the shared-fixture discovery pass is not acceptance).

## Historical: required-nonce native sign-in fix — 2026-11-05 (published in 1.2.x)

The stable printing release below remains accepted historical evidence. The shared-server scanner Authentik requires this fix (`fix/native-login-required-nonce`, integrated on main `f954f11`).

- [x] Implement per-attempt nonce kept only under `REQUIRED`, sent as `expectedNonce`; legacy four-field body preserved; unknown policy refused before provider/browser.
- [x] Root verified exact native callback registrations and real server HTTPS discovery in an isolated shared scanner/Printing 1.0.0 fixture with `REQUIRED` nonce (discovery only, not login proof).
- [x] Integrated NativeCI Release build: zero warnings/errors; 352 Windows/CLI tests passed with no failures/skips; source notices passed for 300 tracked files. Existing method-length limit retained; Core.Tests excluded locally.
- [x] Hosted CI including Core.Tests: GitHub CI on the editions branch carrying this fix passed 182 Core and 352 Windows/CLI tests (run 42078731426, 2026-11-05).
- [ ] Real shared-provider native login acceptance.
- [x] Final published build: 1.2.1 (see above).

## Closed product release — 2026-11-04

- [x] Publish signed Windows 1.0.1 on Gitea and the GitHub updater feed; release run 41532214447 passed its gates and Azure OIDC signing.
- [x] Verify release assets, signatures and provenance; install the official MSI locally. November 3 receipt: exit 0, matching app/service/CLI 1.0.1, service Running.
- [x] Publish qbit-printing 1.0.0 and protocol 1.0.0 with QQQ 4.1.0-RC.21 native sign-in support.
- [x] Refresh current README, operator and maintainer documentation against the stable source; preserve dated evidence.

The requested product work is complete. Shared CI migration is separate. Everything below is historical or unscheduled backlog; unchecked boxes do not reopen work or authorize more tests. See [Release readiness](RELEASE-READINESS.md).

## Documentation publication scope

The user authorized publishing `docs/printing-release-closeout-20261104` through a normal Gitea PR, including its automatic Microsoft-hosted checks. The previous publication hold is superseded for that scope. No code or workflow change, local/VM6218 build, quarantine restoration or protection exception is part of this update. The PR records publication and merge status.

## Historical stable 1.0 checkpoint — 2026-11-02

Active stable-release work is only the two open items below. Every other unchecked item in this file is historical or unscheduled backlog, not a stable-1.0 blocker.

- [x] Official 0.4.0-rc.4 installed (all three components); normal MSI upgrade exit 0 and the service Running.
- [x] Clean hosted Windows CI run 41442796961 on source `53fe1b8281806eda7adc314f92c8205aefcb51ce`: 499 tests (166 Core + 333 CLI), zero failures and skips; build and MSI zero warnings and errors. Core.Tests is never built locally; this hosted pass is its evidence. (Earlier run 41384467500 on `b5eb5d73` is historical.)
- [x] Stable signing implemented (workflow, `Sign-Release.ps1`, packaging). All 19 first-party binary copies of an installed RC4 payload, including `Cli/qqq-print-agent.dll`, and a disposable official RC4 MSI were Azure-signed and strictly verified; this is a local administrator proof and the new CI OIDC identity is not yet exercised. Installed and published remains RC4; no new stable tag.
- [x] Primary Gitea feed and public feed carry all three prereleases (Windows 0.4.0-rc.4; server and protocol 0.4.0-rc.1); nine assets checked and provenance verified.
- [x] Physical 4 × 1 passed by the operator on RC3 (same relevant printing runtime); 4 × 6 and Letter accepted earlier. Physical 4 × 2 is deferred by the operator and is not a stable blocker.
- [x] Source-header inventory (289 tracked files, 231 C#) reported zero issues.
- [ ] Windows release branch (Gitea PR 2): normal protected merge, awaiting the central native 1725 repair for Windows ACL and file sharing on Linux.
- [ ] Supported, published QQQ core with native session login (the last official release, RC20, does not have it); stable tag publication, including the first CI OIDC signing run, follows it.
- Accepted known limitation (user decision 2026-11-02, not a blocker, not fixed): the post-print native UI layout exception; see the historical entries below.
- Cancelled by the user, do not resume: repeat Auth/S3 qualification, the latency investigation and MSI comparison analysis.

## Historical rc.3 status (superseded by rc.4 and the closeout above)

- [x] Implement ordered numeric FileVersion (rc.N revision N, stable 65535) in packaging; source `e10d251`, packaging-input checks and rc.3 production packaging passed (all payloads `0.4.0.3`, `0.4.0-rc.3+e10d251`).
- [x] Complete the local rc.3 installation: normal MSI upgrade exit 0; app, service and CLI match rc.3 and numeric file version `0.4.0.3`, with no repair required.
- [x] Confirm the 4 × 1 label prints correctly after media alignment; operator reports it printed perfectly.
- [ ] Physical 4 × 2 test deferred by the operator, who chose to keep 4 × 1 loaded. The test option is implemented; do not send another label until requested. Not a stable blocker.
- [x] Gitea release artifact and update feed: published for rc.4 (see the closeout above). Shared Windows CI remains the active protected-merge item; no GA claim. Core.Tests is not run on this host (including Gitea runner 6).

## Script quality gates

- [x] Pin Ruff, strict Pyright, ESLint/Stylistic and strict checkJs with locked restores; reject untracked-scope Python/JavaScript and zero-file Pyright runs; negative tool probes pass locally; virtual-agent Downloads/identity behavior preserved.
- [ ] Observe the new `virtual-agent` job steps pass on hosted CI before relying on them.

## Candidate Echo printer media

- [x] Discover truthful machine-default dimensions/DPI and expose an opaque, revision-bound profile.
- [x] Reject stale/unknown selection before spool; pass captured validated defaults to selected PDF DCs.
- [x] Normalize supported job-only driver copies/scale and verify effective DC dimensions/DPI before StartDoc (24 focused Windows tests plus read-only actual driver/DC probe).
- [x] Verify synthetic 4 × 1 / 4 × 2 / 4 × 6 profiles, driver-private drift, unknown DPI and isolated-worker refusal.
- [x] Verify configured 4 × 6 / 300 DPI fingerprint stability across three read-only helper launches.
- [x] Complete the C# source standards cleanup (how/why headers and structural spacing) and merge it with the short-label change; token-compared as comment/blank-line only per batch.
- [x] Merged integration tree: production UI build (strict, 0 warnings) and the 35 existing paper/media tests passed locally (R39).
- [x] Hosted CI passed (499 tests, run 41384467500), the label implementation is installed in rc.4, and the 4 × 1 stock printed correctly (4 × 2 deferred, not a blocker). Earlier text here said "not installed yet"; that is historical.
- [x] Local Core test-assembly quarantine: not pursued. Core.Tests is never built locally; the clean hosted pass is the evidence and no protection was disabled.
- [ ] Qualify the matching server/consumer catalog, immutable selection and authorized retained-document submission.
- [ ] Perform separately authorized installed/profile-selected physical acceptance; available alternative stock is not current queue configuration.

## Query in About

- [x] Place the official mascot and dogfooding/Query links directly below the
  shared About header, preserving customer logos, identity and credits.
- [x] Verify both native themes, initial image/link visibility, unclipped names and
  Close; pass the 426-test Release gate and exact publish-payload asset check.
- [ ] Package, publish and verify the About change in an installed follow-up.

## Companion layout, live transport status and startup follow-up

- [x] Install the attested published 0.3.1 MSI; verify all three exact component versions, automatic WebSocket restoration and preserved local receipts, pairing and profiles.
- [x] Record operator-confirmed test prints and responsive installed 0.3.1 window; verify repeated native dialog/refresh/banner/status replays at observed 125% DPI.
- [x] Reject a successful MSI outcome when the app, service or CLI has a missing, old or different full build version; pass the integrated 426-test gate.
- [x] Publish [0.3.2-rc.1](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v0.3.2-rc.1); verify all nine [release jobs](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41324312106), four assets/attestations and static MSI version, resources, service/callback registration and notices. The MSI remains unsigned.
- [x] Qualify the actual AppKit 0.3.1-to-0.3.2 update: operator-confirmed Not now/responsiveness, Update and restart, verified old-helper handoff, MSI exit 0, exact published component bytes, preserved identity/profiles/earlier jobs, restored WebSocket and automatic visible reopen.
- [x] Pass isolated real Authentik native-session login, permission, logout, expiry/renewal and revocation checks with production client code against unreleased QQQ API 1.9 candidate `ecf0f550` in the local Docker reference environment.
- [ ] Qualify the installed Windows browser/private callback against a compatible published QQQ runtime and configured HTTPS provider; the container HTTPS callback does not establish this installed flow.
- [x] Publish and verify all three 0.3.0-rc.1 releases; install the matched local server/client and verify retained state and service restart recovery.
- [x] Capture the operator's post-print LayoutCycleException and recover only the companion; retain existing print receipts.
- [ ] Native post-print layout cycle: reproduce and fix with a nonprinting host. **Accepted known limitation for 1.0 (user decision 2026-11-02): not fixed and not a blocker.** Left unchecked as history.
- [x] Verify actual native fatal-layout exception logging, handler order and natural diagnostic termination; keep this mitigation distinct from fixing the original trigger.
- [x] Accept scheduler-normalized account names only when they resolve to the exact expected startup SID; verify rejection of other identities and the unchanged actual task.
- [x] Show explicit WebSocket state, server endpoint, live upload/download rates and service-lifetime application-traffic totals in the footer; verify actual native presentation with synthetic traffic.
- [x] Preserve the original diagnostic launcher's finalization failure and independently validate both passing child reports; no installer retry or observer print was needed. Keep 0.3.1 physical acceptance separate.

## Realtime and Jobs UI — 0.3.0-rc.1 release candidate

- [x] Adopt protocol commit `df3729ac632d4a34a3fb8423acef465042a624fc` for printing extension 2.0 and its complete configurable policy.
- [x] Update current architecture/operations documentation for WSS, streamed HTTPS, administrator limits and bounded queue observations; preserve published 0.2 evidence.
- [x] Compile Jobs UI and UI tests without warnings; pass three headless stale-detail generation regressions.
- [ ] Complete combined source and independent runtime review gates, including stalled-transfer controls, ordering, lost ACKs and reconnect recovery.
- [ ] Qualify installed realtime service/UI, configured provider renewal and physical output separately; no source check implies those results.

The following entries preserve earlier implementation checkpoints.

## Remote controls candidate — not released

- [x] Restore pending unbound setup across restart with unreadable or different-server old proof; preserve bound-identity refusal. Final isolated gate: 365 tests, zero warnings/errors/skips.

- [x] Add explicit replacement pairing after revocation, retain pending unbound sign-in across restart, and verify rejected setup preserves old state; 361-test full gate and final brand-neutral UI build pass.

- [x] Reproduce unreadable session/device proof recovery failures and pass explicit recovery, denied binding, sign-out and preserved-journal regressions; full isolated gate now 360 tests with no warnings/errors/skips.

- [x] Implement independent bounded control polling and persisted pause/disconnect state without interrupting active print submission.
- [x] Persist command execution intent and immutable delivery receipts; recover interrupted commands as unknown without replaying physical cancellation.
- [x] Reuse verified single-job spool cancellation, preserving current-connection ownership and reporting absence, refusal and uncertainty explicitly.
- [x] Add companion desired-state, control-contact and last-action status with accessible text.
- [x] Complete the isolated Verify.ps1 gate: 279 tests (72 Core + 207 Windows/CLI), zero warnings/errors/skips. Independent control-boundary review issues are fixed with runtime regressions.
- [x] Wire native QQQ printing transport and refuse control-404 legacy fallback on that advertised extension; keep direct bearer diagnostics behind explicit `--legacy-compat`.
- [x] Implement standard QQQ auth discovery/configured sign-in, protected service session storage and periodic resume, preserving device/journal identity.
- [x] Complete the combined native-authentication, callback forwarding, CLI, UI and control gate: 356 tests (127 Core + 229 Windows/CLI), zero warnings/errors/skips, and independent source review fixes rechecked.
- [ ] Qualify the configured provider and installed private callback end to end; source tests do not establish actual provider renewal or permissions deployment.
- [x] Pass 23 actual Core client/native QQQ interoperability checks with table-based authentication and no print jobs.
- [x] Build and inspect the unsigned candidate MSI, including payload versions/resources, callback registration and service configuration.
- [ ] Qualify the installed companion after protected CI. No new physical printing is implied.

## Published 0.1.9-rc.1 — 2026-10-31

- [x] Reproduce and correct management-response loss during immediate named-pipe disconnect; current/legacy delayed readers and bounded shutdown/deadline regressions pass.
- [x] Record the superseded 0.1.8 installation with preserved credentials/journal and authenticated peers. Its history-check failure required the management-response correction above.
- [x] Pass the combined 0.1.9 solution/UI build with zero warnings/errors and all 255 tests (66 Core + 189 Windows/CLI); verify source/fixture bytes before and after the isolated run.
- [x] Complete 0.1.9 hosted gates, state-preserving upgrade and installed app/service qualification: required checks, retained credentials/history, connected service and 16 installed UI checks pass.
- [x] Publish v0.1.9-rc.1 from protected main `cf0e362`; [release workflow 41190815995](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41190815995), all four assets and all four provenance attestations are verified.
- [x] Record the distinct installed CI and published MSI hashes in [ACCEPTANCE.md](ACCEPTANCE.md). Runtime sources are unchanged; no separate installation of the published MSI is claimed.
- [x] Audit source regression gate: 249 tests and zero build warnings/errors, including authenticated/bounded IPC, machine-state ownership, metadata preservation, backlog scheduling and SQLite initialization cleanup.
- [x] Complete independent deep security scan, investigate all five findings, review fixes and record canonical findings/coverage.
- [x] Qualify bounded production store/merge queries at 375,000 synthetic jobs and append 10,000 through journal APIs; integrity and retention checks pass. See SCALABILITY.md for measured scope; sustained service/network/printer throughput remains separate.
- [x] Verify audit packaging, installed UI and hosted security/provenance gates for 0.1.9. Real in-app updater handoff remains a separate task below.
- [x] Record user decision: prereleases remain unsigned for now; do not describe hashes or build attestations as code signing.
- [x] Merge [audit PR #2](https://github.com/QRun-IO/qqq-print-agent/pull/2) through protected main after the corrective source and installed acceptance passed. Preserve the earlier 0.1.8 audit packaging evidence as history.
- [x] Enforce all main-branch checks for administrators too.

## Historical 0.1.7 acceptance — 2026-10-30

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
- [x] Restore the QQQ connection with credentials preserved and no pending jobs before startup.

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

## Historical 0.1.6 verification — 2026-10-30

- [x] Publish v0.1.6-rc.1 and verify its release assets.
- [x] Complete the 211-test source gate and exact-source CI; install 0.1.6 with credentials retained.
- [x] Complete all 15 installed UI checks across the initial 14 passes and one focused rerun; management, profile, pagination, log controls and branding checks passed.
- [x] Supersede the earlier 0.1.3/0.1.4 desktop and log-window rerun items with the later passing installed checks. Historical evidence remains in ACCEPTANCE.md.
- [x] Connect a Zebra ZT411 at 300 dpi with confirmed 4 × 6-inch stock; expose locally and enable in QQQ.
- [x] Submit exactly one ZPL shipping test and one PDF barcode/alignment test through the paired local QQQ server; both reached SPOOLED and the service remained Running.
- [ ] Obtain user confirmation of the earlier 0.1.6 Zebra outputs, alignment and barcode scanning. Spool acceptance alone does not establish physical output.

## Remaining installed qualification

- [ ] Qualify actual update download/installer failure and cancellation, restart-required reporting and reboot recovery; the successful 0.3.2 update above does not establish these cases.
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
- [ ] Complete remaining deployment and hardware qualification; the successful 0.1.9 CI-package installation does not establish remote or broader driver acceptance.
