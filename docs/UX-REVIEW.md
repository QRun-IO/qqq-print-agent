# Windows companion UX review — 2026-10-30

## Scope and evidence

This review separates current source behavior from installed acceptance. Installed 0.1.5 passed 12 interactive checks across a ten-test run and a focused two-test run. The subsequent 0.1.6 source changes below have not yet completed their source gate or installed acceptance. No new physical print was performed for this review.

The independent 0.1.6 source review inspected inline printer selection, double-click actions, dialog layout and lifetime, sortable headers, displayed sort keys and full-result paging. Reference patterns remain native WinUI NavigationView and menus, GCLO/MarkdownViewr settings, and the single built-in AppKit LogWindow. Source inspection does not establish Narrator, keyboard or resize behavior on an installed application.

## Current workflow matrix

| Workflow | Current implementation | Remaining acceptance or limitation |
| --- | --- | --- |
| Shell and navigation | File/Help menus; left Printers, Jobs, Logs and Settings; persistent connection footer. Shared AppKit About, update prompts, theme and splash preferences. | Inspect new content at default/minimum sizes, keyboard navigation and display scaling. Prior installed shell tests do not qualify new tables. |
| Printer inventory | Compact sortable name/status/location/formats/sharing columns, initially Name ascending. Selection expands information inside the row, with Settings/Print test first, labeled fields, a three-line description preview and a diagnostics expander. Double-clicking the compact row opens Settings. | Verify keyboard selection/focus, large names, checkbox interactions and status/removal changes. The inline subtree and selectable text are excluded from row double-click handling in source. |
| Device/printer metadata | Settings edits computer name, description and location; selected printers have equivalent editors. Local service persistence and outbound synchronization preserve technical names and stable IDs. Names/locations have 512-character bounds, descriptions 4,096. | Verify save/reopen/restart/synchronization and explicit clearing. Friendly labels do not rename Windows queues. |
| Jobs | Sortable document/printer/state/format/Created columns, initially immutable Created descending. Ordering covers matching local history and the bounded cloud preview before 50-row paging. Status sort uses displayed labels; missing metadata uses displayed fallbacks. Double-click or Details opens labeled summary fields and event columns. | Verify ordering across pages, changing filters and status updates, and missing historical creation times. Live offset boundaries can move as new jobs arrive. Cloud preview is limited to 50 and visibly disclosed. |
| Actions | Confirmed server-mediated tests/reprints, queued or identity-checked spooler cancellation, and failure-review acknowledgement. Unknown outcomes retain duplicate-risk warnings. | Physical output and operational action acceptance remain separate. Spool acceptance cannot prove paper output. Zebra qualification is pending. |
| Logs | Every logs entry point reuses the built-in AppKit viewer with both service/companion sources. Settings and Logs offer the same retention/daily-size editor and confirmed clearing. Usage/cap status is shown in the editor; daily rotation follows local calendar date. | Verify viewer discovery/reuse, both sources, cap/resume and clear confirmation on installed build. Limits are 1–365 days and 1–100 MiB; clearing preserves job evidence and credentials. |
| Service and startup | Settings shows actual service state, bounded Start/Restart, Windows Services and installer guidance. Restart requires confirmation. Explicit per-user elevated sign-in task is opt-in and fixed to the installed executable. | Verify missing/stopped/access-denied states, sign-in/reboot, task removal and recovery. Standard users require administrator authorization for management. |
| Enrollment and reconnect | Masked code and validated address; explicit submission; actionable consumed-code/timeout guidance. Replacement preserves prior credentials until saved. Reconnect requests a fresh poll. | A lost enrollment response may consume its code; verify server state before retrying. A reconnect request does not prove connectivity. |
| Refresh and failures | Background polling does not globally disable controls. Printer rows remain stable; job rows are reused by ID within the current page. Mutation/query generations discard outdated responses. EOF becomes an I/O failure; ordinary commands are bounded to 35 seconds. | Verify selection/focus while rows change and actions time out. A timed-out command may have reached the server or printer. |
| Tray and lifetime | Corrected native icon ownership, shared health/text, hide to tray and same-profile relaunch. Explicit companion exit leaves service printing. | User confirmed installed tray visibility; prior hide/relaunch test passed. Explorer restart, all health transitions and sign-in still need qualification. |
| Branding and long values | Runtime identity/logo/support, theme foregrounds and validated accent contrast. Table cells use ellipsis plus full tooltips; details/editor surfaces wrap and expose full values. | Inspect branded builds, long names, light/dark/high contrast and missing logos. Branding reload requires restart; fixed upgrade/service identity is preserved. |
| Accessibility | Native controls, accessible row/action labels, text plus color, live health region, selectable details and DPI-aware windows. | Narrator, focus order, high contrast, large text and multi-monitor checks remain unverified. Source annotations are not certification. |

## 0.1.6 review corrections

- Replaced a single unbounded printer text block with labeled fields and a three-line description preview. Actions appear first; the full description remains in Settings.
- Excluded buttons, checkboxes, editable/selectable text and the inline-details subtree from row double-click handling. Double-clicking text to select a word should not launch an editor.
- Kept wide-dialog size resources scoped to each dialog. Content dimensions follow `XamlRoot.Changed` while open, and the handler is removed on close.
- Reused the displayed printer status for sorting. Job ordering uses human-readable status labels and the same title/printer fallbacks shown in rows.
- Committed job sort arrows and accessible direction labels only after a successful query, avoiding old rows under a new order indicator when the service fails.

These corrections were confirmed by source review. Their build, automated regressions and installed interaction checks are pending for 0.1.6.

## Next installed checks

- Inspect table headers/columns at small and large window sizes; select rows with keyboard, change filters/pages, and confirm full long values remain available.
- Resize an open printer-settings/job-details dialog; double-click selectable expanded text; operate the Shared checkbox; verify those actions do not unexpectedly open a second dialog.
- Confirm Name ascending and Created descending defaults, both sort directions across multiple pages, and a failed query retaining the prior applied indicator. Ensure a status update does not change a job's Created value.
- Save and clear device/printer metadata, restart the service, and verify synchronization without changed IDs or queue names.
- Confirm log viewer reuse, daily-limit feedback, retention settings and diagnostic-only clearing.
- Exercise unavailable-service and action-error feedback, then verify stale polls cannot overwrite newer operations.
- Preserve prior hardware evidence: the user confirmed two earlier laser pages. Any further physical test is coordinated separately; Zebra remains deferred.

## Historical observations

The early build had a long scrolling setup page, duplicated log viewers, an expanded enrollment form and refresh that rebuilt controls. Later reviews identified missing service controls, actions, startup preference and accent constraints. These observations explain completed changes and are not descriptions of the current source.

Historically, installed 0.1.4 had a zero-warning source gate and 163 unit/regression tests. Its interactive suite passed ten of eleven checks; log-window discovery timed out and the focused rerun did not execute. The user confirmed its tray icon worked. The later 0.1.5 installed results above supersede that interactive checkpoint for the checks actually rerun, but neither version qualifies the new 0.1.6 interactions.
