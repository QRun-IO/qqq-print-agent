# Customer branding

Deploy the same MSI to every customer. An optional `%ProgramData%\QqqPrintAgent\branding.json` changes the companion's presentation without a custom build. Restart the companion after changing it; the printing service can keep running.

Copy and edit [branding.example.json](branding.example.json). Every field is optional:

| Field | Behavior and limits |
| --- | --- |
| `displayName` | Window title, heading, tray tooltip and About; at most 60 characters |
| `companyName` | Heading, splash/About subtitle and description; at most 100 characters |
| `supportUrl` | Explicit Contact support link in the window and About; HTTPS only, no embedded credentials, at most 2,048 characters |
| `accentColor` | Heading foreground and AppKit splash/About background; `#RRGGBB` |
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
