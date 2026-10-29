# Windows companion UX review — 2026-10-29

Scope: current `MainWindow.xaml`, `MainWindow.xaml.cs`, pipe timeout, tray lifecycle and runtime branding; compared with GCLO's Settings ContentDialog and MarkdownViewr.Windows' separate settings window. This is a focused happy-path review, not a full accessibility audit.

Evidence: the earlier running tray build was visually inspected at its 960×820 default size. It showed the red dot plus text, service-unavailable guidance, an expanded connection form occupying most of the first viewport, and printers below it. The native automation helper reported a higher-integrity boundary, so keyboard interaction, tray menu invocation and focus behavior were **not** tested. The latest footer, log dialog and branding changes were reviewed in source only; the running window available during this review was an older build.

## Priority findings

### Follow-up status before the prerelease

The three P1 findings below describe the initial review and have now been addressed in source: connection setup moved into a native `ContentDialog` with a first-run Connect action; background polling uses a separate `_refreshing` flag instead of disabling user controls; unchanged printer/job/log snapshots keep their existing UI objects; polling no longer replaces explicit operation feedback with generic health messages. The footer now identifies cached state when the service is unavailable. Changed printer inventories still rebuild their rows, so keyboard focus during an actual inventory change remains an acceptance check. These follow-up changes were source-reviewed; the final branded build still needs the elevated interactive checks below.

| Priority | Finding and consequence | Smallest useful change |
|---|---|---|
| P1 — addressed in source | Automatic polls previously disabled controls and replaced every list. | Separate poll state and unchanged-snapshot preservation are implemented. Verify stable focus on the final build; a shorter status timeout remains useful. |
| P1 — addressed in source | Polls previously overwrote explicit enrollment/validation/exposure results. | Generic health remains in the footer/overview; explicit operation feedback is retained. |
| P1 — addressed in source | The expanded connection form previously displaced the printer list. | Connection settings now use a native dialog and a first-run Connect action. |
| P2 | Printer rows mix exposure controls and status with driver, port and raw ID on every row. More than a few queues require an inner scroll region within the outer page. | Primary row: printer name, textual status, formats and exposure checkbox/switch. Put driver/port/ID in a Details expander or selected-printer panel. Keep local exposure distinct from QQQ approval. |
| P2 | Job history is an unstructured multiline string of status, UTC timestamp and IDs. There is no printer label, and raw enum values require interpretation. | Use aligned labeled fields: status, printer, local time, job ID/details. Use friendly status labels and preserve the precise raw state in details. Adding the printer label requires extending the safe snapshot DTO; do not infer it from a job ID. Keep the existing warning that SPOOLED does not prove physical output. |
| P2 | There are two service-log surfaces (page expander and footer modal), plus a generic Help → Activity log that is actually companion logs. The modal shows a snapshot and requires closing/reopening to refresh. | Keep the persistent View service logs button as the primary entry. Label the other source Companion activity log. Add Refresh/Copy in the service-log dialog and show its snapshot time; remove the duplicate inline log list if it adds no value. |
| P2 | Footer claims a five-second retry while a failed local-pipe request can occupy thirty seconds. Cached rows can remain visible after service loss without their own stale label. | Show Last checked and Last QQQ contact separately, format time for the operator, and mark cached printer/job data as stale when local reads fail. Make the stated retry interval match actual behavior. |
| P2 | The tray lifecycle hides on close and has an explicit exit command, but `Program.Main` has no single-instance activation. Launching again can create duplicate tray icons and competing details windows. | Use the Windows App SDK single-instance activation pattern to restore the existing details window. Keep Exit companion (printing continues) explicit. Do not silently add privileged startup behavior. |
| P2 | Runtime accent color is applied directly to heading text without contrast validation; long display/company names have no wrapping. Branded title/logo changes have not been visually verified. | Keep normal title text theme-colored or enforce contrast. Wrap/trim long branding labels gracefully. Verify no-brand, long-name, light/dark and missing-logo cases. Preserve standard health colors independently of customer accent. |

## Recommended information order

1. Compact branded header: device/server summary, Settings and Refresh.
2. Printer list as the main content: name, status, exposure; details on demand.
3. Recent jobs below, with a clear empty state and status explanations.
4. Fixed footer: color **and text**, last successful QQQ contact, View service logs.
5. Connection setup in a dialog. First-run Connect call to action may open it automatically only through an explicit user action.

The current implementation already gets several important details right: health is not color-only; enrollment uses a masked field and clears the code; printer exposure persists through the service rather than UI-only state; failures have actionable service guidance; closing the companion leaves the service running; About/theme/log foundations remain AppKit; the new footer sits outside the scrolling content.

## Acceptance still needed

- On an elevated interactive desktop, keyboard-tab to a printer toggle and wait through two refresh cycles; focus and state must remain stable.
- Enroll using a valid code and an invalid code; feedback must remain readable and secrets must not enter logs.
- Exercise actual green/orange/red service states and verify tray tooltip/menu, visible status and footer agree.
- Hide via the close button, reopen from tray, launch a second instance, then exit the companion; printing service must remain running.
- Check the details window at 680×600 and enlarged text, including branded long names, multiple printers and populated job history.
- Verify service-log/companion-log labels and stale/offline data behavior.

No source changes or additional physical print jobs were made as part of this review.
