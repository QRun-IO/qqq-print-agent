# Session state — 2026-10-31

## Installed realtime release and companion follow-up

All three 0.3.0-rc.1 releases are published from protected main. The Windows
release at `213ba02769378a9f82881d7f0abd0012a8747e5c` passed 402 tests and
the required build, packaging, security and provenance checks. The published
MSI was installed with paired identity, history, printer choices, metadata and
settings preserved; automatic restoration after a service restart passed.
Fresh native QQQ laser PDF and Zebra ZPL tests reached SPOOLED. The pending
Zebra PDF intent must not be confused with a submitted job or paper confirmation.

The operator then encountered a whole-window freeze after a successful test
print. A captured AppKit log confirms a WinUI LayoutCycleException; the managed
stack shows no blocked pipe call. Companion-only recovery restored navigation
without restarting the service. Saving printer settings without changes also
remained responsive. The exact layout trigger is still under investigation with
an isolated, nonprinting native WinUI test host; no layout fix is yet claimed.

The active follow-up branch adds explicit WebSocket connection/traffic status
and corrects startup-task identity comparison: Windows can return an account
name for a trigger registered with a SID. Acceptance must resolve to that same
SID, preserving all other ownership checks. The startup section controls the
tray companion; the automatic background print service is independent.

The 0.3.1-rc.1 source gate passed 414 Release tests (145 Core, 269 Windows/CLI),
12 headless presentation tests, and a zero-warning/error WinUI build. Branding
dry-run and local MSI packaging also passed. The isolated native host passed
the real expanded-row/dialog/timer/feedback/navigation flow, live meter decay,
retained totals and the server-settings link using synthetic traffic only.
The corrected startup validator also accepted the unchanged actual Windows task.

Independent review added a guard around native exception metadata access. The
fatal-layout policy leaves AppKit's existing decision untouched if inspection
fails; only the exact native LayoutCycleException permits normal termination.
Native fatal-termination execution remains unqualified because investigative
probes encountered an unrelated Windows activation failure. Those experimental
probe modes are excluded from the committed regression host. Publication,
installation and actual in-app update acceptance of 0.3.1 are still pending.

The candidate notes below preserve the implementation history.

## Realtime and Jobs UI source — 0.3.0-rc.1 release candidate

Current source adopts printing extension 2.0 and immutable protocol commit
`df3729ac632d4a34a3fb8423acef465042a624fc`: client-initiated authenticated WSS,
direct controls/events/receipts, event-driven finite claim drains and independent
streamed HTTPS content. No job/control polling fallback is permitted. Complete
server policy includes configurable document, copy, queue, concurrency, bandwidth,
liveness and timeout limits, including native submission. README and current
architecture/operations guides distinguish this source from published 0.2.

Jobs UI changes add inline expansion and reject stale detail completions across
collapse/reopen, filtering, paging, navigation and closing. Focused Debug x64
application and UI-test builds passed with zero warnings/errors using existing
project references; three headless tests of the actual generation tracker passed.
Desktop assertions were compiled, not executed. Full coordinated consumer and
installed realtime acceptance remain separate; no runtime or physical printing
was performed by this documentation/UI lane.

The 0.2 and earlier entries below retain their original checkpoint wording and
test counts; they are not current realtime publication or deployment claims.

## Installed published 0.2 happy path — user confirmed

The published 0.2 MSI installed with pairing, printer exposure and journal history
retained. Native QQQ table-based sign-in, existing-device association, all four
dispatch/connection actions and inventory refresh succeeded. The user confirmed
the Letter PDF and 4 × 6-inch Zebra ZPL printed correctly and manually confirmed
the app's Connected footer and both printers. Restart elevation was canceled
before execution. This is local 0.2 evidence; no installed 0.3 realtime/Jobs UI,
Authentik renewal or remote-production acceptance is inferred.

## Explicit credential and pairing recovery — not released

