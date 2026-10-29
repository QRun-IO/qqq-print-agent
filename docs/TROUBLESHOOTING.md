# Troubleshooting

| Symptom | Check |
| --- | --- |
| Companion cannot connect to service | Confirm the MSI is installed, `QqqPrintAgent` is running, and the companion is elevated. A development CLI worker does not host the installed service pipe. |
| Enrollment fails | Check server URL, fresh/unconsumed code, device/facility configuration and server logs. Remote endpoints require HTTPS. |
| No printers in the service list | Install the queue for the machine/service identity. User-specific network mappings may not be visible to LocalSystem. Confirm Spooler is running. |
| Windows test page fails | Repair driver, connection or printer hardware locally before testing cloud printing. |
| Discovered printer never receives work | Enable the queue in QQQ, verify device/printer IDs and submit to that assigned queue. |
| Job remains queued | Check service status, outbound network access, device authorization and polling. |
| SPOOLED but no output | Check Windows queue and printer paper/labels/jam conditions. Spool acceptance is not physical completion. |
| OUTCOME_UNKNOWN | Inspect output and the Windows queue before reprinting. Do not delete the journal or assume failure. |
| PDF clipped or wrong size | Select actual size versus fit deliberately and configure correct default media in the Windows queue. Custom API media dimensions are not supported yet. |
| Zebra prints text or blank labels | Confirm ZPL-capable hardware/driver, correct DPI/label dimensions and ZPL document. Physical Zebra acceptance is pending. |
| Protected configuration cannot be opened | It belongs to another Windows identity, or the service account changed. Re-enroll through the service after resolving old jobs; do not copy a user-enrolled DPAPI file. |
| Another worker owns the directory | Stop the duplicate CLI worker. Only one worker may own a given journal/data directory. |
| MSI publisher warning | v0.1.0-rc.1 is unsigned. Verify the release checksum and follow local installation policy. |

For a non-security bug, include Windows version, application version, printer model/driver, the stage that failed, job ID and sanitized error text in a [bug report](https://github.com/QRun-IO/qqq-print-agent/issues/new/choose). Never attach credentials, enrollment codes, customer PDFs, address labels, journal databases or raw diagnostic archives without reviewing them.

Security issues belong in [private reporting](../SECURITY.md).
