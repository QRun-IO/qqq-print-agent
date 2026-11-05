# Client editions

One folder per client. `./build/Build-Installer.ps1 -Edition <folder>` builds a fully branded
installer from the same source: the companion ships the folder's mark and icon, every executable
carries the client's product and company metadata, the MSI's product name, manufacturer, service
display name and Start menu entry use the client's name, the MSI is named `<assetPrefix>-<version>-x64.msi`,
and the installed updater follows that asset name on the release feed.

Each folder holds:

| File | Required | Use |
| --- | --- | --- |
| `edition.json` | yes | Names, accent colour and asset prefix (fields below) |
| `app-icon.svg` | yes | Splash and About mark |
| `app.ico` | yes | Window, taskbar, executable and Start menu icon (multi-size, 16 to 256 px) |
| `logo.png` | no | Client logo in Settings → About and help (PNG, at most 2048 × 2048) |

`edition.json` fields: `id` (must equal the folder name; lowercase letters, digits, hyphens), `displayName`
and `companyName` (at most 180 characters), optional `tagline` and `description`, optional `supportUrl`
(HTTPS), optional `accentColor` (`#RRGGBB`, used only when white text stays readable on it) and
`assetPrefix` (2 to 40 letters or digits; the MSI and release asset name).

An edition changes presentation and labels only. Application id, data paths, executable file names,
the install directory, the protocol handler, the MSI upgrade identity and the signing identity are
shared with every other edition, so editions upgrade each other and the generic build in place.
See docs/BRANDING.md.