Latest isolated gate: **365 tests (127 Core + 238 Windows/CLI)**, zero warnings/errors/skips, including the final brand-neutral UI. Evidence: `pending-setup-verify.log`, `TestResults/verify-073522764d7e43eaaab76bab59199907`. Pending unbound sign-in now survives restart when the retained device proof is unreadable or belongs to another server, without replacing that file or allowing claims. Bound saved identities still fail closed for either condition before creating a network client. Both pending cases were reproduced before correction; successful explicit enrollment and preservation of old journal/exposure are covered. Earlier gates below remain historical checkpoints; no installed state or printers were touched.

The isolated recovery follow-up passes the full solution/UI gate: **360 tests (127 Core + 233 Windows/CLI), zero warnings/errors/skips**. Evidence: `recovery-structure-verify.log` and `TestResults/verify-4f3b5f3febf64e90bf48bd45cbbf4b4e`. Both original failures were first reproduced: unreadable QQQ session data prevented re-login, and unreadable device proof prevented sign-in before enrollment. Fresh authenticated sign-in now permits recovery, preserves unreadable ciphertext, and still requires server-authoritative device binding or explicit new enrollment. Denied binding leaves the original data untouched. Decryptable but structurally invalid session data is also rejected and recoverable; stored origin and identity fields are validated before use. Sign-out and read-only status remain usable; scoped journal and printer exposure are retained. This is synthetic verification only, with no installed-state changes or physical printing. The preceding 356-test candidate remains historical evidence.

The revoked-pairing follow-up now passes **361 tests (127 Core + 234 Windows/CLI)** with zero warnings/errors/skips: `new-pairing-verify.log`, `TestResults/verify-c27f72e1913549f88eb48f47135e4863`. A standard three-choice pairing selector keeps reuse as the default and adds explicit replacement setup. Saved unbound sign-in survives restart without presenting old revoked proof or claiming jobs. A denied default binding or rejected enrollment preserves old configuration; successful enrollment uses a new journal scope and preserves exposure/history. Final brand-neutral label edits passed a separate UI build in `new-pairing-ui-labels.log`; runtime and test sources were unchanged. UI automation was compiled, not executed against an installed application.

## Isolated remote-control implementation — not released

The device-controls worktree adds independent control polling, persisted desired dispatch/connection state, connection-scoped command receipts and verified single-job spool cancellation. Pause stops new claims; administrator disconnect keeps control polling and existing print-event delivery alive while suppressing ordinary synchronization. Reconnect does not restart Windows, the service or an in-flight print. Authorization failure requires explicit local recovery and never erases the print journal. The companion displays desired state, control contact and the last remote action/outcome.

The reviewed isolated solution/UI build passed without warnings or errors, and all **279 tests (72 Core + 207 Windows/CLI)** passed without skips. Verification evidence is in `TestResults/verify-211a2eed18b04373858451e6a1f59070`; the command used `build/Verify.ps1 -ProtocolFixtures` with the coordinated protocol worktree. Tests cover blocked printing, disconnect/reconnect, lost receipt and native cancellation responses, duplicate delivery, interrupted execution, expiry, ownership, revision rollback, authorization denial, legacy negotiation and receipt retirement. Independent review identified two corrected contract issues: generated receipts now use literal `Z` UTC timestamps, and omitted desired-state fields cannot silently resume dispatch. No installed service, live server, printer or enrollment has been changed by this work.

Native QQQ authentication integration now passes the combined solution/UI gate: **356 tests (127 Core + 229 Windows/CLI), zero warnings/errors/skips**. Evidence is in `TestResults/verify-01fc65d1e3d742ceac9b69463a75b555` and `native-auth-verify-final.log`. Independent source review findings were corrected and rechecked: malformed input/timeout handling, session-generation races, constructor-failure gate release, installed callback location and visible browser cancellation. Runtime tests also cover temporary resume failure recovery and pre-enrollment session restoration.

