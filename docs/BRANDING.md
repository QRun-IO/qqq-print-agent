# Customer branding

There are two levels. A **client edition** is a separate, fully branded build and installer: its own mark, icon and logo, the client's product and company in every executable and in Windows, and its own installer and update asset. **Deployment branding** applies labels, a colour and artwork to the shared installer at install time. Editions are for clients who get their own installer; deployment branding is for customers of the shared one.

## Client editions

Add a folder under `editions/` (see [editions/README.md](../editions/README.md)) with `edition.json`, `app-icon.svg`, `app.ico` and an optional `logo.png`, then build with:

```powershell
./build/Build-Installer.ps1 -Edition gg-forge
```

The result is `artifacts/installer/<assetPrefix>-<version>-x64.msi`, for example `GgForgePrintAgent-2.0.0-x64.msi`. Installed, it shows the client's name and mark on the splash, About, window title, tray tooltip, Start menu, Programs list and service; Task Manager and file properties show the client's product, company and description; and the in-app updater follows `<assetPrefix>-v<version>-x64.msi` on the release feed. CI builds every edition's unsigned installer, and the stable release workflow signs and publishes one MSI per edition beside the generic one, all listed in the same `SHA256SUMS`. An edition installer forces its client's labels over any labels an earlier install stored.

An edition's descriptor can also name the server as its users know it (`serverName`, used in sign-in and status text instead of QQQ) and leave the Query mascot and License sections out of About (`aboutMascot`, `aboutLicense`); the generic build keeps all of them. Editions share application id, data paths, executable file names, the install directory, the protocol handler, the MSI upgrade identity and the signing identity, so an edition installer upgrades the generic build and other editions in place and keeps the pairing and history. Deployment branding below still applies on top of an edition.

## Deployment branding

