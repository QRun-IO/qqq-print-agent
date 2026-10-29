# Windows companion UX review — 2026-10-29

## Scope and evidence

This review describes the current source for the native Windows 11 companion. It separates implemented behavior from installed, interactive acceptance. The latest source build passed with zero warnings/errors. The final automated gate passed 102 unit/regression tests (36 Core and 66 Windows/CLI), including heartbeat and queued-cancellation corrections. The elevated interactive UI suite has not been reported as executed.

The current installer upgrade, UAC approval and installed UI review remain pending. No current visual acceptance, tray acceptance or additional physical-output confirmation is implied by a successful build.

Reference implementations inspected were GCLO's NavigationView and settings dialog, MarkdownViewr.Windows settings/navigation and single-instance patterns, and AppKit's built-in `LogWindow`. The companion uses the shared AppKit viewer for all logs.

## Current workflow matrix

| Workflow | Implemented in current source | Remaining acceptance or limitation |
| --- | --- | --- |
| Navigation | Native left navigation: Printers, Jobs, Logs and standard Settings. Printers opens first; lists and Settings have independent page scrolling. A connection footer stays outside scrolling content. | Inspect default 1100×760 and minimum 800×560 sizes, small work areas, keyboard navigation and long labels. |
| Service installation and control | Settings distinguishes service status, provides bounded Start/Restart, Windows Services and installer links, and actionable missing-service guidance. Restart requires confirmation because in-flight printing may have an uncertain outcome. | Installation remains an external MSI action. Verify missing/stopped/access-denied cases, recovery and installed service controls. |
| Enrollment | Native connection dialog validates the address, masks the code, submits explicitly and clears a submitted code. Timeout guidance explains checking QQQ for consumed codes, revoking unused devices and obtaining a fresh code. Replacement retains prior credentials until successful persistence. | Verify valid/invalid/expired codes, lost responses and recovery. A network timeout can still consume a code; enrollment is not an atomic transaction across cloud and local persistence. |
| Reconnect | Footer Reconnect requests a fresh service poll; Settings offers Sign in again. Subsequent snapshots establish connection status. | Verify outage recovery and unconfigured-device guidance; requesting reconnect does not itself prove connectivity. |
| Printers | Rows show name, readable status, supported formats and sharing. Driver, port and ID are expandable. Windows printer settings is available. Unsupported interactive queues cannot be newly shared or tested; previously selected queues can be withdrawn. Missing queues retain an offline explanation. | QQQ approval is independent of local sharing. Verify real queues, removal, approval, exposure persistence and failure feedback. |
| Refresh and focus | Rows are retained by printer ID even when inventory changes. Background polling has its own guard and does not globally disable controls. Mutation generations discard older snapshot responses that could overwrite newer action results. | Verify keyboard focus, expanded details and page position across status changes, exposure changes and device removal. A removed queue necessarily removes its row. |
| Jobs | Friendly state/time, document title, printer identity and format; search and status filters; selectable detailed identifiers and retained event history. Older records without metadata show an explicit fallback. | History is bounded, with retention disclosures. Verify populated/mixed history, long text, filters, event details and changes while viewing. Spool acceptance is not physical-output confirmation. |
| Job and printer actions | Printer test sends an explicitly confirmed, server-mediated test. Print again, cancellation and review acknowledgement use confirmations and current backend policy. Retry warns about duplicate output; cancellation warns that emitted pages may still print. | Verify server approval/rejection, queued and spooler cancellation, timeout feedback and duplicate-risk consent. No additional print was performed for this review. Zebra remains pending. |
| Action failure and timing | Pipe connection timeout is 3 seconds; ordinary requests are bounded to 35 seconds and enrollment to 150 seconds. EOF now throws an I/O failure rather than becoming a success message. Unknown action outcomes tell the operator to inspect Jobs/Logs before retrying. | Verify service termination and delayed replies while commands run. A timeout cannot prove an action did not reach QQQ or the printer. |
| Logs | Navigation, footer and launcher reuse one built-in AppKit viewer. Both service and companion write a protected, serialized, redacted daily log with source labels and severity. AppKit provides Follow, Errors only, Copy and Open logs folder. | Verify both sources, viewer reuse, filters and persistent logs while service is stopped. Machine log access requires the elevated companion. |
| Tray and lifecycle | Status icon is loaded synchronously; footer and tray use the same health. Close hides only when a tray exists; explicit Exit leaves the service running. A second launch signals and restores the existing instance for the same user/session/profile. | Native icon decoding passed earlier, but actual shell visibility, click/menu, hide/restore, Explorer restart and second-launch behavior still need installed observation. |
| Sign-in startup | Explicit administrator opt-in creates a per-user interactive elevated task for the fixed MSI-installed executable. It launches with `--tray`; hiding runs after activation. No task is registered merely by opening the app. Service startup is independent. | Verify opt-in/out, sign-in, UAC behavior and task cleanup. Development/portable executables cannot enable elevated startup. Standard users require administrator authorization for the management companion. |
| Branding and settings | Theme/splash preferences, customer identity/logo/support and AppKit About. Main headings use theme foreground. White-text brand accent contrast must be at least 4.5:1; unsuitable accents fall back to the default brand. Stable MSI/service identities are retained. | Verify light/dark/high contrast, missing logos, long names and persistence. Branding reload requires restart. |
| Accessibility | Native controls, accessible printer/action labels, selectable diagnostic text, color plus health text, live-region health notification and per-monitor DPI support. | Narrator, focus order, high contrast, large text and multiple-monitor/scaling checks are unverified. Source annotations are not accessibility certification. |

## Installed acceptance still required

- Complete the current MSI upgrade and inspect all four navigation destinations. Confirm footer and actions remain visible and reachable.
- Run the prepared elevated interactive suite, including same-profile hidden-window relaunch, read-only job search/filter and lifecycle control discovery. It does not enroll or print automatically.
- Verify shared log viewer reuse and both event sources, service controls, enrollment failures, reconnect, exposure persistence and stale-state recovery.
- Observe green/orange/red tray and footer states, close/reopen, explicit companion exit, and opt-in sign-in startup.
- Perform any further physical print only through the release owner's coordinated acceptance. Preserve the distinction between spooler evidence and user-confirmed paper output.

## Historical observations

The early 960×820 build had one long scrolling setup page, duplicated service/activity log surfaces and an expanded enrollment form that displaced printers. Background refresh initially disabled controls and rebuilt rows. Those observations motivated the implemented navigation, unified AppKit logs, connection dialog and stable-row changes above; they are not descriptions of the current source.

Earlier reviews also identified missing service controls, job metadata/actions and sign-in preference, plus unrestricted brand accent contrast. Those source gaps are now addressed. Their installed behavior remains subject to the acceptance list, rather than being claimed from code inspection alone.