The actual packaged Core client also passed **23 interoperability checks** against a disposable native QQQ server with table-based authentication: discovery, sign-in, enrollment/binding, session resume/restore, pause/replay/observed polling, anonymous refusal, logout and old-cookie refusal. No print jobs or printers were involved. The unsigned candidate MSI built and passed version/resource/registry/service inspection. Neither proves an installed callback, real Authentik deployment or indefinite session renewal.

The implementation includes discovery-first sign-in, protected service session storage, periodic standard resume, authoritative principal/tenant/device checks, explicit legacy-device association, and native printing transport. Native control 404 cannot fall back to private bearer routes. Settings exposes sign-in/sign-out and current identity; the installed private-scheme callback forwards only to the active companion attempt, where Core validates PKCE/state. Normal CLI commands use the service; direct bearer diagnostics require explicit `--legacy-compat`. See [QQQ sign-in](QQQ-SIGN-IN.md) for setup and remaining installed/provider qualifications. The preceding 279-test gate is historical controls-only evidence. Barcode diagnostics are explicitly outside this printing change.

## Published 0.1.9-rc.1 — 2026-10-31

[v0.1.9-rc.1](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v0.1.9-rc.1) is published from protected main revision `cf0e362`, following the audit merge. [Release workflow 41190815995](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41190815995) passed; all four assets, their checksums/digests and their build provenance attestations were verified. The MSI remains unsigned.

The exact CI installer from reviewed source `13aa84c` completed with MSI exit 0 and no restart requirement. Both installed management peers authenticated, protected credentials and journal history were preserved, and the service was confirmed running and connected. All **16 installed UI checks passed in one run**, and the companion opened successfully. No physical printing was requested. The source gate passed **255 tests (66 Core + 189 Windows/CLI)** with zero warnings/errors and none skipped; all ten required hosted checks passed. The installed CI MSI and published release MSI have different hashes. Runtime sources are unchanged, but no separate installation of the release MSI is claimed. See [artifact hashes and acceptance scope](ACCEPTANCE.md).

Local QQQ integration was verified with preserved pairing and history. Remote deployment, production S3/IAM, Windows reboot/recovery, real updater handoff, hostile-tree installer rollback and publisher signing remain separate qualification work. Historical physical output below does not establish new 0.1.9 hardware acceptance.

## Historical pre-install 0.1.9 checkpoint — 2026-10-31

The original upgrade installed 0.1.8 with MSI exit 0 and no restart requirement. Credentials and the journal were retained; both the old and new installed management peers authenticated. A later history query failed because the service disconnected before the client read the complete response. Unassigned helper fields did not establish a disconnected service or missing printers; independent server inspection confirmed service health and no pending or uncertain work.

The failure was reproduced on an isolated random pipe using the actual runtime with fake printers and no network/configuration. All eight delayed-reader cases failed before the fix and passed with the bounded client-close wait. Permanent tests cover current/legacy readers, success/error responses, subsequent clients, deadline expiry and runtime shutdown. The correction preserves the wire format and existing deadlines. Independent source review found no concrete issue. The isolated combined 0.1.9 solution/UI build passed with zero warnings/errors and **255 tests (66 Core + 189 Windows/CLI), none failed or skipped**. Source and protocol fixtures remained byte-identical after verification. PowerShell analysis, packaging-input validation, workflow lint and changed-document lint also passed. Hosted and installed qualification were pending at this historical checkpoint; their completed results are recorded above.

