# Troubleshooting

| Symptom | Check |
| --- | --- |
| Companion cannot connect to service | Confirm the MSI is installed, `QqqPrintAgent` is running, and the companion is elevated. A development CLI worker does not host the installed service pipe. |
| Enrollment fails | Check server URL, fresh/unconsumed code, device/facility configuration and server logs. Remote endpoints require HTTPS. |
| No printers in the service list | Install the queue for the machine/service identity. User-specific network mappings may not be visible to LocalSystem. Confirm Spooler is running. |
| Windows test page fails | Repair driver, connection or printer hardware locally before testing cloud printing. |
| Discovered printer never receives work | Enable the queue in QQQ, verify device/printer IDs and submit to that assigned queue. |
| Job remains queued | Check service status, authenticated realtime connection, current user/device authority, server pause/disconnect state, queue approval/exposure and configured capacity. The cloud preview is bounded; inspect server records for the full queue. |
| Realtime connection cannot start | Require printing extension 2.0 and allow outbound WSS upgrades to the configured origin. Inspect the safe connection/policy error; a 1.0 server or blocked socket does not enable HTTP polling fallback. |
| CONFIGURATION_REQUIRED | Have the QQQ administrator correct the complete effective policy or upgrade the incompatible client. Limits must fit declared client ceilings; reconnect does not silently lower them. |
| SPOOLED but no output | Check machine Printing Defaults, hardware media/method, pause and paper/label/ribbon conditions. A clear Windows queue or READY status is not proof of physical output. |
| OUTCOME_UNKNOWN | Inspect output and the Windows queue before reprinting. Do not delete the journal or assume failure. |
| PDF clipped or wrong size | Select actual size versus fit deliberately and configure correct default media in the Windows queue. Custom API media dimensions are not supported yet. |
| Zebra prints text or blank labels | Confirm ZPL-capable hardware/driver, DPI, stock dimensions and actual printer mode. Inspect the specific document; previously confirmed output does not qualify every format/driver combination. |
| Protected configuration cannot be opened | It belongs to another Windows identity, or the service account changed. Re-enroll through the service after resolving old jobs; do not copy a user-enrolled DPAPI file. |
| Another worker owns the directory | Stop the duplicate CLI worker. Only one worker may own a given journal/data directory. |
| MSI publisher warning | Current prerelease builds are unsigned. Verify the release checksum and follow local installation policy. |

## ZPL prints but PDF does not

The paths use different settings: RAW ZPL carries its format instructions, while PDF is rendered through the Windows driver using its defaults. The diagnostic media choice controls the generated document, not the machine driver's configuration. A default that works in a signed-in user's **Printing Preferences** may differ from the defaults used by the Windows service.

For 4 × 6-inch direct-thermal stock with no ribbon, inspect **Printer properties → Advanced → Printing Defaults** for the queue. Use **4.00 × 6.00 inches** and **Thermal Direct**, with the printer's correct DPI; preserve other settings. Other supplies may require different dimensions or Thermal Transfer. Also verify the physical printer's print method and pause/error state. Do not assume Windows READY or an empty queue rules out a hardware problem.

During 0.1.7 acceptance the machine defaults were 4.09 × 2-inch Thermal Transfer. The ZPL label printed, but the PDF did not despite SPOOLED. Correcting the machine defaults and hardware state allowed one fresh reviewed PDF to print fully and correctly; the user confirmed it. No application-code fix was needed. Inspect the existing job/output before retrying to avoid duplicates. See [acceptance evidence](ACCEPTANCE.md).

For a non-security bug, include Windows version, application version, printer model/driver, the stage that failed, job ID and sanitized error text in a [bug report](https://github.com/QRun-IO/qqq-print-agent/issues/new/choose). Never attach credentials, enrollment codes, customer PDFs, address labels, journal databases or raw diagnostic archives without reviewing them.

Security issues belong in [private reporting](../SECURITY.md).
