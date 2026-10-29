# Install QQQ Print Agent

## Download and verify

Download the x64 MSI and `SHA256SUMS` from the same [GitHub release](https://github.com/QRun-IO/qqq-print-agent/releases). Check the file locally:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\QqqPrintAgent-v0.1.0-rc.1-x64.msi
Get-Content .\SHA256SUMS
```

Compare the complete hash with the entry for the downloaded MSI. Checksums detect changes after publication; they do not replace publisher signing. This prerelease is **unsigned** and no Authenticode or provenance attestation is claimed. Download only from the project's release page and follow your organization's software-install policy.

## Install

Run the MSI as an administrator. Windows Installer deploys one coherent application:

- The `QqqPrintAgent` automatic Windows service, running as LocalSystem and depending on the Windows Spooler.
- The native **QQQ Print Agent** setup/status app and Start-menu entry.
- The `qqq-print-agent.exe` diagnostic CLI.
- A restricted `%ProgramData%\QqqPrintAgent` directory for enrollment and the job journal.

There is no inbound firewall rule, local HTTP server, or desktop self-updater. Keep outbound HTTPS available to the QQQ server. The UI requests administrator elevation because its management pipe permits only SYSTEM and local administrators.

## Prepare printers

Install each printer and its appropriate vendor driver in Windows. Print a normal Windows test page, then discover the queues through QQQ Print Agent. The installed service must have access; a queue mapped only under a user's profile is insufficient. Use machine-installed USB/TCP/IP queues where possible. Network share authentication may require a deliberately configured service identity; that variant has not been qualified for this prerelease.

Set the queue's default paper/tray/orientation for PDF printing. The initial API supports actual size or fit and copies, but rejects custom media dimensions. For ZPL, configure compatible Zebra/ZDesigner drivers and generate ZPL matching the printer's DPI and label stock.

## Enroll

Create a single-use enrollment code in QQQ. Open the companion, enter the QQQ server origin and code, then connect. Enrollment runs in the service so its credential is protected under the service identity. The desktop stores no device token.

Do not enroll the development CLI under your user and copy its protected file into ProgramData: DPAPI credentials are bound to the account that created them. Use the companion or an elevated CLI `enroll --service` command.

## Upgrade and remove

Install a newer MSI as administrator. The installer stops and replaces the service and companion together. Current authoring retains ProgramData through upgrades and uninstall. Stop new job submissions and resolve uncertain jobs before upgrading.

Use **Settings → Apps → Installed apps → QQQ Print Agent → Uninstall** to remove application files and the service. Enrollment and journal data are retained to support recovery. After revoking/decommissioning the device in QQQ and resolving all outstanding jobs, an administrator may deliberately remove the retained data. Never delete a live journal as a troubleshooting shortcut: it is evidence used to avoid duplicate printing.

The first prerelease has local install/service/uninstall acceptance. Upgrade, reboot and all supported-driver combinations still require wider qualification.

## Select exposed printers

New installations share no queues. Open the companion's Printers tab and enable **Expose to QQQ** for each intended printer, then approve/enable it in QQQ. Only the selected set is sent to the cloud; all discovered queues remain visible locally for setup. A missing formerly exposed queue is shown as offline.

