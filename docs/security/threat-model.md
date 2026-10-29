# Threat model: QQQ Print Agent

Version: 0.1.0-rc.1. Reviewed: 2026-10-29. This is an implementation-level inventory, not an independent security audit.

## Boundaries and assets

The QQQ server supplies assigned document bytes over HTTPS. The Windows LocalSystem service validates document length/hash, records submission state, and calls OS renderer/spooler APIs. A locally elevated AppKit companion controls enrollment and printer exposure over an administrator-only named pipe. Local credentials, job IDs/events, Windows queue access and physical output are the protected assets.

| Boundary | Implemented control | Residual risk / qualification |
| --- | --- | --- |
| Internet to device | Outbound HTTPS, normal certificate validation; loopback-only HTTP development exception | Proxy/custom trust deployments not qualified |
| Device to server | Dedicated bearer credential; server enforces device/job scope | A compromised trusted server can supply hostile print content |
| Local UI to service | Pipe ACL permits SYSTEM/admins; narrow command set | Local administrators already control the machine; input allocation limits need further hardening |
| Exposure selection | Service-owned persisted allowlist; discovered-ID validation; filter inventory and recheck submission | Removal cannot retract data already accepted by Windows |
| Credentials at rest | DPAPI CurrentUser under service identity; restricted ProgramData ACL | Machine/service compromise defeats local protection |
| Payload download | 20 MiB cap, declared length and SHA-256 validation | Integrity does not sanitize PDFs or printer languages |
| Rendering/driver execution | Native Windows PDF/GDI/spooler APIs; bounded render dimensions | Runs in service process; malformed files/driver faults remain a privileged attack surface |
| RAW ZPL | Explicit compatible queue, trusted server workflow | ZPL can contain printer-control commands; not a sandboxed format |
| Duplicate output | Persistent receipts and events; uncertain submissions require review | Database and physical printer cannot share a transaction |
| Tray snapshot/logs | No device credential or document bytes; bounded recent jobs/logs | IDs/errors remain operational data and must be reviewed before sharing |
| Software distribution | Public source, pinned actions/dependencies, audited locked restore, checksums | Initial installer is unsigned; no attestation/production signing claim |

## Data handling

The service owns `connection.dpapi`, `journal.db` and its WAL. The MSI retains state through uninstall/reinstall. Revoke/decommission the device and resolve pending jobs before deliberately deleting retained state. Journal retention and automated redaction/export policy are open work; never publish raw state or customer documents.

Recent logs are an in-memory ring and recent jobs are bounded snapshot queries over the durable journal. Tray colors describe observed connectivity/queue state, not a guarantee of physical printer readiness or completed output.

## Follow-up

Qualify signed upgrades, renderer worker isolation, stricter bounded IPC reads, broad hardware/driver behavior, credential rotation/re-enrollment with pending work, cancellation and retention. Revisit this document when any network, privilege or persistence boundary changes.
