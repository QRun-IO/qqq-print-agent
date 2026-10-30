# Threat model: QQQ Print Agent

Installed baseline: 0.1.7-rc.1; audit source changes pending release. Reviewed: 2026-10-30. This is an implementation-level inventory. The separate independent deep scan has not yet returned its findings.

## Boundaries and assets

The QQQ server supplies assigned document bytes over HTTPS. The Windows LocalSystem service validates document length/hash, records submission state, and calls OS renderer/spooler APIs. A locally elevated AppKit companion controls enrollment and printer exposure over an administrator-only named pipe. Local credentials, job IDs/events, Windows queue access and physical output are the protected assets.

| Boundary | Implemented control | Residual risk / qualification |
| --- | --- | --- |
| Internet to device | Outbound HTTPS, normal certificate validation; loopback-only HTTP development exception | Proxy/custom trust deployments not qualified |
| Device to server | Dedicated bearer credential; server enforces device/job scope | A compromised trusted server can supply hostile print content |
| Local UI to service | Pipe ACL permits SYSTEM/admins; narrow command set; bounded newline framing and cancellable reads/writes | Local administrators already control the machine; source regressions passed, installed audit acceptance pending |
| Exposure selection | Service-owned persisted allowlist; discovered-ID validation; filter inventory and recheck submission | Removal cannot retract data already accepted by Windows |
| Credentials at rest | DPAPI CurrentUser under service identity; restricted ProgramData ACL | Machine/service compromise defeats local protection |
| Payload download | 20 MiB cap, declared length and SHA-256 validation; 60-second body deadline; separate 2 MiB control-response cap | Integrity does not sanitize PDFs or printer languages |
| Rendering/driver execution | Native Windows PDF/GDI/spooler APIs; bounded render dimensions | Disposable child with bounded timeouts and parent-exit cleanup; child retains service privileges, so this is fault isolation, not a security sandbox |
| RAW ZPL | Explicit compatible queue, trusted server workflow | ZPL can contain printer-control commands; not a sandboxed format |
| Duplicate output | Persistent receipts and events; uncertain submissions require review | Database and physical printer cannot share a transaction |
| Tray snapshot/logs | No device credential or document bytes; bounded recent jobs/logs | IDs/errors remain operational data and must be reviewed before sharing |
| Software distribution | Public source, pinned actions/dependencies, audited locked restore, checksums | Initial installer is unsigned; no attestation/production signing claim |

## Data handling

The service owns `connection.dpapi`, `journal.db` and its WAL. The MSI retains state through uninstall/reinstall. Revoke/decommission the device and resolve pending jobs before deliberately deleting retained state. Only delivered terminal detail whose failures were reviewed is pruned; final-state evidence is retained. Never publish raw state or customer documents.

Service and companion persist serialized, credential-redacted AppKit logs with 30-day retention; recent job snapshots come from durable connection-scoped metadata. Tray colors describe observed connectivity/queue state, not a guarantee of physical printer readiness or completed output.

## Follow-up

Qualify real in-app upgrades and broad hardware/driver behavior. The user accepts unsigned prereleases for now. Regression coverage includes interrupted jobs, credential recovery, retention, operator actions, bounded IPC and bounded child-process termination. Revisit this document when any network, privilege or persistence boundary changes.

Runtime branding is presentation-only, loaded from the existing administrator-restricted ProgramData directory. JSON size, text length, color syntax, HTTPS support links, and local PNG file size/dimensions are validated. No remote logo fetch occurs; malformed settings fall back to defaults. Machine identity, credentials and authorization are unaffected.

Sign-in startup requires an explicit administrator preference in the installed companion. The task uses that administrator's interactive identity and a fixed Program Files executable/arguments; it stores no password. Same-name foreign tasks are refused. Full uninstall deletes only exact owned definitions; upgrades preserve them. Device operator actions remain scoped to authenticated device jobs and approved/exposed printers; reprint creates a new job and requires duplicate-risk consent for uncertain/spooled originals.


## Machine-MSI updates: implemented source, installed acceptance pending

