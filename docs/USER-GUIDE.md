# Using QQQ Print Agent

This guide describes **1.0.1**, the signed stable Windows release. Use it with qbit-printing **1.0.0** on QQQ **4.1.0-RC.21** or a compatible newer server. See [Acceptance](ACCEPTANCE.md) for dated installation and physical-print evidence.

## Menus

**File** provides **Settings**, **Close window** (hide to the tray), and **Exit companion**. Exiting the companion leaves the print service running. **Help** provides the shared **Activity log**, user guide, support, releases, project links, **Check for updates**, and **About**.

**Help → Check for updates** and the Settings update button use the same AppKit update flow. Installation always requires confirmation and updates the companion, service and CLI together. Settings also controls optional startup update checks, theme and splash duration. Ctrl+Shift+S opens Settings; Ctrl+Shift+L opens Logs.

## Connect and select printers

Open **QQQ Print Agent** from Start. In **Settings → Server connection → Sign in / Connection settings**, enter your HTTPS QQQ server address and select **Discover sign-in options**. Every step of discovery, sign-in, the hand-off to the service and the service's own verification is written to the activity log, and a failure's reason is shown in the dialog as well as logged; open **View logs** when something stops. For a development server with a self-signed or private-authority certificate, enter its address and turn on **Allow untrusted server certificate for this server**; both the companion and the Windows service then accept that one server's certificate, and no other server is affected. The service runs as LocalSystem and trusts only the machine certificate store, so a certificate that your user account trusts can still be refused by the service; either install the issuing root in the Local Machine Trusted Root store or use this option. With it on, any certificate is accepted and every session is logged with a warning, so turn it off for production servers. Sign in with the advertised username/password or browser flow. **Reuse the saved pairing** is the default; an older unbound device can be explicitly associated with your account. For a new pairing, obtain an enrollment code from QQQ and choose **Connect this device**. The Windows service holds the device credential; the companion never receives it. **Reconnect** requests another connection attempt. **Sign in again** restores user authentication while preserving the saved pairing and history. Changing servers or replacing a revoked pairing requires the explicit **Set up a new pairing** choice; see [QQQ sign-in](QQQ-SIGN-IN.md).

If enrollment times out, check QQQ before trying again: the code may already have been consumed. Generate a fresh code and revoke an unused/orphaned device through your administrator. An unreadable saved credential can be repaired by explicit enrollment; the previous encrypted file and journal are preserved.

