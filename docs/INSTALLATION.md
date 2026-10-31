# Install QQQ Print Agent

## Download and verify

Download the x64 MSI and `SHA256SUMS` from the same [GitHub release](https://github.com/QRun-IO/qqq-print-agent/releases). Check the file locally:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\QqqPrintAgent-v0.2.0-rc.1-x64.msi
Get-Content .\SHA256SUMS
```

Use the filename of the release you downloaded and compare the complete hash with its `SHA256SUMS` entry. The published baseline is 0.2.0-rc.1; realtime source 0.3.0-rc.1 is unreleased. Checksums detect changes after publication; they do not replace publisher signing. The MSI is **unsigned**, with no Authenticode identity. The current release workflow also produces GitHub build-provenance attestations, which are verified separately from checksums and are not code signing. Older releases may lack those attestations; their presence must be checked for the exact downloaded asset. Download only from the project's release page and follow your organization's software-install policy.

## Install

Run the MSI as an administrator. Windows Installer deploys one coherent application:

- The `QqqPrintAgent` automatic Windows service, running as LocalSystem and depending on the Windows Spooler.
- The native **QQQ Print Agent** setup/status app and Start-menu entry.
- The `qqq-print-agent.exe` diagnostic CLI.
- A restricted `%ProgramData%\QqqPrintAgent` directory for enrollment and the job journal.

There is no inbound firewall rule or local HTTP server. The unreleased realtime client requires outbound WSS upgrades and HTTPS to the QQQ server; update checks/downloads use HTTPS to GitHub release services. The matching server must advertise printing extension 2.0. The published 0.2 transport remains HTTP-based. The UI requests administrator elevation because its management pipe permits only SYSTEM and local administrators.

## Service recovery

The MSI configures Windows Service Control Manager to restart `QqqPrintAgent` after its first and second unexpected process failures, waiting 30 seconds each time. On the third and subsequent failures it takes no action; the failure count resets after one failure-free day. An administrator stop, upgrade or Windows shutdown exits normally. Unexpected fatal runtime failures exit nonzero so Windows can apply this policy. Routine network failures remain inside the running service's reconnect loop.

Recovery uses the supported [WiX Util ServiceConfig](https://docs.firegiant.com/wix/schema/util/serviceconfig/) extension, matching the existing WiX 6.0.2 toolset. It does not reboot Windows or run arbitrary recovery commands. After installation, inspect the applied policy with this read-only command:

```powershell
sc.exe qfailure QqqPrintAgent
```

Expected: reset period 86400 seconds, restart actions with 30000 ms delay, then no action. Installer compilation and installed recovery acceptance are separate checks; configuration in source alone does not prove Windows has applied it. Do not deliberately crash a service while print jobs are active. After retries are exhausted, inspect **Logs** and correct the underlying fault before restarting the service. Recovery preserves the journal; an uncertain submission is not automatically reprinted.

The shared AppKit log lives in `%ProgramData%\QqqPrintAgent\logs`, with 30-day daily-file retention and the same restricted directory access. Service and companion events use source labels and retain severity for the viewer's **Errors only** filter. Credential-shaped values are redacted before persistence; callers must still never log tokens, enrollment payloads or document contents. Log files can contain local operational identifiers and should not be included in release artifacts.

## Service controls and sign-in preference

The companion's Settings page reads Windows Service Control Manager independently of the management pipe. **Start service** starts a stopped installation; **Restart service** is an explicit administrator action and can interrupt active submissions. Inspect uncertain jobs before retrying them. Each state transition has a 30-second wait limit, after which the app directs the operator to Windows Services and Logs. A missing installation links to the MSI release rather than pretending that a disconnected pipe can start a nonexistent service.

Printing starts with Windows through the service. The companion tray is separate. **Start at sign-in** is off until an administrator explicitly enables it in the installed companion. This action registers a task for that administrator's SID, with an interactive token and highest available privileges. It saves no password and does not grant elevation to a standard user. The task starts only the fixed MSI-installed executable under Program Files, with `--tray`; development/portable executables and reparse-point substitutes are rejected. No task is registered merely by launching the app. Disable the preference to remove the current user's task.

Enabling the preference is the administrator's authorization for future elevated launches at their sign-in. The task is not a general-purpose launcher: its executable and arguments are fixed. Users who do not need persistent tray status can leave it off and open the companion from the Start menu. Installed sign-in/task lifecycle acceptance remains separate from source and synthetic XML tests.

Full uninstall removes only tasks whose name, current-user SID, interactive principal, logon trigger, installed command and `--tray` argument match this application's definition. Unknown or modified task definitions are left untouched. Major upgrades preserve these preferences; they replace the executable at the same installed path. Cleanup runs through the existing service executable as a deferred SYSTEM installer action before files are removed. This cleanup is not run during ordinary service startup.

## Isolated native printer operations

Discovery, submission and cancellation run in a short-lived child of the service or explicit diagnostic CLI. Control requests use anonymous standard-input/output pipes. Realtime documents stream to protected owned staging files; their content is not placed in command arguments or logs. Discovery and cancellation have bounded helper deadlines. Realtime printing uses the server's `nativeSubmissionTimeoutMillis`, default 120 seconds, rather than a fixed submission deadline. A timed-out child is terminated, with bounded cleanup, and also watches its parent. The child still runs with service privileges; isolation limits a blocked driver, not its authority.

This bounds a blocked printer driver without stopping cloud connectivity permanently. It cannot retract bytes already accepted by a printer: timeout, crash or cancellation after submission begins is an uncertain outcome and requires inspection before reprinting. Existing spooler receipt cancellation verifies the exact QQQ document identity before requesting cancellation; it does not prove that no paper or label has already emerged. Synthetic subprocess tests use a fake backend and never access a printer.

## Prepare printers

Install each printer and its appropriate vendor driver in Windows. Print a normal Windows test page, then discover the queues through QQQ Print Agent. The installed service must have access; a queue mapped only under a user's profile is insufficient. Use machine-installed USB/TCP/IP queues where possible. Network share authentication may require a deliberately configured service identity; that variant has not been qualified for this prerelease.

Set the queue's default paper/tray/orientation for PDF printing. The initial API supports actual size or fit and copies, but rejects custom media dimensions. For ZPL, configure compatible Zebra/ZDesigner drivers and generate ZPL matching the printer's DPI and label stock.

## Sign in and pair

Upgrade the QQQ printing server first; realtime source requires printing extension 2.0. In the companion's connection settings, enter its origin, discover the advertised sign-in options and sign in through the configured QQQ authentication. See [QQQ sign-in](QQQ-SIGN-IN.md) for browser-provider requirements and recovery.

For an existing unbound installation, choose **Associate the existing pairing with my account**; preserve its device record and history. For a new installation, complete sign-in and enter a single-use enrollment code created in QQQ. Enrollment runs in the service, protecting its device proof and QQQ session under the service identity. The desktop does not retain the password or device token. **Set up a new pairing** is an explicit replacement flow, not routine upgrade migration.

Normal CLI setup, printer exposure and connection commands use the authenticated installed service. Sign in through the companion first; `run` requests a connection refresh. Explicit `--legacy-compat` diagnostics require a separate `--data-dir` and cannot use `--service`. Do not copy a user-protected DPAPI credential into ProgramData: credentials are bound to the account that created them. The service owns unattended discovery and printing.

## Updates

In the MSI-installed companion, open Settings and select **Check for updates**. The **Check for updates on startup** preference enables a quiet check after startup; it does not authorize installation. AppKit's `UpdateCoordinator` and native prompts handle offers and confirmation. Development/portable copies cannot apply machine updates. The elevated companion and apply helper require local administrator rights; there is no persistent privileged update daemon or UAC bypass.

The feed is fixed to [QRun-IO/qqq-print-agent releases](https://github.com/QRun-IO/qqq-print-agent/releases), independently of customer branding or the QQQ server. Stable builds accept newer stable versions; prerelease builds may accept newer RC or stable versions. Offers below core version 0.1.3 are rejected. Downloads require the expected versioned MSI and same-release `SHA256SUMS`. SHA-256 checks establish consistency with that release, not independent publisher identity or signing; the prerelease remains unsigned.

After confirmation, the adapter downloads into a protected unique folder under `%ProgramData%\QqqPrintAgent\updates`, verifies size/hash and prepares a copied CLI helper. The service pauses new claims and waits for active native submissions; connection/control traffic remains separate. The helper waits for the companion to exit, rechecks the package, then runs one MSI for service, companion and CLI. It preserves enrollment and journal evidence. Windows Installer is not forcibly terminated. A failed or abandoned handoff does not leave claims paused permanently.

The helper records a dated result in `updates\last-result.json`, retains a private installation log and attempts to reopen the companion. Settings displays the last result. Exit 3010 means a Windows restart is required, not that the new app is fully ready. On failure, inspect Settings/Logs and the installed version before retrying. Completed update folders, helper copies and install logs are retained for administrator review; there is no automatic cleanup policy for completed stages. Remove an old stage only after confirming its installer/helper has exited and its evidence is no longer needed; never remove enrollment or journal files.

The published 0.2 MSI has completed a state-preserving local upgrade, native QQQ sign-in and device-control checks. The user confirmed Letter PDF and Zebra ZPL output plus the Connected footer and both printers. The earlier 0.1.9 automated UI run and 0.1.7 hardware checks retain their own scope. None of these establishes installed 0.3 realtime behavior, a complete in-app updater handoff or restart-required recovery. See [acceptance evidence](ACCEPTANCE.md) and [session state](SESSION-STATE.md) for exact versions and remaining checks. A nonworking older companion can be repaired using the reviewed MSI directly.

## Upgrade and remove

Install a newer MSI as administrator. The installer stops and replaces the service and companion together. Current authoring retains ProgramData through upgrades and uninstall. Stop new job submissions and resolve uncertain jobs before upgrading.

Use **Settings → Apps → Installed apps → QQQ Print Agent → Uninstall** to remove application files and the service. Enrollment and journal data are retained to support recovery. After revoking/decommissioning the device in QQQ and resolving all outstanding jobs, an administrator may deliberately remove the retained data. Never delete a live journal as a troubleshooting shortcut: it is evidence used to avoid duplicate printing.

Local MSI upgrades and installed UI checks are recorded by version in the acceptance evidence. Reboot/recovery, a complete in-app upgrade and broader driver combinations still require qualification; an MSI exit code alone does not establish those results.

## Select exposed printers

New installations share no queues. Open the companion's Printers tab and enable **Expose to QQQ** for each intended printer, then approve/enable it in QQQ. Only the selected set is sent to the cloud; all discovered queues remain visible locally for setup. A missing formerly exposed queue is shown as offline.