The current audit source gate passed **237 tests with zero warnings/errors**. The installed 0.1.7 companion passed all 16 desktop checks before this audit. The AppKit `IUpdateService` adapter, `UpdateCoordinator` integration, fixed-feed client and copied apply helper are implemented, but direct MSI upgrades and synthetic updater tests do not establish a successful real in-app update handoff. That qualification remains pending.

### Implemented flow and trust

1. The companion performs a user-requested check or an enabled startup check through AppKit's coordinator. Checking does not authorize installation; apply requires the coordinator's explicit offer/consent.
2. The release client queries the fixed `api.github.com/repos/QRun-IO/qqq-print-agent/releases?per_page=100` feed. Runtime customer branding, QQQ server address and user settings cannot replace this update authority.
3. The release client selects a newer eligible version, rejects releases below the minimum 0.1.3 baseline, ignores prereleases for stable installs and allows newer prerelease/stable versions for prerelease installs. It validates the repository release-asset path, version and expected filename, uses HTTPS and allows only expected GitHub/CDN redirects.
4. The release client bounds release metadata to 2 MiB, checksum text to 64 KiB and MSI content to 256 MiB. It writes a uniquely created partial file, validates SHA-256 against that release's `SHA256SUMS`, and only then renames to the staged MSI. Cancellation/failure cleans only the operation's own temporary file.
5. The adapter supplies a staging directory beneath the existing SYSTEM/administrator-restricted ProgramData tree. The client rejects a reparse-point staging folder; adapter/helper validation also checks relevant parent paths and rejects user-writable or substituted destinations. The update backend alone does not establish installed-build eligibility or ACL correctness.
6. A protected CLI apply helper, invoked with fixed structured arguments, revalidates the staged artifact immediately before applying one coherent MSI for service, companion and CLI. The helper's path, staging containment, checksum and release identity are validation boundaries, not assumptions derived solely from a prior UI check.
7. Windows Installer performs the machine update. Elevation stays explicit and subject to Windows/UAC and administrator policy. There is no persistent privileged update daemon, generic remote command endpoint, scheduled silent installer or UAC bypass. Canceling elevation leaves the existing installation and job evidence in place.

### Threats, controls and residual risk

| Threat | Implemented control | Residual risk or acceptance |
| --- | --- | --- |
| Feed substitution via branding, server configuration or arbitrary URL input | Fixed QRun-IO repository and expected asset paths; no configurable update feed | GitHub repository/release authority remains trusted. Review exact redirect allowlist in final code. |
| Corrupt or mismatched MSI | Same-release bounded SHA256SUMS lookup, streaming size limits, verified staging, helper-side revalidation | Checksums and MSI obtained from the same authority prove consistency, not independent publisher identity. A compromised release authority can replace both. Signing/attestation is not claimed. |
| Downgrade or reinstall of known bad packages | Newer-version/channel comparison and minimum offered version 0.1.3 | Verify prerelease ordering, ambiguous/missing assets and unexpected version formats with fixtures. |
| User-writable staging, junction/symlink substitution or time-of-check/time-of-use swap | Restricted ProgramData, unique CreateNew partial files, path/reparse/ACL checks, protected helper and final hash check | A local administrator can already control this machine. Do not accept a user-provided path merely because its string begins with ProgramData. Installer execution must use the verified artifact. |
| Arbitrary privileged process launch | Fixed installed CLI/helper, expected operation, structured arguments and Windows Installer target; no server-controlled executable/arguments | Verify invalid helper input fails closed without invoking an installer. Runtime branding must not alter helper or install paths. |
| Split component versions or loss of uncertain-job evidence | One MSI updates service/UI/CLI; retain credentials/journal and stop/restart according to installer lifecycle | Running helper/payload file locks, active submission interruption, repair/rollback and reboot-required outcomes need actual upgrade qualification. Do not delete receipts to make an upgrade succeed. |
| Silent privilege escalation or unconsented apply | AppKit offer/consent plus Windows administrator/UAC policy; no background privileged update service | Test declined offer, canceled UAC and insufficient privileges. Startup preference authorizes checks, not automatic installation. |
| Update failure destabilizes printing | AppKit update failures return/display values; service remains independent; bounded network/download work and owned-file cleanup | Verify no overlapping manual/startup apply and honest UI handling when restart/apply does not complete. |
| Diagnostic or credential leakage | Update logs contain version/state and sanitized failure messages; never device tokens, enrollment code, document bytes or local journal exports | Protected installation logs can contain local paths; review before sharing. |

