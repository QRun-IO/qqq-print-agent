# Machine installer

Build the service, AppKit companion and CLI into one x64 machine MSI:

```powershell
./build/Build-Installer.ps1 -ProductVersion 0.1.8 -ReleaseVersion 0.1.8-rc.1
```

The build publishes self-contained .NET applications into a fresh per-run directory under `artifacts/package` and builds WiX 6.0.2 authoring. An explicit publish directory must not already exist; this prevents obsolete files from entering the harvested MSI. Existing reviewed outputs can be packaged using `-SkipPublish -PayloadDirectory <path>`; all component versions must match the requested release and source version. The resulting MSI is local development output and is **unsigned unless a signing certificate is explicitly provided**.

For a signing certificate already installed in the Windows certificate store:

```powershell
./build/Build-Installer.ps1 -ProductVersion 0.1.3 -ReleaseVersion 0.1.3-rc.1 -CertificateThumbprint '<thumbprint>' -TimestampServer 'https://your-approved-timestamp-service'
```

Signing needs `signtool.exe` on PATH. The script signs the first-party executables before packaging and signs/verifies the resulting MSI. Use the existing organization signing workflow for distribution; never store signing secrets here.

Install the resulting MSI through an elevated Windows Installer session. It installs `QqqPrintAgent` as an automatic LocalSystem service, depends on the Windows Spooler, starts the service, and adds the companion to the Start menu. Configure and test printers accessible to LocalSystem; user-only network printer mappings are not sufficient. The current companion's management pipe requires local administrator access.

Customer branding uses this same MSI without a rebuild. Validate a deployment with PowerShell 7.4 or newer:

```powershell
./build/Install-Branded.ps1 -MsiPath 'C:\Deployment\QqqPrintAgent.msi' -BrandingFile './docs/branding.example.json' -WhatIf
```

Remove `-WhatIf` in an elevated administrator PowerShell to install. Optional `-ExpectedSHA256` verifies an approved release checksum first. The wrapper validates bounded JSON, 180-character customer/company labels, HTTPS support links and optional local PNG/ICO assets; it stages them under protected ProgramData and restores the previous configuration if MSI installation fails. It launches the standard `/i /passive /norestart` machine installation. The sample JSON needs no image files. The dry-run fixture `./build/Test-BrandedInstaller.ps1` validates inputs without requesting an installation and runs in CI after Verify.

`BRANDNAME` and `BRANDCOMPANY` public MSI properties set the service display name and installed-product labels. Their persisted HKLM values are reused by subsequent ordinary MSI upgrades, including updates installed from the companion. The installer creates a branded shortcut in the all-users **Print Connectors** Start menu folder; rename/full-uninstall cleanup verifies the exact owned target before deleting it. Optional ICO branding affects the AppKit application icon and this shortcut; embedded release binaries/icons remain shared. Internal service/AppId/MSI upgrade identity and paths stay fixed, and source/license attribution remains visible. See [Customer branding](../docs/BRANDING.md) for configuration precedence, rename/reset behavior and asset limits.

The installer protects `%ProgramData%\QqqPrintAgent` for SYSTEM and Administrators and preserves the journal/enrollment through upgrades and uninstall. It replaces app/service/CLI together and stops the old service before removing files. Preserved credentials/data can be deliberately removed by an administrator after decommissioning; do not delete the journal while a device still has unresolved print jobs.

No inbound firewall rule or persistent privileged update daemon is installed. The companion uses AppKit update prompts with a machine MSI adapter: manual or optional startup checks use the fixed GitHub release feed, and installation requires confirmation and administrator rights. Protected staging and a copied CLI helper update all components together; completed stages/logs are retained for administrator review. Same-feed SHA-256 checks do not replace publisher signing. See [Updates](../docs/INSTALLATION.md#updates). Production release qualification must include signed installation, service-account printing while logged out, upgrade/reboot, and uninstall/reinstall recovery. Building an MSI alone does not establish those checks.

The installer suppresses MSI validator ICE03 because the bundled Microsoft WinUI `Microsoft.ui.xaml.dll`/`Microsoft.UI.Xaml.Phone.dll` have multi-language version resources longer than that validator's File.Language constraint, and their MUI resources include newer locale IDs absent from its static list. The vendor binaries are retained unchanged. ICE61 is also suppressed for intentional `AllowSameVersionUpgrades`, supporting RC-to-RC/stable releases that share a numeric MSI version. The app updater requires a strictly newer semantic release; manual MSI deployments must follow semantic release order and avoid downgrades. All other MSI validation remains enabled; installation and launch qualification are still required. See [ADR-0002](../docs/adr/0002-prerelease-quality-and-packaging.md).

References: [WiX service installation](https://docs.firegiant.com/wix/schema/wxs/serviceinstall/), [file harvesting](https://docs.firegiant.com/wix/schema/wxs/files/), [MSI directory security](https://docs.firegiant.com/wix/schema/wxs/permissionex/).

The 0.1.3-rc.1 source gate passed 163 tests with zero warnings/errors. The new MSI is being packaged; installed startup/UI and actual in-app upgrade acceptance remain pending. Same-numeric-version upgrades support RC transitions; the updater separately enforces a strictly newer semantic version.
