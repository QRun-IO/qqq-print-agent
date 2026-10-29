# Threat model: QQQ Print Agent

Version: 0.1.1-rc.1. Reviewed: 2026-10-29. This is an implementation-level inventory, not an independent security audit.

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
| Rendering/driver execution | Native Windows PDF/GDI/spooler APIs; bounded render dimensions | Disposable child with bounded timeouts and parent-exit cleanup; child retains service privileges, so this is fault isolation, not a security sandbox |
| RAW ZPL | Explicit compatible queue, trusted server workflow | ZPL can contain printer-control commands; not a sandboxed format |
| Duplicate output | Persistent receipts and events; uncertain submissions require review | Database and physical printer cannot share a transaction |
| Tray snapshot/logs | No device credential or document bytes; bounded recent jobs/logs | IDs/errors remain operational data and must be reviewed before sharing |
| Software distribution | Public source, pinned actions/dependencies, audited locked restore, checksums | Initial installer is unsigned; no attestation/production signing claim |

## Data handling

The service owns `connection.dpapi`, `journal.db` and its WAL. The MSI retains state through uninstall/reinstall. Revoke/decommission the device and resolve pending jobs before deliberately deleting retained state. Only delivered terminal detail whose failures were reviewed is pruned; final-state evidence is retained. Never publish raw state or customer documents.

Service and companion persist serialized, credential-redacted AppKit logs with 30-day retention; recent job snapshots come from durable connection-scoped metadata. Tray colors describe observed connectivity/queue state, not a guarantee of physical printer readiness or completed output.

## Follow-up

Qualify signed upgrades, stricter bounded IPC reads and broad hardware/driver behavior. Regression coverage now includes interrupted jobs, credential recovery, retention, operator actions and bounded child-process termination. Revisit this document when any network, privilege or persistence boundary changes.

Runtime branding is presentation-only, loaded from the existing administrator-restricted ProgramData directory. JSON size, text length, color syntax, HTTPS support links, and local PNG file size/dimensions are validated. No remote logo fetch occurs; malformed settings fall back to defaults. Machine identity, credentials and authorization are unaffected.

Sign-in startup requires an explicit administrator preference in the installed companion. The task uses that administrator's interactive identity and a fixed Program Files executable/arguments; it stores no password. Same-name foreign tasks are refused. Full uninstall deletes only exact owned definitions; upgrades preserve them. Device operator actions remain scoped to authenticated device jobs and approved/exposed printers; reprint creates a new job and requires duplicate-risk consent for uncertain/spooled originals.
