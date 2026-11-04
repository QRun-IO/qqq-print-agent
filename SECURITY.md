# Security policy

## Report privately

Do not publish a security vulnerability as a normal issue. Use [GitHub private vulnerability reporting](https://github.com/QRun-IO/qqq-print-agent/security/advisories/new). Include the affected version, reproduction steps, impact and sanitized evidence. Do not send live enrollment codes, device credentials, customer documents or local print databases.

Fixes target the latest stable 1.0 release. There is no long-term support commitment or independently audited production security certification. Gitea is the primary source repository; the established private reporting channel above remains available on GitHub.

## Trust and privileges

The machine service runs as LocalSystem to access machine-installed queues. The companion requires local administrator rights and communicates over an administrator-only named pipe. Treat a trusted QQQ server as authorized to supply printer documents to the explicitly exposed queues. RAW ZPL is printer control data and must originate from trusted generation workflows.

Device credentials are stored with DPAPI under the service identity in an MSI-restricted ProgramData directory. They are never included in tray snapshots. The local event journal may contain operational job IDs and error text; protect it as private operational data.

Remote QQQ endpoints require HTTPS. Plain HTTP is allowed only on loopback for development. Hash/length checks detect changed payloads; they do not make an untrusted PDF or ZPL document safe. Keep Windows, drivers and the OS renderer patched.

## Distribution

Stable **1.0.1** publishes an Azure Artifact Signing-signed MSI and first-party executables, `SHA256SUMS` and build provenance. Download from the [official release](https://git.kof22.com/QRun-IO/qqq-print-agent/releases/tag/v1.0.1) and verify both checksum and Authenticode signature. The installed updater uses the matching GitHub release assets. Earlier unsigned prereleases retain their historical status; signing does not establish printer compatibility or make arbitrary printer documents safe. See [Installation](docs/INSTALLATION.md) and [release signing](docs/RELEASE-SIGNING.md).

See [the threat model](docs/security/threat-model.md) for implemented controls and residual risks.
