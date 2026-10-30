# Windows companion UX review — 2026-10-30

## Scope and evidence

This review separates current source behavior from installed acceptance. Installed 0.1.4 passed 163 unit/regression tests and a zero-warning source gate. Its interactive suite passed 10 of 11 checks, including menus/About, splash and close/relaunch; log-window discovery timed out, and its focused rerun did not execute. The user explicitly confirmed that the tray icon now works. No new physical print was performed for the current changes.

The table, paging, metadata and diagnostic-limit changes below are newer than that installed version. Their source gate and installed visual/interactive acceptance are pending. Reference patterns remain native WinUI NavigationView and menus, GCLO/MarkdownViewr settings, and the single built-in AppKit LogWindow.

## Current workflow matrix

| Workflow | Current implementation | Remaining acceptance or limitation |
| --- | --- | --- |
| Shell and navigation | File/Help menus; left Printers, Jobs, Logs and Settings; persistent connection footer. Shared AppKit About, update prompts, theme and splash preferences. | Inspect new content at default/minimum sizes, keyboard navigation and display scaling. Prior installed shell tests do not qualify new tables. |
| Printer inventory | Compact aligned name/status/location/formats/sharing columns. Stable rows retain identity across polls. Select a row for Details, Edit details or confirmed Print test; Windows printer settings is available. Unsupported interactive queues cannot be newly shared/tested, but old selections can be withdrawn. | Verify selection/focus across status/removal changes, long labels, real queue metadata and independent server approval. |
| Device/printer metadata | Settings edits computer name, description and location; selected printers have equivalent editors. Local service persistence and outbound synchronization preserve technical names and stable IDs. Names/locations have 512-character bounds, descriptions 4,096. | Verify save/reopen/restart/synchronization and explicit clearing. Friendly labels do not rename Windows queues. |
| Jobs | Aligned document/printer/state/format/time columns, single selection and selected-row actions. Service queries page 50 results with indexed prefix search and state/attention filters across durable history. Full selectable details retain identifiers/events. | Inspect populated multi-page history, filters and long values. Live offset boundaries can move as new jobs arrive. Cloud-queued preview is limited to 50 and visibly disclosed; its count is not an unlimited cloud total. |
| Actions | Confirmed server-mediated tests/reprints, queued or identity-checked spooler cancellation, and failure-review acknowledgement. Unknown outcomes retain duplicate-risk warnings. | Physical output and operational action acceptance remain separate. Spool acceptance cannot prove paper output. Zebra qualification is pending. |
| Logs | Every logs entry point reuses the built-in AppKit viewer with both service/companion sources. Settings and Logs offer the same retention/daily-size editor and confirmed clearing. Usage/cap status is shown in the editor; daily rotation follows local calendar date. | Verify viewer discovery/reuse, both sources, cap/resume and clear confirmation on installed build. Limits are 1–365 days and 1–100 MiB; clearing preserves job evidence and credentials. |
| Service and startup | Settings shows actual service state, bounded Start/Restart, Windows Services and installer guidance. Restart requires confirmation. Explicit per-user elevated sign-in task is opt-in and fixed to the installed executable. | Verify missing/stopped/access-denied states, sign-in/reboot, task removal and recovery. Standard users require administrator authorization for management. |
| Enrollment and reconnect | Masked code and validated address; explicit submission; actionable consumed-code/timeout guidance. Replacement preserves prior credentials until saved. Reconnect requests a fresh poll. | A lost enrollment response may consume its code; verify server state before retrying. A reconnect request does not prove connectivity. |
| Refresh and failures | Background polling does not globally disable controls. Printer rows remain stable; job rows are reused by ID within the current page. Mutation/query generations discard outdated responses. EOF becomes an I/O failure; ordinary commands are bounded to 35 seconds. | Verify selection/focus while rows change and actions time out. A timed-out command may have reached the server or printer. |
| Tray and lifetime | Corrected native icon ownership, shared health/text, hide to tray and same-profile relaunch. Explicit companion exit leaves service printing. | User confirmed installed tray visibility; prior hide/relaunch test passed. Explorer restart, all health transitions and sign-in still need qualification. |
| Branding and long values | Runtime identity/logo/support, theme foregrounds and validated accent contrast. Table cells use ellipsis plus full tooltips; details/editor surfaces wrap and expose full values. | Inspect branded builds, long names, light/dark/high contrast and missing logos. Branding reload requires restart; fixed upgrade/service identity is preserved. |
| Accessibility | Native controls, accessible row/action labels, text plus color, live health region, selectable details and DPI-aware windows. | Narrator, focus order, high contrast, large text and multi-monitor checks remain unverified. Source annotations are not certification. |

## Next installed checks

- Inspect table headers/columns at small and large window sizes; select rows with keyboard, change filters/pages, and confirm full long values remain available.
- Save and clear device/printer metadata, restart the service, and verify synchronization without changed IDs or queue names.
- Confirm log viewer reuse, daily-limit feedback, retention settings and diagnostic-only clearing.
- Exercise unavailable-service and action-error feedback, then verify stale polls cannot overwrite newer operations.
- Preserve prior hardware evidence: the user confirmed two earlier laser pages. Any further physical test is coordinated separately; Zebra remains deferred.

## Historical observations

The early build had a long scrolling setup page, duplicated log viewers, an expanded enrollment form and refresh that rebuilt controls. Later reviews identified missing service controls, actions, startup preference and accent constraints. These observations explain completed changes and are not descriptions of the current source. Installed evidence is scoped above rather than inferred from source review.
