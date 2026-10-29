# Using QQQ Print Agent

## Setup and status

Start **QQQ Print Agent** from the Start menu and accept the administrator prompt. Open **Connection settings** (or **Connect this device** on an unenrolled machine). Enter the full QQQ server origin, for example `https://warehouse.example.com`, and a fresh enrollment code from your QQQ administrator. Select **Connect**. The code is consumed once; later operation uses a device credential held by the background service.

The system tray shows green when connected and all exposed queues report ready, orange when connected but exposed printers need attention (including no printers selected), and red when disconnected or a recent job/runtime failure needs attention. Open the tray menu to show details. Closing the window hides the companion to the tray; exiting the companion still does not stop the service. If the service is not installed/running, the companion reports that it cannot reach the management pipe.

In **Printers**, check **Expose to QQQ** for the queues this computer should share. New installations expose none. The selection is saved by the service and takes effect in its next cloud inventory. Unselecting a queue prevents new local submissions but cannot retract paper already printing. In QQQ, review the exposed queues and explicitly enable the correct one. Local exposure and server approval are separate controls. Submit a small PDF test before sending packing slips. For Zebra labels, check printer language, DPI and label dimensions before the first real job.

## Job states

| State | Meaning and operator action |
| --- | --- |
| QUEUED | The cloud has accepted the document. |
| CLAIMED | This connector has been assigned the job. |
| RECEIVED | The connector has recorded receipt locally. |
| SUBMITTING | It is entering the Windows print path. |
| SPOOLED | Windows accepted the job; inspect the printer if no paper appears. |
| FAILED | The connector reported a failure before it could safely complete the attempt. Read the message. |
| OUTCOME_UNKNOWN | The attempt might have reached the printer. Inspect the printer/queue before submitting another copy. |

A ready queue or connected service does not guarantee that the printer has paper, ribbon or labels. This version reports Windows observations, not guaranteed hardware telemetry.

## Safe everyday operation

Keep the Windows computer awake and connected to the facility network. Leave the Spooler and QQQ Print Agent services running. Fix paper-out/jam conditions locally. Do not repeatedly submit the same shipping label to work around a delay: it may already be in the Windows queue.

A physical reprint is a new printing action; follow your QQQ application's workflow and preserve its audit trail. Automatic reprint/cancel controls are not provided by this first companion release.

## Jobs and logs

The details window shows the latest 50 local job states and the latest 100 service log entries. Logs are kept in memory for the running service session. No device credential or document bytes are included. The latest failed/uncertain job keeps health red until a later job succeeds; inspect the job before retrying. A red tray icon may also mean the service cannot be reached, not only that the cloud is down.


The bottom status bar remains visible while scrolling. It shows the same health as the tray and the last successful QQQ contact; the companion refreshes its observation every five seconds, with longer waits possible during a timeout. **View service logs** opens a selectable diagnostic snapshot immediately. Close and reopen it to refresh. **Help → Companion activity log** opens the separate UI log. After a connection failure, previously shown printer/job/log details may be stale.

Administrators can apply [customer branding](BRANDING.md) without rebuilding the app.
