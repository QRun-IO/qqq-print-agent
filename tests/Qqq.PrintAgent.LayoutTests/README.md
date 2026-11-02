# Native nonprinting layout regression host

This executable compile-links the actual companion views, XAML and event handlers. It substitutes only application startup, the activity-log destination and the injected service-request delegate. The production default constructor still uses the real pipe client. No test route or environment override is added to production service transport.

The host starts a separate native WinUI window and private presentation profile with splash and startup update checks disabled. Synthetic inventory contains two exposed physical queues and two interactive queues. It selects and expands the last printer, invokes its actual Print test button through an in-process native automation peer, invokes the real dialog's Primary button, receives the exact production success message, and exercises feedback, refresh and navigation. Unexpected service requests fail the fixture; it never calls a printer, service pipe or HTTP server. Initial service/startup status inspection remains read-only, as in the real window.

Native layout tracing is enabled at High. Any unhandled UI exception writes `unhandled.txt` and fails the process. Success writes `result.json`, including root dimensions, scale and request counts. These peer-based checks do not establish physical mouse/keyboard responsiveness or replace interactive acceptance. A passing run means that this particular geometry did not reproduce the layout exception.

Build separately from the normal solution (existing pinned dependencies only):

```powershell
dotnet build tests/Qqq.PrintAgent.LayoutTests/Qqq.PrintAgent.LayoutTests.csproj -p:Platform=x64
```

Run the bounded script on an interactive Windows desktop with one new evidence directory. It builds the host, launches only that executable, verifies the result, and stops only its own child if it exceeds 35 seconds. The host creates a private `profile` child. It has an `asInvoker` manifest and does not request elevation.

Optional second and third arguments set the native window width and height in pixels (800–1400 by 560–1000). Defaults are 1100 by 760. Each run records actual client dimensions and scale; no system DPI setting is modified.

An optional fourth argument, `--refresh-during-dialog`, holds the actual dialog open for 6.5 seconds. Only the window's real five-second timer triggers the intervening snapshot request. The synthetic response changes selected-printer metadata, job history, server-contact time and last-command footer data. The scenario requires those metadata/footer changes to render while the dialog remains open and no submission has occurred; it then invokes Primary and verifies the normal feedback/refresh/navigation path.

```powershell
./tests/Qqq.PrintAgent.LayoutTests/Run.ps1 -EvidenceDirectory (Join-Path $env:TEMP ('qqq-layout-' + [guid]::NewGuid().ToString('N')))
```

No real printing, connection enrollment, settings commits, service restart, startup-task changes or updater execution occurs. Logs and synthetic reports must remain separate from installed service data.

The footer check uses the real `ApplicationTrafficMeter` with synthetic byte increments, verifies positive rates followed by zero after an idle window while totals remain unchanged, and invokes the actual server-link peer. It requires the existing connection dialog's native Opened/Closed events and a null NavigateUri; cleanup uses ContentDialog.Hide. It does not sign in or navigate to an external website.

The About check renders the shared AppKit dialog with a long synthetic customer
name and the production Query section in both light and dark themes. It requires
the bundled PNG to decode and verifies rendered black artwork on white. The
mascot, both links and full customer name must fit the opening viewport before
scrolling; the original credits and Close action remain reachable afterward.
It retains full synthetic dialog and card captures for review, without
activating the website links.

This is a manual native desktop gate. It has no external input or printing dependency, but it is not a headless unit test and has not been qualified on a GitHub-hosted runner. It requires PowerShell 7, the repository's .NET 10 build prerequisites and a working WinUI desktop. The native Windows App SDK components are copied by the build; the executable still uses the installed .NET runtime. No mandatory CI step is added until that runner environment is independently qualified.
