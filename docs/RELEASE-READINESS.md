# Release readiness — signed 1.0 closeout

Updated 2026-11-04. The requested product release and local installation are complete. The dated records below distinguish the stable release from earlier candidate evidence; they are not a claim that every printer or deployment has been qualified.

## Current release

- **Windows:** signed [1.0.1 on Gitea](https://git.kof22.com/QRun-IO/qqq-print-agent/releases/tag/v1.0.1) and matching [GitHub updater assets](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v1.0.1), from source `5bf00f3291923f7afb34c6cce61276e69649689b`.
- **Release gate:** [run 41532214447](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41532214447) passed the build/test/package, security, CodeQL, script, Azure OIDC signing and publication gates. Published checksums, signatures and provenance were verified. See [release signing](RELEASE-SIGNING.md).
- **Installed:** the November 3 check recorded MSI exit 0, app/service/CLI product version `1.0.1+5bf00f3291923f7afb34c6cce61276e69649689b`, file version `1.0.1.65535`, and service **Running**. No new runtime check is implied by this documentation update.
- **Server and contract:** [qbit-printing 1.0.0](https://git.kof22.com/QRun-IO/qbit-printing/releases/tag/v1.0.0), [protocol 1.0.0](https://git.kof22.com/QRun-IO/qqq-print-protocol/releases/tag/v1.0.0) and QQQ **4.1.0-RC.21** are released. Native printing API **2.3**, payload **v1** and WebSocket subprotocol `qqq-printing.v1` retain their own version meanings.
- **Physical scope:** Letter and 4 × 6 output were accepted earlier; the operator confirmed 4 × 1 on RC3 with the same relevant print path. Physical 4 × 2 was deferred and is not a release blocker. Dated evidence remains in [Acceptance](ACCEPTANCE.md).
- **Documentation:** current install, sign-in, settings, media, logs, branding and release instructions reflect the stable release. Ordinary source changes use Gitea; the authorized GitHub signing and updater bridge remains in place.

No product implementation, release publication or installation task remains in this closeout. Shared CI-platform migration is separate from the shipped product. Historical unchecked items below do not reopen cancelled work or authorize physical tests.

## Historical scope decision — 2026-11-02

The post-print native UI layout exception (WinUI `LayoutCycleException` after a print, recovered by the existing mitigation) was removed from the 1.0 blocker list by the user on 2026-11-02 and is accepted as a known limitation. It is **not fixed**, and the history below is preserved unchanged. It is not carried as another mandatory qualification.

## Historical cancelled work, not active

Repeat Auth/S3 qualification, the print-latency investigation, and the MSI comparison/install-free upgrade analysis were cancelled by the user. Their recorded evidence is historical; do not resume them.

---

# Historical matrix — candidate 0.4.0-rc.1 (superseded 2026-11-02)

The sections below were the working matrix on 2026-11-01. They are kept as evidence only and are **stale** where they list work as pending; the current truth is above.

## Observed gates

Historical (superseded heads, kept as evidence): Windows PR #16 head `35ed93c7…` hosted run 41340470827 passed Core 154 and CLI/Windows 317 with 0 warnings; server PR #30 head `4eee28e9…` passed 183 reported / 182 executed / 1 opt-in skip / Checkstyle 0. Protected main then advanced (server rendering at printing 2.2.0; client virtual-agent), so those heads are not the candidate.

Current candidate, local results only:

| Tree | Gate | Result |
| --- | --- | --- |
| Windows | First focused build | **Rejected**: `PrintWorker.cs(96,29)` MA0051 (method 67 lines, maximum 60), caused by the phase-timing instrumentation. Log preserved. An earlier sandbox build had stalled with orphaned build workers; that was environmental, not source. |
| Windows | After extracting `SubmitJobAsync`, `RequireCurrentPrinter`, `RequireSubmitAllowed`, `SubmitNativeAsync` and `JobAttempt` (no analyzer waiver) | Focused negotiation/runtime run **16 of 16 passed**, no warnings, no analyzer errors. Core.Tests was not built. |
| Windows | Roslyn token compare against merge base `c4c663a` | Exactly the four intended semantic files differ (`PrintWorker.cs`, `PrintWorkerTests.cs`, `QqqApiDiscovery.cs`, `AgentRealtimeMediaCapabilitiesTests.cs`); no fifth file. The worker refactor was reviewed: same reservation, staged-file and submission disposal inside the same catch, same authority checks and state order. |
| Server | Full verify, sandbox | 188 reported, 0 assertion failures, 11 loopback setup errors, 1 skip. Environmental; log preserved. |
| Server | Full verify, actual user, before fix | 188 reported, 0 assertion failures, **1 error** (`PrintingRealtimeTest.mediaCapabilityIsReplacedOnEveryAuthenticatedHello`, temp-directory cleanup). Cause: `PrintingRealtimeRoutes.close()` did not wait for in-flight channel drains, which recreated the stopped file database. Log preserved. |
| Server | After `stopWorkers()` fix and regression `closeWaitsForChannelWorkersAlreadyRunning` | `PrintingRealtimeTest` five consecutive runs, 4 tests each, no empty-database warnings. Full verify **189 reported / 188 executed / 1 intentional opt-in workload skip / 0 failures / 0 errors, Checkstyle 0**. Source-header guard over 146 tracked files passed; staged whitespace checks passed. |
| Protocol | Check | 76 definitions and 78 fixtures passed locally; compatibility docs published as a signed draft PR #15 (commit `1916324e6d2c0dac0fd48f2401b654303ab228f9`). |

**Not yet observed:** hosted CI on the final signed candidate heads (Windows Core.Tests, the new `RunOnce_LogsOnePhaseTimingLinePerJobWithoutDocumentContent`, Cli.Tests totals, MSI build at ProductVersion 0.4.0; server verify). Hosted passes do not clear the local Defender quarantine of the Core test assembly, which stays unbuilt and unrestored. No installer, installed-upgrade, physical-output or published-cohort native-callback result exists for this candidate.

Status words are strict. **Proven** names evidence that was observed for the stated scope. **Partial** means part of the requirement has evidence. **Incomplete** means no qualifying evidence exists yet, including where only source or a plan exists.

| Requirement | Status | Evidence on record | Open gap / exact next evidence |
| --- | --- | --- | --- |
| AppKit parity (menu, About, settings, theme, logs, tray, update) | Partial | [APPKIT-PARITY.md](APPKIT-PARITY.md); real in-app 0.3.1 → 0.3.2 update accepted ([ACCEPTANCE.md](ACCEPTANCE.md)); 426-test Release gate at 0.3.2 | Update download/installer failure, cancel and reboot-required paths; high contrast, Narrator, scaling and small-screen matrix. Not re-run for this candidate. |
| Realtime, reconnect, large-file control concurrency | Partial | Source tests: `PauseAndCancelRemainResponsiveDuringLargeDownloadWithoutHttpPolling`, `NativeCookieAndDeviceProofUpgradeKeepsReceivingDuringStalledDocument`, ordering/generation/bandwidth suites; installed 0.3.x reconnect observed ([REALTIME.md](REALTIME.md)) | [TODO.md](TODO.md) still lists the combined runtime review (stalled transfer, lost ACK, reconnect) as unchecked. Needs the hosted Core + Windows gate on the exact head, then an installed reconnect check on a compatible server. |
| Jobs table, details, sorting, 10k/day scale | Partial | `JobSortingTests.TenThousandJobs_AllColumnsSortAcrossPagesAndInterleaveBoundedCloudQueue`; `AgentStoreScaleTests`; 375,000-job store measurements in [SCALABILITY.md](SCALABILITY.md); installed UI checks at 0.1.9/0.3.1 | Measurements are local storage with fake printers. GUI rendering at scale, service scheduling and printer throughput are unmeasured. Not a throughput claim. |
| Printer media (this candidate) | Partial | Read-only real-driver/DC normalization probe 4×6 in / 300 DPI with no queue change ([PRINTER-MEDIA.md](PRINTER-MEDIA.md)); media is offered only to stable printing 2.3+ in major 2 (2.2 rendering stays a three-capability peer), covered by the 16 passing focused negotiation/runtime tests (HELLO per version, reconnect upgrade and downgrade) | The earlier hosted Core pass was on a superseded head. Final-head hosted Core is pending and does not clear the local Defender quarantine. No installed profile-selected print has occurred. |
| Print latency (measured 5–7 s ZPL test print) | Not pursued | Historical: source trace of the per-job path and a diagnostic plan (`LATENCY-DIAGNOSTIC-PLAN.md` in the coordination outbox); the candidate logs one phase-timing line per job | The user cancelled further latency work; the cause stays unproven and nothing is claimed. The log line remains available. |
| Branding and install | Partial | [BRANDING.md](BRANDING.md); branding tests; MSI payload/resource/notice checks recorded for 0.3.2 | Candidate MSI is built only by the hosted job on the final head (pending). Needs MSI resource comparison against the 0.3.2 baseline after CI, then the install-free plan in [UPGRADE-QUALIFICATION.md](UPGRADE-QUALIFICATION.md). |
| Status and log rotation | Partial | `AgentLogSettingsTests` (10k jobs × 3 entries inside the default daily budget); daily-cap marker and clear-logs behavior in [SCALABILITY.md](SCALABILITY.md) | Installed rotation across a local-midnight boundary and cap visibility in the UI are not observed. |
| Native auth, permissions, recovery | Partial | Isolated real Authentik login, permission, logout, expiry/renewal and revocation against an unreleased API 1.9 candidate in local Docker; recovery regressions in [QQQ-SIGN-IN.md](QQQ-SIGN-IN.md) | Installed browser/private-callback and long-lived grant need a compatible published API 1.9+ cohort. The local server 1.8 does not qualify. **Incomplete** until that cohort exists. |
| Retained-source and S3 retention boundaries | Partial | Server local full verify on the merged candidate: 189 reported / 188 executed / 1 intentional opt-in 10k skip / 0 failures, Checkstyle 0; callback authority and accepted-row history fixes independently reviewed; the server's `docs/STORAGE-AND-RETENTION.md` states the layout, outbox-copy and versioning boundaries | Final-head server hosted CI pending. Production IAM, bucket policy, noncurrent-version retention and cross-platform key migration are deployment work, not proven. Echo's experimental core tuple is unqualified; no silent repin. |
| Packaging, CI/CD, notices, About | Partial | Notice and Query About guards merged into both bases; release workflow pins actions and protocol; candidate version inputs (0.4.0 / 0.4.0-rc.1) and the reviewed workflow patch are applied by the coordinator, with action pins unchanged | The protocol pin in `ci.yml` is the final commit `42c0d249f81395f00408ec0f92638dcbbf5deb03` ([Gitea PR 2](https://git.kof22.com/QRun-IO/qqq-print-protocol/pulls/2)); earlier pins are historical. Candidate identity is 0.4.0-rc.2 (ProductVersion 0.4.0). Hosted packaging steps on the final head are pending. MSI remains unsigned. |
| C# source standards (XML how/why headers, member layout) | Partial | License headers on all tracked C# (IDE0073, [SOURCE-HEADER-COMPLIANCE.md](SOURCE-HEADER-COMPLIANCE.md)); how/why headers and structural spacing completed in batches A to D, each token-compared as comment and blank-line only; Roslyn helper controls passed | Source cleanup is complete and merged. A final inventory over the merged tree has not been reported, and the merged UI build and 35 paper/media tests passed locally; hosted CI is pending. |

## Order of closure

1. Done locally: both trees pass their focused/full local gates (table above). Pending: signed functional commits and exact-head hosted CI for the Windows and server candidate heads.
2. Done: the Roslyn inventory/compare helper passed its positive and negative controls.
3. After the functional baseline is committed: documentation-only C# standards batches of at most 8 files, each with token-equivalence proof and never mixed with semantic fixes.
4. Version inputs (`Directory.Build.props`, `build/Build-Installer.ps1`, README, CHANGELOG) and the `ci.yml`/`release.yml` patch are applied for the candidate. No tag.
5. Normal Gitea Core/CLI/build CI on the integration head, then installation of the new label implementation and a small-stock paper test.
6. Historical, no longer active: the install-free upgrade plan in [UPGRADE-QUALIFICATION.md](UPGRADE-QUALIFICATION.md) and the latency diagnostic plan were cancelled by the user; their recorded evidence is kept.

## Not claimed (historical, 2026-11-01)

Authenticode signing, physical-stock sensing, installed profile-selected printing, desktop/native OAuth callback or long-lived grant on a published cohort, remote cloud deployment, broad driver matrix, and any 1.0 readiness.
