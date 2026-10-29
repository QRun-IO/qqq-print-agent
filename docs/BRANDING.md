# Customer branding

Deploy the same MSI to every customer. An optional `%ProgramData%\QqqPrintAgent\branding.json` changes the companion's presentation without a custom build. Restart the companion after changing it; the printing service can keep running.

Copy and edit [branding.example.json](branding.example.json). Every field is optional:

| Field | Behavior and limits |
| --- | --- |
| `displayName` | Window title, heading, tray tooltip and About; at most 60 characters |
| `companyName` | Heading, splash/About subtitle and description; at most 100 characters |
| `supportUrl` | Explicit Contact support link in the window and About; HTTPS only, no embedded credentials, at most 2,048 characters |
| `accentColor` | AppKit splash/About background; `#RRGGBB` with at least 4.5:1 contrast against white |
| `logoPath` | PNG displayed in the main heading and About; local absolute path or relative to the branding file directory, at most 260 characters; maximum 2 MiB and 2,048 × 2,048 pixels |

Names cannot contain control characters. JSON is capped at 16 KiB and a shallow object. Missing, inaccessible or malformed files use the built-in appearance; invalid individual fields use their defaults. An unreadable/invalid logo is omitted. PNG decoding failures hide the logo without blocking the companion. Network/UNC logo paths and automatic image downloads are not supported. Deploy assets onto a local disk and keep the directory restricted to administrators and SYSTEM, as created by the MSI.

The stable AppId, Windows service name, MSI identity, executable names, Start menu entry, package icon and source/license attribution remain QQQ Print Agent. AppKit's bundled vector mark remains in its splash/About header; the customer PNG is additionally shown in the window/About. Tray dots retain green/orange/red health meaning. Accent colors do not replace status colors. Choose a color with appropriate contrast for your light/dark theme.

## Deployment

After installing the MSI, use elevated PowerShell or your organization's software management. Example with files staged on the local disk:

```powershell
$target = Join-Path $env:ProgramData 'QqqPrintAgent'
if (-not (Test-Path -LiteralPath $target -PathType Container)) {
    throw 'Install QQQ Print Agent before deploying branding.'
}
Copy-Item -LiteralPath 'C:\Deployment\customer-logo.png' -Destination (Join-Path $target 'customer-logo.png')
$branding = @{
    displayName = 'Acme Print Connector'
    companyName = 'Acme Logistics'
    supportUrl = 'https://support.example.com/printing'
    accentColor = '#0F766E'
    logoPath = 'customer-logo.png'
}
$branding | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'branding.json') -Encoding utf8
```

Use **File → Exit companion**, then launch it again. Closing the window only hides it to the tray and does not reload branding. Remove `branding.json` and restart the companion to restore defaults. The configuration and logo are customer-managed files retained with ProgramData state; MSI updates do not overwrite them. Branding contains presentation data only—never put enrollment codes or device credentials in it.

Accent colors must provide an unrounded contrast ratio of at least **4.5:1 against white**, because AppKit splash/About text uses white foreground. A valid but lighter color falls back to the built-in teal gradient; other valid branding settings still apply. `#767676` passes while `#777777` does not. The main content heading follows the active Windows theme. This uses the [WCAG contrast calculation](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html); it is not a claim that the entire application has completed accessibility certification.
