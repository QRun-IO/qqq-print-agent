# Customer branding

Use the same released machine MSI for every customer. No customer-specific build is required. The deployment wrapper stages an optional machine branding file and local assets, then supplies customer labels to Windows Installer. The companion, service display name, installed-product labels and all-users Start menu shortcut use that identity.

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

Run the same command **without `-WhatIf` from an elevated administrator PowerShell** to install. The script does not silently elevate or bypass UAC. Supply `-ExpectedSHA256 '<64 hexadecimal digits>'` when you have the expected MSI checksum from your approved release source. The wrapper verifies it before installation and keeps the source MSI read-locked through execution. A checksum does not substitute for publisher signing.

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

The installer creates the companion shortcut under **Start → Print Connectors**. Unsafe filename characters are sanitized; truncated or sanitized names receive a stable hash suffix. Rename cleanup and full-uninstall cleanup only remove the tracked shortcut after verifying its exact installed target, arguments and ownership marker. Other shortcuts are left alone. An existing conflicting shortcut causes an explicit installation error rather than an overwrite.

For a customer rename, rerun the wrapper with revised JSON and the intended MSI. This updates service/product/shortcut labels together with runtime settings. To return to built-in labels, explicitly deploy `displayName: "QQQ Print Agent"` and `companyName: "QRun-IO"` through the wrapper. Omitting `-BrandingFile` preserves existing configuration. Software-management systems may instead supply `BRANDNAME` and `BRANDCOMPANY` as MSI public properties; JSON takes precedence for companion presentation, so keep both sources consistent.

Restart the companion to load changed presentation. Use **Exit companion**, then launch it again; closing the window merely hides it to the tray. Directly editing `branding.json` changes runtime presentation after restart, but does not itself rerun the installer to update Windows product/service/shortcut labels.

## Fixed identities and attribution

Customer labels do not change the AppId, internal service name `QqqPrintAgent`, MSI upgrade identity, executable names, install directories, protocol identity or data paths. The installation remains under `%ProgramFiles%\QQQ Print Agent` and state under `%ProgramData%\QqqPrintAgent`. Source, license and third-party attribution remain available in About and the distribution. Put only presentation settings in branding JSON; enrollment codes and device credentials belong in protected service configuration.
