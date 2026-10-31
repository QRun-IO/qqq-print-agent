# QQQ Print Agent

<!-- Sized product icon; Markdown has no portable image-sizing syntax. -->
<!-- markdownlint-disable-next-line MD033 -->
<img src="src/Qqq.PrintAgent/Assets/Brand/app-preview.png" width="96" alt="QQQ Print Agent cloud and printer icon">

[![CI](https://github.com/QRun-IO/qqq-print-agent/actions/workflows/ci.yml/badge.svg)](https://github.com/QRun-IO/qqq-print-agent/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Print from **QQQ 4.1+** to printers at a local facility without opening inbound network ports. QQQ Print Agent runs as a Windows service, connects outward to QQQ, and submits documents to the printers installed for its Windows service account.

The native setup and status application uses the same [KofTwentyTwo AppKit](https://github.com/KofTwentyTwo/AppKit) foundation as GCLO and MarkdownViewr.Windows.

> **Prerelease software.** [v0.2.0-rc.1](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v0.2.0-rc.1) is published. All four release assets, checksums and exact-tag build attestations are verified; the tagged build passed 365 tests with zero warnings/errors/skips. The MSI remains unsigned. Installed acceptance for this release is still in progress; [acceptance notes](docs/ACCEPTANCE.md) distinguish earlier installed versions and CI packages from published releases.

Release `0.2.0-rc.1` adds standard QQQ sign-in and server device controls. Upgrade the QQQ printing server to the matching native extension before installing this client: it requires discovered printing extension `1.0.0` and a user session, and existing unbound pairings require explicit account association. An older bearer-only server will not work through a silent fallback. See [sign-in and migration](docs/QQQ-SIGN-IN.md), including replacement pairing and damaged-session recovery. Configured Authentik/provider renewal and installed browser-callback acceptance remain separate qualification work.

## What works

- Discover Windows print queues and explicitly choose which ones to expose to QQQ.
- Use native Printers, Jobs, Logs and Settings navigation, persistent status, reconnect and a visible health tray icon.
- Browse compact printer/job tables with full details and confirmed test/reprint/cancel actions. Expand printer information inside the selected row and open printer settings or job details on double-click.
- Sort table headers in either direction. Printers default to Name ascending; jobs default to immutable Created time descending. Job sorting applies across the matching result set before paging.
- Page through local history in groups of 50, searching by job ID, document-title or printer-name prefix.
- Edit computer/printer names, descriptions and locations locally and synchronize them to QQQ.
- Configure daily log retention/size limits, inspect usage and explicitly clear diagnostic logs.
- Start/restart the service and opt into tray startup at sign-in; service and companion share the AppKit log viewer.
- Check for updates manually or at startup using AppKit; confirm a machine MSI upgrade of the service, companion and CLI together.
- Set theme and splash duration, and open project/help/release links and dependency credits.
- Print packing slips and other PDFs through the installed Windows driver.
- Send printer-ready ZPL to Zebra/ZDesigner queues.
- Generate one QQQ-branded warehouse diagnostic at a time: Letter, A4 or 4 × 6-inch PDF; 4 × 6-inch ZPL at 203, 300 or 600 dpi. Native choices prefer ZPL on compatible label queues, with a driver-name DPI suggestion that the operator verifies.
- Enroll once using a short-lived code from QQQ; the service stores its own protected device credential.
- Keep printing when the desktop app is closed or the user logs out.
- Preserve job receipts and outgoing status events across service restarts.
- Avoid automatically printing a job again when a previous submission is uncertain.

```mermaid
flowchart LR
  Q[QQQ 4.1+ printing QBit] <-->|Outbound HTTPS polling| S[Windows service]
  U[AppKit setup and status app] -->|Local administrator pipe| S
  S --> W[Windows spooler]
  W --> L[Laser printer: PDF]
  W --> Z[Zebra printer: ZPL]
```

The companion projects are [qbit-printing](https://github.com/QRun-IO/qbit-printing), the QQQ server integration, and [qqq-print-protocol](https://github.com/QRun-IO/qqq-print-protocol), the shared versioned API contract. The agent does not require a separate cloud printing vendor.

## Install and connect

1. Download the x64 MSI and `SHA256SUMS` from [Releases](https://github.com/QRun-IO/qqq-print-agent/releases). Verify the checksum before installing.
2. Install the MSI with administrator rights. It installs the service and adds **QQQ Print Agent**, or your configured customer label, under **Print Connectors** in the Start menu.
3. In your QQQ printing administration, create an enrollment code for this facility/device.
4. Open **QQQ Print Agent**, accept the administrator prompt, enter the QQQ server URL and enrollment code, and select **Connect**.
5. In **Printers**, select which queues to expose to QQQ. Then enable the intended discovered queue in QQQ and submit a test document.

The QQQ URL must use HTTPS. HTTP is accepted only for loopback development such as `http://localhost:18081`. No inbound firewall rule is installed. Existing outbound proxy/network policies and local printer connectivity still apply.

See the [installation guide](docs/INSTALLATION.md) for verification, service identity, upgrade and removal instructions, and the [user guide](docs/USER-GUIDE.md) for the setup screen and job statuses.

## Requirements and supported behavior

| Area | Initial support |
| --- | --- |
| Server | QQQ 4.1+ with the printing QBit and protocol v1 |
| Windows | x64; Windows 11 used for hardware acceptance; API target Windows 10 build 19041+ |
| Runtime | Bundled with the MSI; users do not install .NET separately |
| Printers | Installed Windows queues accessible to LocalSystem, the default service account |
| Documents | PDF and ZPL, at most 20 MiB per job |
| PDF options | Copies, actual size or fit; queue-default paper/tray settings |
| ZPL options | Copies; label dimensions/DPI encoded in printer-ready ZPL |
| Transport | Outbound HTTPS polling, normally every five seconds |

A printer visible only in a signed-in user's profile may not be available to the service. Configure the printer for the machine and test through the installed service. Virtual PDF/fax queues are not suitable for unattended physical printing.

`SPOOLED` means Windows accepted the job. It does **not** prove that paper came out. If the agent cannot determine whether a previous submission succeeded, it reports `OUTCOME_UNKNOWN`; an operator should inspect the printer before requesting another copy.

## Validation so far

The 0.1.9 source gate passed **255 tests (66 Core + 189 Windows/CLI)** with zero warnings/errors, and the full solution/UI build passed. [CI build 41185716708](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41185716708) and the required security, CodeQL and script gates passed. The CI MSI upgraded the installed service, companion and CLI with exit 0 and no reboot, preserving protected credentials and journal history. Authenticated management, connection and history checks succeeded; all **16 installed UI checks passed in one run**. No print was submitted by the upgrade or UI tests.

[Release workflow 41190815995](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41190815995) published v0.1.9-rc.1 from protected main revision `cf0e362`. The MSI, `SHA256SUMS`, `Install-Branded.ps1` and `branding.example.json` were verified, including provenance attestations for all four assets. The published MSI differs from the installed CI MSI; runtime sources are unchanged, but the release package has not been separately reinstalled. Their full hashes and verification scopes are in [acceptance notes](docs/ACCEPTANCE.md).

These results cover the local installed service/app upgrade. They do not establish remote deployment, Windows reboot/recovery, updater-initiated elevation, hostile-tree rollback, broad driver qualification or every accessibility scenario. Build attestations establish provenance, not publisher signing; the MSI remains unsigned. The earlier clean service stop still lacks a confirmed cause.

### Historical 0.1.7 hardware acceptance — 2026-10-30

The user confirmed laser PDF, Zebra ZPL and Zebra PDF output on 0.1.7. The first Zebra PDF was accepted by the spooler but did not print until machine defaults were corrected to 4 × 6-inch direct thermal and the hardware pause was cleared. A fresh reviewed PDF then printed fully and correctly. Barcode scanning and measured alignment remain unverified. This dated evidence does not claim fresh physical output from 0.1.9; the [configuration diagnosis and earlier results](docs/ACCEPTANCE.md) retain their original scope.

## Development

Requires Windows x64 and the .NET 10 SDK selected by `global.json`. Check out this repository and the protocol repository as siblings; use the pinned protocol commit in `.github/workflows/ci.yml`.

```powershell
git clone https://github.com/QRun-IO/qqq-print-agent.git
git clone https://github.com/QRun-IO/qqq-print-protocol.git
Set-Location qqq-print-agent
./build/Verify.ps1
./build/Build-Installer.ps1 -ProductVersion 0.2.0 -ReleaseVersion 0.2.0-rc.1
```

[History and log limits](docs/SCALABILITY.md) · [Customer branding](docs/BRANDING.md) · [Developer guide](docs/DEVELOPMENT.md) · [Administrator guide](docs/ADMINISTRATION.md) · [Troubleshooting](docs/TROUBLESHOOTING.md) · [Release process](docs/RELEASING.md) · [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md)

## Current limits

This prerelease uses polling rather than WebSocket notifications. Custom PDF paper dimensions are rejected; use the Windows queue's default media. Operator actions, safe journal detail retention and bounded renderer subprocesses are implemented. Physical completion telemetry, custom media and production signing remain outside the initial deployment scope. Update checks are implemented; installation requires operator confirmation and administrator rights. Review the [roadmap](docs/TODO.md) before a production rollout.

## License and acknowledgments

[MIT](LICENSE). The original Windows template and AppKit copyright notices are preserved. See [NOTICE](NOTICE) for the foundation and bundled dependency acknowledgments. QQQ and the printing QBit are separate projects with their own licenses.