In **Printers**, use the **Shared** checkbox for each intended queue, then approve it on the server. Local sharing and server approval are separate, and the **Server** column shows the second one for the server this computer is connected to: **Approved** (the server's printer catalog lists the printer for this computer), **Awaiting approval** (the server received the inventory but an administrator has not approved the printer), **Not reported** (the server could not be asked: an older server, or the signed-in user may not read the operator printer catalog; the reason is in the tooltip and the activity log) or **Not checked** (not connected yet). The service asks the server after each inventory and about once a minute while connected, so after a sign-in to a different server every shared printer starts as awaiting approval there until that server approves it. Select a printer to expand its information directly below that row. **Settings** and **Print test** appear at the top of the expansion; there is no fixed printer-details area below the list. The description preview is limited to three lines, and **Windows diagnostics** expands the driver, port and identifier.

Double-click the compact printer row, or select it and choose **Settings**, to open a wider editor. Text selection, sharing checkboxes and controls inside the expansion retain their own behavior. Read or edit the full description in Settings. **Print test** asks for confirmation, format and stock/resolution before sending one real test through QQQ. It offers Letter/A4 PDF and 4 × 6, 4 × 2 or 4 × 1-inch PDF/ZPL labels, with 203/300/600 dpi choices for ZPL. Compatible label queues default to ZPL; the suggested DPI must be checked against the printer. Virtual PDF, XPS, fax and save-to-file queues that need interaction are unavailable to the service. **Windows printer settings** opens the native queue setup page.

Printers initially sort by **Name ascending**. Click Name, Status, Location, Formats or Shared to sort that column; click it again to reverse direction. The active header shows an arrow and an accessible direction label. Long table values use ellipses and full tooltips; the expanded fields and editor expose the values separately from technical queue identity.

## Set machine media defaults before printing labels

PDF can use the machine default, an advertised media profile or explicit per-job dimensions. These job choices do not rewrite the shared Windows queue. Keep the machine default matched to the usual stock; see [Printer media](PRINTER-MEDIA.md). For a ribbonless direct-thermal printer loaded with 4 × 6-inch labels, open **Windows printer settings → your queue → Printer properties → Advanced → Printing Defaults**. Set width **4.00 inches**, height **6.00 inches**, and the printing method to **Thermal Direct** (driver wording may vary). Preserve unrelated settings. **Printing Preferences** for the signed-in user may not affect the LocalSystem service.

Use the method appropriate to your actual printer and supplies; Thermal Transfer expects ribbon. Check the printer's own mode and pause/error state as well as Windows. After correcting a mismatch, inspect existing output/queue state before requesting one new test. A job marked **SPOOLED** can still fail to produce paper or a label. See [label troubleshooting](TROUBLESHOOTING.md#zpl-prints-but-pdf-does-not).

## Jobs

The Jobs table aligns document title, printer, state, format and **Created** time. It initially sorts by **Created descending**, newest first. Creation time is retained from the job and does not change when a later status event arrives; last update time is shown separately in Details. If an older record has no creation time, the UI says **Not recorded** rather than inventing one.

Click Document, Printer, Status, Format or Created to change sorting; click the active header again to reverse it. Sorting applies to the entire matching result set before 50-row paging, including the bounded cloud queue preview. Changing sort returns to the first page. The direction indicator describes the last successfully loaded order; a failed refresh leaves the old rows with an unavailable/stale explanation.

Select a row to expand its details, recent events and actions directly below that row. Double-click the row or choose **Details** to open a wider dialog with labeled document/printer/outcome fields and a structured event history. The dialog follows the available window size and scrolls vertically. Previous and Next browse 50 results at a time. Search matches the beginning of a document title, printer name or job ID; choose a state or Needs attention to narrow results. Long table text is abbreviated with full tooltips; Details exposes selectable full values. Older jobs retain their IDs when their title was never recorded.

- **Details** shows local events and whether each was reported to QQQ.
- **Print again** creates a new server job after confirmation. Inspect output first, especially for uncertain or previously spooled work. The original job is never reset.
- **Cancel job** asks Windows to remove the exact spooled document. Pages already sent can still print. A reused spooler number belonging to another document is refused. QQQ also offers cancellation before dispatch through its job record/table actions.
- **Mark reviewed** clears a failed/uncertain job's attention indicator without changing its delivery outcome or printing again.

A timeout on a print action is not proof of failure. Inspect QQQ Jobs and Logs before repeating it. The operator-action features require the updated printing QBit; an older server produces an update/not-found message.

| State | Meaning |
| --- | --- |
| QUEUED | QQQ accepted the document; it has not yet been assigned. |
| CLAIMED / RECEIVED | Assigned to the connector / recorded locally. |
| SUBMITTING | Entering the Windows print path. |
| SPOOLED | Windows accepted the job; this does not confirm paper output. |
| FAILED | A definite failure or operator cancellation before dispatch; read its message. |
| OUTCOME_UNKNOWN | Submission might have reached the printer; inspect before reprinting. |

Detailed delivered history defaults to 30 days or the latest 300,000 detailed jobs. Pending events, active submissions, unreviewed failures and uncertain outcomes retain their evidence. Small final-state records remain for duplicate suppression. All retained local history is paged and searchable. Up to 50 server-queued jobs merge into the selected result order; a notice identifies this bounded queue preview. Its count is not the total of a larger cloud queue. Queued cancellation can fail if the server has already claimed a job. See [history and log limits](SCALABILITY.md).

## Computer and printer details

Use **Settings → Computer details** to edit this installation’s display name, description and location. Select a printer and choose its inline **Settings** button, or double-click its compact row, for the equivalent editor. Names/locations allow 512 characters; descriptions allow 4,096. Empty names fall back to Windows names. Metadata changes display values here and on the server; it does not rename Windows queues, change stable IDs or replace server approval. The expanded printer information exposes the actual queue name, with driver, port and ID under Windows diagnostics.

## Tray, logs and service

The footer and tray use text plus color: green for connected/ready, orange for exposed printer issues or no shared queues, and red for disconnection, failures or unreviewed uncertain jobs. The footer also shows the WebSocket connection, configured server and live upload/download rates, with totals since service start in the traffic tooltip. Select the server to open connection settings, or use **Reconnect** and **View logs**. These counters describe application traffic, not all network traffic on the computer. Printer status is a Windows observation, not guaranteed hardware telemetry.

**Logs**, **View logs**, and **Open activity log** all open the same built-in AppKit viewer. Service and companion activity are together, persisted under the protected machine data directory with configurable daily rotation and retention. Use **Follow**, **Errors only**, **Copy**, and **Open logs folder**. Credentials and document payloads must never be logged; local identifiers remain operational data.

**Settings → Diagnostic logs → Log settings** controls retention (1–365 days, default 30), the file size at which a day's log rolls over (1–1024 MiB, default 100), how many rolled-over segments of a day are kept (1–100, default 10) and **Debug logging**. A file that reaches the size limit is renamed to a numbered segment (`qqq-print-agent-yyyy-MM-dd.1.log`, `.2.log`, …) and logging continues in a fresh file; logging never stops because of size, and the oldest segments beyond the kept count are removed. Debug logging adds every request route, status code and timing, every realtime frame and full failure detail; credentials, documents and enrollment codes are never logged at any level. **Clear local logs** confirms removal of diagnostic logs only, preserving credentials, jobs, settings and journal history. The same controls are available on Logs.

Settings shows actual Windows service state and provides **Start service**, **Restart service**, **Windows Services**, and an installer link. Restarting can interrupt a submission; wait for active work when possible. The service continues printing when the companion exits.

Closing the window hides it to the tray. Reopening from Start restores the existing instance. **Show the tray companion when I sign in** explicitly enables a task for the current administrator and the installed executable; it is off by default. Upgrades retain this preference, and full uninstall removes verified application tasks. See [installation and security details](INSTALLATION.md).

Administrators can apply [customer branding](BRANDING.md) without rebuilding. Appearance follows Windows or your selected light/dark theme; unsafe light accents fall back for readable AppKit text.

## Branded warehouse test prints

Select a printer and choose **Print test**. Choose a document format and stock from the native option boxes, then confirm **Print one test**. PDF offers Letter, A4 or a 4 × 6, 4 × 2 or 4 × 1-inch label. ZPL offers those label sizes at 203, 300 or 600 dpi. Match the loaded stock and actual device resolution; the driver-name suggestion is only a hint. The default stays 4 × 6 until you pick another size, so load 4 × 1 or 4 × 2 stock before choosing it. PDF stock choices do not change Windows queue defaults; a 4 × 2 or 4 × 1 PDF sends its own actual-size paper for that job only, with the released qbit-printing 1.0.0 server.

QQQ generates the same branded diagnostics for desktop requests and its own printer record/table actions. The pages use Query, QQQ's mascot, large warehouse identifiers, dotted zeros and distinct 1/I/l glyphs from embedded JetBrains Mono Bold. PDF embeds the font; ZPL carries rasterized glyphs in bounded document graphics, so neither depends on a resident printer font. Label diagnostics include a Code 128 barcode on the ZPL path, and all layouts include size/alignment checks. Paper output and scanner readability must be checked physically.

Use qbit-printing 1.0.0 for the complete set of branded profiles and per-job media options. Older clients can omit the profile, defaulting to Letter PDF or 203-dpi 4 × 6 ZPL. Native QQQ actions can process selected printers; group devices with the same stock and label resolution. Connecting or discovering a device never automatically sends a test.