Use the same [signed stable machine MSI](https://git.kof22.com/QRun-IO/qqq-print-agent/releases/tag/v1.0.1) for every customer. No customer-specific build is required. The deployment wrapper stages an optional machine branding file and local assets, then supplies customer labels to Windows Installer. The companion, service display name, installed-product labels and all-users Start menu shortcut use that identity.

## Install with a customer name

Copy and edit [branding.example.json](branding.example.json). The text-only example works without image files:

```json
{
  "displayName": "Acme Print Connector",
  "companyName": "Acme Logistics",
  "supportUrl": "https://support.example.com/printing",
  "accentColor": "#0F766E"
}
```

Use PowerShell 7.4 or newer. First validate a downloaded MSI and configuration without making machine changes:

```powershell
./build/Install-Branded.ps1 -MsiPath 'C:\Deployment\QqqPrintAgent.msi' -BrandingFile './docs/branding.example.json' -WhatIf
```

Run the same command **without `-WhatIf` from an elevated administrator PowerShell** to install. The script does not silently elevate or bypass UAC. Supply `-ExpectedSHA256 '<64 hexadecimal digits>'` when you have the expected MSI checksum from your approved release source. The wrapper verifies it before installation and keeps the source MSI read-locked through execution. A checksum does not substitute for publisher signing; verify the stable MSI signature as described in [Installation](INSTALLATION.md#download-and-verify). Customer branding does not change the Authenticode publisher or update feed.

The wrapper launches `msiexec.exe` with `/i`, `/passive` and `/norestart`, using separate arguments. It accepts success/reboot codes 0, 1641 and 3010 and reports the result. It creates `%ProgramData%\QqqPrintAgent` with SYSTEM/Administrators permissions when absent, or rejects unsafe existing permissions. It never broadens access to existing device state. It copies validated assets under unique names, replaces `branding.json`, and restores the previous configuration if Windows Installer fails. The client/service/enrollment remain one coherent MSI installation.

`./build/Test-BrandedInstaller.ps1` exercises validation using synthetic files and `-WhatIf` only. It runs in CI after the normal verification gate; it does not install an MSI.

## Fields and assets

Every field is optional. The wrapper rejects unknown fields and invalid supplied values before installation.

| Field | Behavior and limits |
| --- | --- |
| `displayName` | Companion title/heading/tray tooltip/About, service display name, installed-product label and Start menu label; at most 180 characters |
| `companyName` | Customer/company presentation and installed-product publisher label; at most 180 characters |
| `supportUrl` | Explicit Contact support link; HTTPS without embedded credentials, at most 2,048 characters |
| `accentColor` | AppKit splash/About background; `#RRGGBB`. Runtime uses it only if white text has at least 4.5:1 contrast; otherwise built-in colors remain |
| `logoPath` | Optional PNG in the companion heading/About; maximum 2 MiB and 2,048 by 2,048 pixels |
| `iconPath` | Optional ICO for the AppKit application icon and branded Start menu shortcut; maximum 2 MiB, 1–64 bounded image-directory entries |

Names and other scalar values cannot contain control characters. JSON must be an object no larger than 16 KiB. Image paths may be local absolute paths or relative to the input branding file. Network paths, alternate data streams and reparse-point components are rejected by the wrapper. Local image files are copied into protected ProgramData; staged configuration uses their new relative filenames. Do not point managed deployments at user-writable asset locations. The runtime loader also limits image paths to 260 characters and checks PNG/ICO structure; malformed or inaccessible runtime settings fall back to built-in presentation.

To provide customer artwork, add `"logoPath": "acme-logo.png"` and/or `"iconPath": "acme-icon.ico"` beside the example fields and put those files beside the input JSON. An ICO is a separate Windows icon asset, not a renamed PNG. The PNG adds customer artwork to the window/About; it does not replace AppKit's bundled vector mark. Green/orange/red tray dots continue to communicate printing health. Embedded executable/MSI icons and binary resources remain those of the shared release.

## Upgrade and rename behavior

The MSI persists `DisplayName` and `CompanyName` in `HKLM\SOFTWARE\QRunIO\PrintAgent`. Future installations of the ordinary shared MSI, including the in-app updater, recover these values when new `BRANDNAME`/`BRANDCOMPANY` properties are not supplied. ProgramData branding/assets and registry labels are retained through upgrades and full uninstall with device state. Removing `branding.json` therefore does **not** erase persisted customer labels.

The installer creates the companion shortcut under **Start → Print Connectors**. Unsafe filename characters are sanitized; truncated or sanitized names receive a stable hash suffix. Rename cleanup and full-uninstall cleanup only remove a tracked shortcut after verifying its exact installed target, arguments and ownership marker, immediately before deleting it. Other shortcuts are left alone. An existing conflicting file under the wanted name causes an explicit installation error rather than an overwrite; from 2.0 the name is checked again immediately before the shortcut is put in place.

From 2.0 the installer tracks, by name, every branded shortcut it has put there, until that shortcut is gone, is found not to be its own any more, or the product is uninstalled, and treats what it finds under a tracked name as follows. A file that is not ours (someone else's shortcut, a file that is not a shortcut at all, or a link) is left unchanged, noted as a warning in the activity log and no longer tracked; neither an install nor an uninstall fails because of it. A file that cannot be opened, because another program holds it, is not judged, and so it is never deleted or overwritten. A file that Windows reports as in use is first tried nine times over two seconds. If a tracked shortcut still cannot be opened, deleted or replaced, neither an install nor an uninstall fails because of it (decided 2026-11-11; not yet exercised by a real installer run): an install, upgrade or repair leaves it as it is, keeps tracking it and replaces or removes it the next time, and an uninstall leaves it where it is and stops tracking it, so that it has to be deleted by hand unless the product is installed again under the same display name. Each case is a warning in the activity log with the file name and the kind of the failure. Only a file under the wanted name that is not tracked and cannot be opened still stops an install, before anything is changed: close the program that holds it and run the installer again. See the [installer notes](../installer/README.md) for the full rules and [Troubleshooting](TROUBLESHOOTING.md) for where the reason of a failed step is logged. A previous brand's shortcut that cannot be removed during a rename stays tracked, with a warning, and is removed by the next install, upgrade, repair or uninstall. A read-only mark on a shortcut of ours is cleared before the shortcut is replaced or removed. The temporary file a shortcut is written to before it is put in place is not tracked; one left in the folder by an interrupted run (`.qqq-` followed by 32 hexadecimal digits and `.lnk`) is removed by the next run or the uninstall.

For a customer rename, rerun the wrapper with revised JSON and the intended MSI. This updates service/product/shortcut labels together with runtime settings. To return to built-in labels, explicitly deploy `displayName: "QQQ Print Agent"` and `companyName: "QRun-IO"` through the wrapper. Omitting `-BrandingFile` preserves existing configuration. Software-management systems may instead supply `BRANDNAME` and `BRANDCOMPANY` as MSI public properties; JSON takes precedence for companion presentation, so keep both sources consistent.

Restart the companion to load changed presentation. Use **Exit companion**, then launch it again; closing the window merely hides it to the tray. Directly editing `branding.json` changes runtime presentation after restart, but does not itself rerun the installer to update Windows product/service/shortcut labels.

## Fixed identities and attribution

Customer labels do not change the AppId, internal service name `QqqPrintAgent`, MSI upgrade identity, executable names, install directories, protocol identity or data paths. The installation remains under `%ProgramFiles%\QQQ Print Agent` and state under `%ProgramData%\QqqPrintAgent`. Source, license and third-party attribution remain available in About and the distribution. Put only presentation settings in branding JSON; enrollment codes and device credentials belong in protected service configuration.
