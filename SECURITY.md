# Security policy

## Report privately

Do not publish a security vulnerability as a normal issue. Use [GitHub private vulnerability reporting](https://github.com/QRun-IO/qqq-print-agent/security/advisories/new). Include the affected version, reproduction steps, impact and sanitized evidence. Do not send live enrollment codes, device credentials, customer documents or local print databases.

The initial release is a prerelease for controlled evaluation. Fixes target the latest prerelease; there is no long-term support commitment or independently audited production security certification.

## Trust and privileges

The machine service runs as LocalSystem to access machine-installed queues. The companion requires local administrator rights and communicates over an administrator-only named pipe. Treat a trusted QQQ server as authorized to supply printer documents to the explicitly exposed queues. RAW ZPL is printer control data and must originate from trusted generation workflows.

Device credentials are stored with DPAPI under the service identity in an MSI-restricted ProgramData directory. They are never included in tray snapshots. The local event journal may contain operational job IDs and error text; protect it as private operational data.

Remote QQQ endpoints require HTTPS. Plain HTTP is allowed only on loopback for development. Hash/length checks detect changed payloads; they do not make an untrusted PDF or ZPL document safe. Keep Windows, drivers and the OS renderer patched.

## Distribution

The v0.1.0-rc.1 MSI and first-party executables are **unsigned**. Download from the official repository release and compare SHA256SUMS. No Authenticode identity or provenance attestation is claimed. Production signing, renderer process isolation, broader driver testing and operational hardening remain open work.

See [the threat model](docs/security/threat-model.md) for implemented controls and residual risks.
