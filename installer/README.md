# Machine installer

Build the service, AppKit companion and CLI into one x64 machine MSI:

```powershell
./build/Build-Installer.ps1 -ProductVersion 0.1.0
```

The build publishes self-contained .NET applications into `artifacts/package` and builds WiX 6.0.2 authoring. Existing publish outputs can be packaged using `-SkipPublish`. The resulting MSI is local development output and is **unsigned unless a signing certificate is explicitly provided**.

For a signing certificate already installed in the Windows certificate store:

```powershell
./build/Build-Installer.ps1 -ProductVersion 0.1.0 -CertificateThumbprint '<thumbprint>' -TimestampServer 'https://your-approved-timestamp-service'
```

Signing needs `signtool.exe` on PATH. The script signs the first-party executables before packaging and signs/verifies the resulting MSI. Use the existing organization signing workflow for distribution; never store signing secrets here.

Install the resulting MSI through an elevated Windows Installer session. It installs `QqqPrintAgent` as an automatic LocalSystem service, depends on the Windows Spooler, starts the service, and adds the companion to the Start menu. Configure and test printers accessible to LocalSystem; user-only network printer mappings are not sufficient. The current companion's management pipe requires local administrator access.

The installer protects `%ProgramData%\QqqPrintAgent` for SYSTEM and Administrators and preserves the journal/enrollment through upgrades and uninstall. It replaces app/service/CLI together and stops the old service before removing files. Preserved credentials/data can be deliberately removed by an administrator after decommissioning; do not delete the journal while a device still has unresolved print jobs.

No inbound firewall rule or automatic desktop updater is installed. Production release qualification must include signed installation, service-account printing while logged out, upgrade/reboot, and uninstall/reinstall recovery. Building an MSI alone does not establish those checks.

The installer suppresses only MSI validator ICE03 because the bundled Microsoft WinUI `Microsoft.ui.xaml.dll`/`Microsoft.UI.Xaml.Phone.dll` have multi-language version resources longer than that validator's File.Language constraint, and their MUI resources include newer locale IDs absent from its static list. The vendor binaries are retained unchanged. Other MSI validation remains enabled; installation and launch qualification are still required.

References: [WiX service installation](https://docs.firegiant.com/wix/schema/wxs/serviceinstall/), [file harvesting](https://docs.firegiant.com/wix/schema/wxs/files/), [MSI directory security](https://docs.firegiant.com/wix/schema/wxs/permissionex/).
