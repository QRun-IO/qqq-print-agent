# Using QQQ Print Agent

## Connect and select printers

Open **QQQ Print Agent** from Start. In **Settings → QQQ connection**, choose **Connect this device**, enter your HTTPS QQQ server address and a fresh enrollment code, then connect. The Windows service holds the device credential; the companion never receives it. **Reconnect** immediately requests another connection attempt. **Sign in again** replaces the connection using a fresh code while preserving prior job evidence.

If enrollment times out, check QQQ before trying again: the code may already have been consumed. Generate a fresh code and revoke an unused/orphaned device through your administrator. An unreadable saved credential can be repaired by explicit enrollment; the previous encrypted file and journal are preserved.

In **Printers**, use **Share with QQQ** for each intended queue, then approve it in QQQ. Local sharing and server approval are separate. **Print test** asks for confirmation and PDF/ZPL format before sending one real test through QQQ. Virtual PDF, XPS, fax and save-to-file queues that need interaction are unavailable to the service. **Windows printer settings** opens the native queue setup page.

## Jobs

The Jobs page shows document title, printer, format, state and time. Search by document/printer/job, or filter to work needing attention, active work or spooler submissions. Unreviewed failures appear first so newer successes cannot hide them. Legacy jobs retain their IDs when their title was never recorded.

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

Detailed delivered history is retained for 30 days or the latest 1,000 detailed jobs. Pending events and unreviewed failures are never pruned. Small final-state records remain to prevent duplicate printing. The visible history contains up to 50 local jobs, prioritizing those requiring review, plus up to 50 jobs still queued in QQQ. Queued jobs can be cancelled before dispatch; if QQQ has already claimed one, cancellation reports that state instead.

## Tray, logs and service

The footer and tray use text plus color: green for connected/ready, orange for exposed printer issues or no shared queues, and red for disconnection, failures or unreviewed uncertain jobs. Printer status is a Windows observation, not guaranteed hardware telemetry.

**Logs**, **View logs**, and **Open activity log** all open the same built-in AppKit viewer. Service and companion activity are together, persisted under the protected machine data directory with 30-day log retention. Use **Follow**, **Errors only**, **Copy**, and **Open logs folder**. Credentials and document payloads must never be logged; local identifiers remain operational data.

Settings shows actual Windows service state and provides **Start service**, **Restart service**, **Windows Services**, and an installer link. Restarting can interrupt a submission; wait for active work when possible. The service continues printing when the companion exits.

Closing the window hides it to the tray. Reopening from Start restores the existing instance. **Show the tray companion when I sign in** explicitly enables a task for the current administrator and the installed executable; it is off by default. Upgrades retain this preference, and full uninstall removes verified application tasks. See [installation and security details](INSTALLATION.md).

Administrators can apply [customer branding](BRANDING.md) without rebuilding. Appearance follows Windows or your selected light/dark theme; unsafe light accents fall back for readable AppKit text.