### Required update evidence

Unit/integration fixtures must cover fixed-origin/asset validation, channel/version policy, size/hash mismatch, redirect rejection, canceled download cleanup, reparse/untrusted path rejection and failed helper/apply outcomes. Installed acceptance must cover explicit UAC cancellation, blocked/in-use files, coherent version replacement, retained credentials/deduplication evidence, restart and Windows Installer reboot-required results. No updater operation is considered accepted merely because the AppKit interface compiles.


### Apply-helper source checkpoint

`MsiUpdateInstaller` now copies the entire installed self-contained CLI into the protected update session's helper directory before handoff, avoiding a running helper's lock on product binaries. Source validation walks existing path components, rejects reparse points, checks trusted ownership and effective write ACLs (including generic write/all rights), and separately checks copied descendants. Normal inherit-only CREATOR OWNER rules do not count as direct write grants on the parent object.

The bounded request captures MSI hash/version and the exact parent companion PID/start time. The helper must be running from that staged copy, requires an administrator, and waits at most 30 seconds for the expected installed companion to exit; it never kills it. A verified MSI read handle disallows writes/deletion while Windows Installer runs. Installer arguments use structured `/i`, `/passive`, `/norestart` and a protected `/L*v` log path; the helper waits for Windows Installer without terminating it.

Exit 0 is accepted only when the actual installed companion ProductVersion matches the requested release, ignoring build metadata. Exit 3010 explicitly requires Windows restart and is not reported as fully active; cancellation and other failures are distinct. The helper atomically records a bounded, dated `updates/last-result.json`, writes the shared AppKit log, attempts to clear maintenance pause, and attempts to relaunch the fixed installed companion. The UI can read the last outcome after handoff, when the original coordinator no longer exists. If relaunch fails, protected result/install logs remain for diagnosis.

The runtime's update pause is bounded and keeps heartbeats alive; abandoned handoff does not permanently stop claims. Failed pre-handoff stages are cleaned by the adapter after path checks. Completed stages/helper copies/install logs are retained for administrator review; automatic update-stage retention is not claimed. Pure tests cover request/path/ACL/hash/exit/version policy without invoking an installer; the gate owner reported all 163 tests passing (36 Core + 127 Windows/CLI), locked restore and zero build warnings/errors. This includes synthetic helper tests, not real MSI application or installed UI acceptance.

### Metadata, history and deployment branding (0.1.5)

Local administrators can edit bounded device/printer presentation metadata through the existing protected management pipe. Physical queue identities and Windows destinations remain unchanged. Metadata is machine-local, synchronized under the existing scoped device credential, and displayed as text by the native QQQ UI. The management request limit is 64 Ki characters to accommodate JSON-escaped Unicode descriptions.

Job pages deserialize at most100 summaries, event delivery/recovery uses200-record batches and detail maintenance deletes at most250 eligible records per pass. Unacknowledged/uncertain evidence and deduplication summaries remain; this is bounded work per operation, not a bounded lifetime database. Diagnostic log clearing removes owned dated files only, with a shared write lock and reparse rejection; it cannot clear the print journal or credentials.

The optional branded-install wrapper requires an administrator, validates bounded local configuration/artwork and stages it under the protected service directory. The MSI uses public display-label properties and remembers them in HKLM; customer labels never select the executable, service identity, updater origin or privileged command. The shortcut helper validates fixed target/empty arguments/ownership before replacing or deleting a tracked shortcut. Customer artwork and strings do not change code-signing identity or source/license attribution. WhatIf validation and pure shortcut tests do not establish a completed branded MSI upgrade/rollback qualification.

Shared Start menu directories use a separate shortcut policy: deletion-only rights are allowed because they cannot create replacement content; untrusted writes, permission changes and ownership changes remain rejected. New shortcut folders have protected SYSTEM/Administrators full access and Users read/execute. Protected updater/data directories retain their stricter policy. MSI major-upgrade removal is now inside the install transaction; rollback behavior still requires installed qualification.