The related server review completed its approved H2 license record and [standards configuration repair](https://github.com/KofTwentyTwo/standards/pull/54). The local database cutover subsequently preserved pairing and history. This work did not provision a remote cloud/S3 deployment or qualify new physical output.

## Historical audit candidate 0.1.8-rc.1 — 2026-10-30–31

Independent baseline scan and fresh source review are complete. The Windows fixes authenticate the installed pipe peer before sending commands, retain pipe ownership, reject unsafe machine state across all privileged entry points, and protect startup task definitions. Reviewer and packaging findings were corrected. The isolated full gate passed **249 tests (66 Core + 183 Windows/CLI), zero warnings/errors/skips**; updated installer sequencing passed its focused regression. All 12 PowerShell tests and all 75 analyzer rules passed.

The unsigned CI candidate MSI SHA256 is `CB5C2F6F17246C6DC3FD3D746B9FC82D47AE956940F67A4697CFD7677B3D40B9`, from passing build `41151199441` at source `2e6a2e5`. Every required build, security, CodeQL, script and PR check passed. Actual Windows Installer safe-session formatting and sequence inspection passed; all 995 surviving file targets retain their component identities and key-path roles versus 0.1.7. No installer actions were executed by those inspections. This CI artifact supersedes earlier local candidate hashes.

At this checkpoint the published baseline was **0.1.7-rc.1**; 0.1.8 was an audit candidate and was superseded before publication. Its local installation and incomplete post-install check are recorded above. Its helper requested no physical printing and did not run desktop qualification. The later 0.1.9 audit merge, publication and installed UI results supersede that pending work; hostile-tree rollback, actual updater handoff, reboot/recovery and remote deployment remain unqualified. See [audit evidence](AUDIT-2026-10-30.md) and the reviewed [workflow compatibility decision](CI-COMPATIBILITY.md).

Retained originals and printing delivery copies have separate storage and retention; their typed integration remains pending.

Additional synthetic storage qualification used the packaged 0.1.8 implementation at 375,000 jobs and 1,109,254 events. All sampled totals/page bounds and SQLite integrity checks passed. Appending 10,000 jobs through production journal APIs took 210 seconds using one store connection; first/final 50-row pages measured 67/165 ms medians, while ordinary page allocations remained about 71–83 kB. Retention removed exactly 250 jobs' detail in 10 ms, retaining summaries. This is local storage/query evidence, not CI-MSI binary identity, service scheduling, GUI or physical throughput qualification. See [method and limits](SCALABILITY.md). Installation/release guidance now distinguishes updater SHA-256 checks, current workflow provenance attestations and unsigned MSI publisher identity; older releases are not retroactively attested.

## Historical 0.1.7 publication and physical acceptance — 2026-10-30

Installed **0.1.7, source 93dd5fc**, completed with MSI exit 0 and no reboot, retaining credentials. Management, computer profile, paged history, log settings, branded shortcut and connected-state checks passed. The source gate passed **227 tests (57 Core + 170 Windows/CLI)** with zero warnings/errors; [exact-source CI 41139885687](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41139885687) passed. All **16 installed UI checks passed in one run**, including the diagnostic media/DPI dialog, which was closed without printing during the UI test.

The user confirmed the laser PDF and Zebra ZPL output. The original Zebra PDF reached SPOOLED but produced no label. Windows machine **Printer properties → Advanced → Printing Defaults** was configured for **4.09 × 2 inches / Thermal Transfer**, while the installed stock is **4 × 6 inches and direct thermal, without ribbon**. The defaults were changed to **4.00 × 6.00 inches / Thermal Direct**, preserving other options; the saved machine DEVMODE dimensions were verified as 1016 × 1524 tenths of a millimeter. The printer was also returned to direct-thermal mode and unpaused without producing output during that correction.

One fresh reviewed PDF was then submitted through QQQ and the installed service. It reached spool receipt 9, the hardware counter advanced from 492 to 493, and the printer remained direct thermal and unpaused. **The user confirmed the replacement PDF label printed fully and correctly.** No application-code fix was needed. Barcode scanning and measured alignment remain unverified; the earlier spool-only result is not retroactively counted as successful physical output.

[Release workflow 41140882339](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41140882339) succeeded. [v0.1.7-rc.1](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v0.1.7-rc.1) is published, and its MSI, SHA256SUMS and two branding assets were verified. The MSI remains unsigned. The built-in generator uses QQQ branding and embedded warehouse-readable characters; the UI exposes media/DPI choices. All initial content hashes matched reviewed previews, but the initial Zebra PDF was a configuration failure despite SPOOLED. This was local paired-server acceptance, not remote-cloud deployment qualification.

## Historical checkpoint: 0.1.6 published and Zebra submissions accepted

Public v0.1.6-rc.1 release assets are verified. The original paired QQQ database was restored on loopback port 18081 without changing device credentials; read-only inspection found no pending jobs before startup. The connection is healthy. A Zebra ZT411 at 300 dpi, loaded with confirmed 4 × 6-inch stock, is exposed locally, enabled in QQQ and READY. Exactly one ZPL shipping test and one PDF barcode/alignment test reached SPOOLED. Physical output and barcode scanning are pending user confirmation. The service remained Running throughout this session. This is local end-to-end protocol evidence, not a deployed remote cloud test. No production code changed.

### Installed acceptance before the Zebra session

Version 0.1.6-rc.1 from d9033aef951c569748fe9d610ea08ab59be31d02 is installed. The MSI returned 0 without a reboot and retained credential bytes. Service restart, management pipe, computer profile, bounded job pages, log settings and branded shortcut checks passed; four printers and two retained jobs were returned. MSI SHA256: 54CDC0A68DEF13AF3F97A78CB556A003275C25C41489408AF42A31F2CD60830C.

The full source gate passed 211 tests (42 Core and 169 Windows/CLI), locked restore and zero build warnings/errors. Exact-source CI 41131558158 passed, including branding validation and MSI packaging. Installed UI checks initially passed 14 of 15. The printer double-click test selected arbitrary text in an expanded row; its target was corrected to the compact header without changing production code. The focused rerun passed, so all 15 checks have passing results across the two runs. Sorting defaults/toggles, inline details bounds, both double-click dialogs, wide printer editor, menus/logs/preferences and tray lifecycle were exercised. No new physical print or visual/Narrator certification.

The service was later observed stopped with exit code 0; available event/log evidence did not establish why. A normal-UAC restart succeeded and the focused test ended with the service Running. The connection was offline at that checkpoint; the newer local QQQ restoration is recorded above. The cause of the earlier clean stop remains unknown.

Private evidence: cloud-printing-integration/runs/audit-upgrade-016/result.json, ui-results, and focused-check/result.json/test.log. Windows v0.1.6-rc.1, protocol and qbit-printing v0.1.2-rc.1 releases/assets are verified. Older checkpoints below are historical.

## Historical failed upgrade and recovery checkpoint — 2026-10-30

Attempted0.1.5 installation (b45a906,MSI82A6C8B283612CD45238820F38BAEEBBE2AA76003833FA3E5FAAD3BC2991EE79) failed1603 in ApplyBranding. The earlier MSI MajorUpgrade schedule removed0.1.4 outside the rollback transaction, leaving its service/app absent. Protected device state and pre-upgrade backup remain. Recovery uses the verified installer artifact from passing CI41076156859 under cloud-printing-integration/runs/recovery-014/verified-ci; Restore-014.ps1 was launched through normal UAC, but Windows canceled elevation before it started. The user has been asked to reply retry for a fresh prompt; no elevation is now pending. Do not claim installation/recovery complete until result.json confirms it. Root owns recovery/install. The corrected source passes204tests (39Core+165Windows/CLI): Start menu checks allow deletion-only inherited user grants while rejecting content writes, and MajorUpgrade now uses afterInstallInitialize. Build outputs are isolated per numeric version to preserve prior recovery installers. Remote CI for b45a906 failed one timing-based heartbeat assertion; its corrected test waits for observed traffic while retaining the no-claims pause invariant. The strict protected updater-data ACL policy remains unchanged.

## 0.1.5 source gate — 2026-10-30

New tables/profile/log/branding scope passes the full Windows gate:196tests (39Core+157Windows/CLI), locked restore, zero compiler/analyzer warnings/errors. Branding WhatIf validation and PowerShell analysis pass without machine changes. Server22tests/zeroCheckstyle and protocol28definitions/21fixtures pass; downstream CI pins protocol a0de93b. Synthetic scale evidence is in SCALABILITY.md. Installer packaging and installed desktop acceptance are still pending; see the newer recovery checkpoint above for machine state. No new physical print.

**0.1.4-rc.1 is installed and opened**, source **80e253f**. File/Help menus restore Settings, Close/Exit, logs/help links, Check for Updates and About. The pinned H.NotifyIcon Icon property disposes previous values, conflicting with our cache; native WinUI reproduction failed on red→green→orange→red, then passed using the non-owning UpdateIcon API with valid handles and Windows registration. Installed PNG assets render correctly. This establishes a lifecycle defect; the user subsequently confirmed the tray icon works. Older debug companions were closed during this upgrade.

Full source gate passed163tests/zero warnings and exact-source GitHub CI passed. MSI returned0, no reboot required; credentials retained, service restart/pipe verified, four queues, cloud snapshot still offline. Local MSI SHA-256: `E1AF2438F24DC99243C0E71AF4126072DACD9B390E0AB56B7E8619AF50B03B19`. Installed UI run: **10 passed / 1 failed**; menu/About, hide/relaunch, splash and other shell checks passed. Only log-window discovery timed out. Test command dispatch was subsequently changed to prefer Invoke, then SelectionItem, then mouse; this test-only change compiles cleanly and Windows canceled elevation for its focused rerun, so that rerun did not execute. The installed 0.1.4 app and service remain running. Private evidence: `runs/audit-upgrade-014`. No new physical print or actual updater apply.

## Earlier installation evidence

Installed target: **0.1.3-rc.1**, including the startup correction, functional audit fixes and shared AppKit parity. Source commit **7344914** passed **36 Core + 127 Windows/CLI tests (163 total)**, locked restore and a zero-warning/error Release build. GitHub CI for that exact commit also passed, including MSI packaging. User renewed installation authorization; Windows elevation succeeded and MSI returned 0, with no reboot required. Enrollment credential bytes were preserved, service restart and management pipe passed, and four printer queues were returned. The initial cloud-connected snapshot was false; a live QQQ connection is not newly qualified. Root owns remaining acceptance and publication.

Final local installer: `artifacts/installer/QqqPrintAgent-0.1.3-x64.msi`, SHA-256 `808869F507D0B87E67546F41940A4A25D059E89E655E9127635299D025EBF737`. Private integration evidence is in `runs/audit-upgrade-013/result.json` and `ui-results`.

Installed desktop tests completed **5 passed / 5 failed**. Preferences/help, theme persistence, connection validation, navigation/footer and job search/service controls passed. Tray hide/relaunch, log-content identifier, splash and About checks failed; the log identifier is absent in pinned AppKit 0.1.0, hidden-window UIA needs native visibility checks, and About encountered Windows SendInput access denied. These failures are not a passing UI acceptance gate. The companion was opened after the tests and the service remains running. Computer-use capture reported a higher-integrity limitation, so it did not establish visual acceptance. Test-only corrections now compile with zero warnings/errors: HWND visibility/ownership, pinned AppKit log controls, UIA navigation selection and a longer isolated splash observation window. They have not been rerun. No new physical print or actual in-app update was attempted.

## Implemented source

- AppKit `UpdateCoordinator` and native prompts use `MsiUpdateService` for manual checks and the persisted startup-check preference. Version/channel, splash duration, theme, project/help/release/support links and dependency credits are present.
- The fixed QRun-IO GitHub feed permits newer stable versions for stable builds and newer RC/stable versions for prerelease builds, with a minimum core version of 0.1.3. Same-release SHA-256 checks establish consistency, not publisher signing.
- Protected ProgramData staging, path/reparse/ACL checks and a copied self-contained CLI helper apply one MSI to service, companion and CLI with administrator rights and explicit confirmation. No privileged persistent updater or UAC bypass is added. Claim pause preserves heartbeats; results/install logs persist and relaunch is attempted. Completed stages remain for administrator review.
- Native Printers/Jobs/Settings navigation, one shared persistent redacted AppKit log viewer, status tray, single-instance activation, close-to-tray and explicit exit.
- Service controls/install guidance, explicit administrator opt-in sign-in task, bounded SCM recovery, reconnect and timeout/pipe recovery. Independent heartbeats continue during slow printing; stale/EOF snapshots do not imply healthy service state.
- Connection-scoped credentials/outbox/history, restart recovery without duplicate printing and explicit unreadable-credential repair preserving encrypted backup/evidence.
- Friendly job identity, searchable/filterable history, event details, persistent failure review and safe detail retention preserving deduplication summaries. Server-mediated test/retry/cancel actions include cloud-queued jobs before local receipt and identity-checked spooler cancellation.
- Bounded isolated native helpers and orphan cleanup, supported-queue filtering, correct health flags, foreground CLI identity/locks and runtime branding contrast validation.

See [APPKIT-PARITY.md](APPKIT-PARITY.md), [FUNCTIONAL-AUDIT.md](FUNCTIONAL-AUDIT.md), [UX-REVIEW.md](UX-REVIEW.md) and [TODO.md](TODO.md) for source evidence and acceptance scope.

## Historical evidence and installed state

Public v0.1.0-rc.1 has a known WinUI resource packaging defect and warning. The later 0.1.1 MSI installed successfully, retained the exact encrypted credential bytes and restarted the service, but installed desktop tests exposed a native startup failure: NavigationView SettingsItem was accessed before its template created it. The correction defers that access until Navigation.Loaded and guards it. FlaUI launch handling now retains the process handle for native exit diagnostics.

The corrective 0.1.2 source passed 102 tests and built an unsigned MSI; its UAC installation was canceled. That left0.1.1 installed until the successful0.1.3 upgrade recorded above; the0.1.1 result is historical and is not working companion acceptance. The 0.1.3 target includes the correction and additional update/parity work. Inspect current local processes/service state before further actions; do not infer installed versions from source or old artifact hashes.

Earlier development and temporary installed-service laser pages were confirmed by the user. No fresh physical print has been performed for the latest corrections. Zebra is deferred. Reboot/sign-in/recovery, real update cancellation/relaunch and publisher signing remain pending. Do not report a canceled/pending elevation as a completed upgrade.

QQQ `qbit-printing` 0.1.1-rc.1 verification passed **19 tests with zero Checkstyle violations**, including native record/table job controls. Synthetic Windows update/native-child tests do not invoke an installer or access physical printers.

## Definitive realtime source verification — 2026-10-31

Product0.3.0-rc.1 requires native printing API2.x (framework/wire/subprotocol remainv1), pinned shared contract df3729ac632d4a34a3fb8423acef465042a624fc. Locked restore and entire Release solution build passed with zero warnings/errors. Definitive fulltests399PASS:140Core+259Windows/CLI. Earlier UIbuilds and three headless detail-generation regressions passed; no automated desktop acceptance is claimed.

Actual native-session WebSocket runtime tests cover stalled25MiB transfers with concurrent controls, exactACK replay/reconnect with no duplicate synthetic submission, stickyCLAIMED admission held throughRECEIVED commit, per-printer order/different-printer parallelism, aggregate bandwidth, live concurrency/limits/deadlines, policy/auth generation fences and startup unreadable-credential recovery with a functioning synthetic management pipe. Actual.NET/Java API2 integration separately passed13 checks using this unchanged ReleaseCore, including25MiB verifiedstream, replacement, receipt replay and sign-out denial. Protocol71definitions/73fixtures and server118tests pass. Current installed0.2 remains unchanged. Package/hostedCI/deployment and physicalqualification for0.3 remain separate; prioruserconfirmed0.2 outputs are not0.3 evidence.
