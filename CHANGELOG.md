# Changelog

## Unreleased

Two cases the print worker now settles with a known outcome (2.0).

- A printer that stops being shared while jobs wait behind a print no longer leaves those jobs with an unknown
  outcome. The filter that checks sharing at the moment of submission refuses before anything is handed to Windows,
  and its refusal now has a type of its own, so the worker records the job as FAILED with a reason ending "no bytes
  were submitted" where it used to record OUTCOME_UNKNOWN. History shows RECEIVED, SUBMITTING, FAILED. The printer
  is still discovered once per job after the download, as before, and not a second time while the job holds the
  printer's turn. A selected media default that changed during the wait is refused by the print helper before it
  prints and is FAILED, as before. A queue that was removed from Windows during the wait is still
  OUTCOME_UNKNOWN: the print helper finds it missing before it sends anything but cannot yet say so in a form the
  worker can rely on.
- A job the server offers again while a lane of this computer still holds it is left to that lane. The log says so
  in plain words, nothing is recorded for the replay, and the lane ends its pass. Before, the replay was stopped
  only by an internal duplicate check that logged "An item with the same key has already been added". A lane
  also lets go of a job whose receipt was interrupted when the journal then fails to record that failure; before,
  the job stayed marked as held until the service restarted, and every later offer of it was turned away.

Branded Start menu shortcut (2.0). The installer step that creates, renames and removes the shortcut under
**Start → Print Connectors** now records, by name, every shortcut it has put there and may still own, and a file
that is not ours no longer fails an install or an uninstall. Nothing changes for an installation whose shortcut
was never touched by anything else.

What counts as ours, here and below: a shortcut that starts the installed companion with no arguments and carries
this installer's description. Someone else's shortcut, a file that is not a shortcut at all (empty, or other
content) and a link are not ours. A file that cannot be opened, because another program holds it, is neither: it is
not judged, since it may well be ours.

- **Install, upgrade or repair with a changed display name.** The new shortcut is written first and the previous
  brand's shortcut is removed afterwards, as before. What is different:
  - If the previous shortcut cannot be deleted, or cannot be opened for the look taken just before the delete,
    the install now succeeds. It used to fail, with both shortcuts on the Start menu and nothing left to remove
    the old one later. The old shortcut stays for the moment, a warning in the activity log names it and the
    reason, and the next install, upgrade, repair or uninstall removes it.
  - The previous shortcut is looked at again immediately before it is deleted, and is left alone if it was
    replaced in the meantime.
  - If the file under the previous name is not ours, or the recorded name is not a shortcut file name in that
    folder, it is left unchanged with a warning and the new shortcut is created. This used to fail the install
    after the new shortcut had been written, and every install after it.
- **Any install, upgrade or repair.**
  - A file that is not ours under the name the shortcut needs is never overwritten. The name is looked at before
    the shortcut is written and again immediately before it is put in place; 1.2.1 looked once, and overwrote a
    file that appeared or was replaced in between.
  - A shortcut of ours that is marked read-only is replaced, or removed, like any other. It used to fail every
    install, upgrade, repair and uninstall until the mark was cleared by hand. A read-only file that is not ours
    keeps its mark.
  - A temporary file a killed or failed earlier run left in the folder (`.qqq-` and 32 hexadecimal digits,
    `.lnk`) is removed. Only a file with exactly such a name is removed for this reason, and such a name is never
    recorded. One that cannot be removed is noted as a warning and tried again by the next run.
  - An install fails before it has changed anything when a file that is not ours has the name the shortcut
    needs, when the record in the registry cannot be opened for writing, or when a recorded shortcut, the
    current one included, cannot be opened. The last one now says that the shortcut could not be read; a
    same-brand upgrade used to say that a different shortcut already used the name.
  - An install can still fail after that. If the new shortcut cannot be written, the record is put back as it
    was found, except that the new name stays on record when a shortcut of ours already has that name or when
    the temporary file could not be removed. If the shortcut is written and the record then cannot be, the
    shortcut stays and its name is already on record, so a later install or the uninstall finds it.
- **Uninstall.** A recorded file that is not ours no longer fails the uninstall: it is left unchanged, a warning
  in the activity log says so, and the record is removed. The same holds for a recorded name that is not a
  shortcut file name in the folder. Both used to stop the uninstall. Shortcuts an earlier install could not
  remove, and temporary files left behind, are removed with the current shortcut. An uninstall still fails,
  before it has deleted anything, when a recorded shortcut cannot be opened, when the record cannot be changed,
  or when the shortcut folder can no longer be trusted. It fails with the record kept, so that it can be run
  again, when a shortcut of ours cannot be opened for the look taken just before its delete or cannot be deleted,
  or when a temporary file cannot be deleted; shortcuts it had already deleted by then stay deleted.
- **Registry.** `HKLM\SOFTWARE\QRunIO\PrintAgent` keeps `LastShortcutName` as before and may now also hold
  `PendingShortcutNames` (multi-string): the names of a shortcut that is being written and of shortcuts that are
  still to be removed. It is normally absent and is removed at uninstall. A value of either name that is of
  another kind is not used and is reported as a warning in the activity log each time it is read; an
  expandable string under `LastShortcutName` is read as stored, without expansion.
- A warning that cannot be written to the activity log never fails an install or an uninstall.
- **Sign-in startup.** When the task scheduler's program cannot be started at all, the companion's preference
  and the uninstall cleanup now say "Could not start Windows Task Scheduler administration." They used to show
  the raw Windows error. Nothing else about the sign-in task changes.

Removed the code the service never runs (2.0). The installed product behaves as before; the Core and Windows
libraries lose every path that existed only for the polling transport and the `--legacy-compat` mode.

- The API client can be built only from a signed-in session. Its device-token parameter and bearer header, the
  control poll, the HTTP command receipt and the HTTP fallbacks for inventory, heartbeat and job events are gone.
  Enrollment is unchanged: it posts to the session's `devices/enroll` route, as the service always did; only the
  `enroll` route a client without a session used is gone. Inventory, job events and command receipts travel only
  over the realtime channel.
- The print worker is the lane the service runs: it no longer has a mode that recovers the journal, sends its own
  heartbeat and inventory, or downloads and submits a document from memory. Everything the runtime hands it is
  required.
- A printer backend prints a verified staged file and nothing else. `IFilePrinterBackend` is folded into
  `IPrinterBackend`, the in-memory print is removed from the Windows adapter and the isolated backend, and the
  print helper no longer accepts a `print` operation that carries the document in the request.
- The update installer's message for a missing helper names the command line tool instead of a "diagnostic CLI".

Core hardening for 2.0. No change to pairing, history or the wire protocol.

- A job the server sends in a form the agent cannot use no longer blocks a device. The agent identifies the job
  and refuses it: history shows RECEIVED and then FAILED with a fixed reason ending "No bytes were submitted",
  nothing is downloaded or printed, and the next job is taken. This covers a missing or unusable member, a member
  given twice in any letter case, and options nested deeper than 64 levels. Only a claim that names no job at all
  still stops the device's queue until that job is cancelled on the server.
- Extra fields in a job, including any a submitter adds to its options, are ignored. A job title may be 1,024
  characters of any script, and a byte-order mark in front of a server response is accepted.
- A job is never printed twice because the server offered it again. One that is still being submitted is left to
  finish, and the log says so. A job stopped after its SUBMITTING receipt and before Windows was asked is FAILED
  with a reason that says no bytes were submitted; a job Windows has accepted stays SPOOLED even if releasing its
  staging space or printer turn fails afterwards.
- A realtime message that breaks the contract ends the connection with a reason naming what was wrong, and the
  agent reconnects.
- Core can now check a server before sign-in and report each problem by kind (unreachable, certificate refused
  with the certificate, not a QQQ server, printing extension missing or too old, no usable sign-in). The check
  sends no credential, follows no redirect and never signs the service out.
- Text that comes from a server, an identity provider, a browser callback or a certificate is shown and logged
  only in printable, bounded form, so a server cannot forge a log line or flood the log. A long value is cut and
  ends in "...", and a long list is counted.

Log redaction recognises two more shapes (2.0). This changes what a log line shows, in the service's log, the
companion's log and the shared activity log file alike, but only for a line that holds one of them; no line the
product writes today does (the companion completes the browser sign-in and hands the service a session
credential, addresses are logged without their query, and cookies are named, never shown), so this is a second
line of defense.

- The value of a `Cookie:` or `Set-Cookie:` header line is replaced by `[REDACTED]`, to the end of that line.
  The header's name stays.
- The values of `code` and `state` in the query or fragment of an address (straight after `?`, `&` or `#`) are
  replaced by `[REDACTED]`, as in `/oauth/callback?code=[REDACTED]&state=[REDACTED]`. The names stay.
- `code` and `state` are not keywords on their own. Lines such as "status code: 401", "exit code=1" and
  "state=Paused" are written as before.

Sign-out now ends the identity provider's browser session as well as the QQQ session, and every browser
sign-in sends `max_age=0` beside `prompt=login`, so the next sign-in always asks for credentials (the provider
must stay invisible to users). The companion's Sign out asks the service to log out with the application's
registered callback; when the server advertises `nativeSessionLogout` (UI API 1.10 or later) and lists that
callback, the server returns the provider's end-session data, Core checks it against the issuer QQQ advertises
and the provider's own discovery (HTTPS end-session endpoint on the issuer's origin, exact callback, bounded
hint), and the companion opens it in the browser and waits for the state-matched return. A server without the
capability gets the ordinary logout, and the log says the provider session was not ended. The command-line
`sign-out` cannot open the companion's callback inbox, so it still ends only the QQQ session and says so.
No change to pairing, history or the printing protocol.

Command line (2.0, breaking). `qqq-print-agent.exe` now only manages the installed service over its pipe.

- The `--legacy-compat` mode is removed with everything that existed for it: the separate per-user data directory
  (`--data-dir`), the command line's worker lock, the run-once claim pass (`run --once`), the isolated enroll and
  expose paths, and the `--service` flag. It was the last path the shipped programs could reach that sent a device
  token outside a QQQ session. A command line that uses one of the four options is refused with a sentence saying
  they were removed in 2.0. State the mode left in `%LOCALAPPDATA%\QqqPrintAgent` is no longer read; see
  [Administrator operations](docs/ADMINISTRATION.md#command-line).
- The executable is no longer a printer worker: `--printer-worker` is an unknown command. Only the removed mode
  ever started it that way; the service starts its own executable as the worker, in 1.x as now. Its two remaining
  internal roles (MSI update helper, browser sign-in callback) are unchanged.
- Help: `--help` and `-h` print the usage on standard output and exit 0 (they used to exit 1), as `help` and no
  arguments already did, and so does `--help` or `-h` after a command (`printers --help`). The usage text is new:
  one line per command, where the enrollment code comes from, and the exit codes. It is written with the
  platform's line ends.
- Refusals: an unknown command, and any command line a command does not accept, exits 1 with one line saying what
  is wrong followed by the usage, on standard error (an unknown command used to print the old usage on standard
  output). The reasons are new and fixed: for example `--server is required.`, `--printer needs a value.`,
  `--exposed may be given only once.`, `'run' takes no arguments.`. A refusal never repeats what was typed.
- Arguments are strict. A command accepts exactly its own options, each once, each with a value that is not blank
  and does not begin with `--`; extra words used to be ignored. `--exposed` takes `true` or `false` in any letter
  case and nothing else (padded values used to be accepted). Nothing is sent, and no enrollment code is asked
  for, when a command line is refused.
- Enrollment code: there is a prompt only when the code is typed at a Windows console. It is written to standard
  error, not standard output, the code is not shown while it is typed, and the prompt line is ended afterwards.
  When the code is piped in or comes from `QQQ_PRINT_ENROLLMENT_CODE` nothing is prompted: standard output holds
  only the service's reply and a successful enrollment writes nothing to standard error. A terminal that gives
  programs a pipe instead of a console (the default Git Bash window) is read as redirected input and may echo
  what is typed; an unattended caller whose console nobody types at must set the variable or redirect input.
  White space around the code and a leading byte-order mark are dropped, whether it comes from the variable or
  standard input.
- `service-status` and `snapshot` exit 1 when the service replies with `Error:`, as the other commands already
  did. The reply itself is still printed on standard output.

The companion's presentation logic moved out of the views into a new UI-free assembly,
`Qqq.PrintAgent.Presentation`, gated at 100% line coverage ([ADR-0003](docs/adr/0003-version-2.md)): one typed
client for the local service pipe, the texts and enablement rules of the printer, job, footer and settings
views, the Jobs page's paging and sorting state, and the connection dialog's flow (discovery, sign-in, sign-out,
enrollment, certificate exception) as a state machine. The pipe's wire format is unchanged except for one member:
the `control` object of the status snapshot (also printed by the command line's `snapshot`) no longer carries
`authorizationRequired`. Only the polling prerelease ever set it; it had been always false since, and the footer
sentence it selected, "Server authorization required — sign in again", could no longer appear. Missing authorization
is shown from the authentication status, as before.
What the operator sees is the same except for the following, each of them deliberate:

- **The companion keeps no session after sign-in.** Once the service confirms that it took over the session, the
  companion discards its own copy, so the service is the only process holding it. It also discards it when the
  hand-over was sent and could not be confirmed, because the service may hold it by then. The password form and the
  browser button disappear with it; signing in again starts with **Discover sign-in options**. They also disappear
  after **Sign out** and after a cancelled or timed-out browser sign-in, where they used to stay visible without
  doing anything.
- **"Not sent" and "sent, result unknown" are told apart.** The pipe client now reports whether a failed request
  ever left (`AgentPipeClient.SendReportingPhaseAsync`, `AgentPipeNotSentException`), and only that report counts
  as "not sent". An enrollment that never reached the service (nothing listening, pipe refused, service not
  verified) is shown as **Service unavailable**, because the code was not used; it used to say the code might have
  been consumed. A request that left and brought no usable answer back is never called a failure or a
  cancellation, and its message starts with what really happened (the request timed out, the connection closed
  before the answer arrived, the answer was too large, the answer could not be read):
  - enrollment says **Enrollment could not be confirmed** (a lost reply used to say the service was unavailable,
    and every cause used to be worded as a time limit);
  - a sign-in whose hand-over to the service left says **Sign-in could not be confirmed** and to check the
    connection status before trying again. This replaces **Browser sign-in canceled. No new sign-in was accepted.**
    after a browser sign-in whose hand-over timed out, and **Sign-in not completed** / **Browser sign-in not
    completed** after a hand-over whose reply was lost: the service may complete the sign-in in all of these.
    "Canceled" is now only said when nothing was handed to the service;
  - a sign-out says **Sign-out could not be confirmed** and that the session may have ended, instead of **Service
    unavailable**. This includes a sign-out the service answered with a document the companion cannot read;
  - a change to the certificate exception says **Certificate exception could not be confirmed**, instead of **Service
    unavailable** with the checkbox put back. The service may hold either value, so the checkbox is shown
    undetermined and its label says the last change was not confirmed; the companion applies no exception meanwhile,
    and the setting is read again as soon as the service answers a status request;
  - an action on a printer or a job (test print, retry, cancel, saving details) that never left this computer says
    **Request not sent. Nothing was sent to the local service, so nothing was changed**, with the reason. It used to
    say **Result could not be confirmed** and that the request might already have reached the printer, which stays
    the message for an action that did leave.
- **Pipe failures the window did not handle are handled.** A failure while verifying the service, an oversize pipe
  message, and a malformed callback written to the browser sign-in inbox now show the same "service unavailable"
  or "not completed" message as the other failures instead of escaping the handler. The identity check itself now
  always answers with one of two sentences, in the command-line tool and the in-app update as well as the
  companion (a check that could not be carried out used to show the underlying Windows or .NET message): **The
  local endpoint is not the installed Windows print service...** for a process that is running and is not the
  service, and **The Windows print service stopped or restarted before it could be verified... Wait a moment and
  try again** when the process that served the pipe has ended, which is what a service restart between the
  connection and the check looks like. Exit codes and the update's behavior are unchanged.
- **A click that cannot run says so.** While one operation of the connection dialog is running, another one is
  refused with **Another operation is still running** and a log line; it used to do nothing. A refused enrollment
  or password sign-in keeps the code or password that was typed, because it was not used. This is reachable
  because saving the certificate checkbox does not block the dialog's other buttons.
- **Closing the window during an operation is quiet.** The operation ends by itself; it used to be able to fail
  with "Cannot access a disposed object", shown and logged as a discovery or sign-in failure, and could still send
  its request to the service after the window had closed.
- **An update the service is not ready for says why.** When jobs claimed before the update's pause are still
  running after the preparation limit, the in-app update shows the service's own sentence ("Not ready for update:
  1 job(s) were still in progress after 25 seconds. They will finish; try the update again afterwards.") after "The
  update was not started, and the current version remains installed." A preparation that was interrupted (printing
  was resumed, or a sign-in began, while it waited) is shown the same way, in the service's words. Any other answer
  is shown as an answer the update could not use. All of these used to follow "Let active printing finish and try
  again", whatever the service said.
- **Full details** no longer opens a dialog for a details reply that names another job or has no job or events;
  the message was already shown, and the dialog used to open anyway.
- A service error shown as **Action could not be completed** no longer starts with a space. A sign-out reply
  that is a document without a message, or with an incomplete identity-provider part, is treated as unreadable
  and never shown as text (it is the reply that carries the identity provider's token hint). A timestamp without
  an offset is read as UTC, not as this computer's local time; the service writes offsets, so this is a safeguard.
  A log-settings reply of JSON `null` shows **Log settings unavailable** instead of doing nothing.

Activity log changes: the first failed request to the local service per operation is a warning that names the
cause and whether the request was sent, the same failure repeating is a debug line, each failure keeps its full
exception as debug detail, and the first answer afterwards is noted once, so the status polls do not fill the log.
An answer that arrived and could not be read is logged as that, not as a request that did not complete. Discovery
logs the entered address as scheme, host, port and path only, and does not log an address it cannot read. A
hand-over, a sign-out or an enrollment that left without a usable answer is logged as unconfirmed with the same
sentence the operator sees; a browser sign-in is logged as cancelled only when nothing was handed over, and the log
no longer claims that no callback arrived when one did. A refused operation is a warning that names it. An
operation ended by closing the window or by its caller is one debug line. "Print device enrollment completed." now
carries the `[Companion]` prefix.

## 1.2.1

Release-pipeline fix only; same source as 1.2.0 otherwise. The stable release workflow's per-edition
sign job passed the edition to `Build-Installer.ps1` through an array splat, which PowerShell binds
positionally, so `-Edition gg-forge` landed in the signing-certificate parameters and the GG Forge
sign job refused to publish; tag `v1.2.0` therefore produced no signed installer and must not be
moved or reused. The workflow now splats a hashtable. No application or wire change.

## 1.2.0

Client editions. A folder under `editions/<id>` (descriptor, SVG mark, ICO icon, optional logo) and
`Build-Installer.ps1 -Edition <id>` produce a fully branded build and installer: the client's mark and
icon on the splash, About, window, taskbar and Start menu; the client's product, company and file
description in every executable; the client's product name, manufacturer and service display name in
the MSI, forced over any stored labels; the MSI and release asset named after the edition's asset
prefix; and the installed updater following that asset on the shared feed. CI builds every edition,
and the stable release workflow signs and publishes one installer per edition beside the generic one
with a single checksum list. The first edition is `gg-forge` (GG Forge Print Agent by Greater Goods).
Deployment branding through `Install-Branded.ps1` is unchanged and still applies on top of an edition.
Native browser sign-in sends the attempt's nonce as `expectedNonce` when the grant policy is REQUIRED or
OPTIONAL or when discovery advertises `nativeSessionNonceValidation`; legacy servers keep the unchanged
exchange body. `build/Build-CiRelease.ps1` is the shared-CI packaging entrypoint and delegates to
`Package-Release.ps1`.

Printer approval per server. On a printing 2.4 server the client advertises `printer-approval-v1` and
takes approval from the server's `PRINTERS` frame, pushed after each accepted inventory and whenever an
administrator approves or disables one of this device's printers (protocol 1.1.0, QBit 1.1.0). On a 2.3
server the service instead reads the operator printer catalog after each accepted inventory and about once
a minute while connected. Either way the snapshot carries a per-printer server state:
approved, awaiting approval, not reported (with the reason) or not checked. The Printers page shows it in a
new Server column, in the summary line and in the expanded details, named for the connected server, so a
printer shared before a server change is never mistaken for one the new server approved. The reading is
reset with the connection. The activity log viewer has a Clear logs button, the connection dialog keeps its
result banner above the form and shows a waiting state during browser sign-in, and the service's sign-in reply
no longer repeats itself. No wire change.

## 1.1.1

Browser sign-in fix. The companion runs elevated, and its callback inbox pipe was keyed with
PipeOptions.CurrentUserOnly to the elevated token's default owner (the Administrators group), so
the protocol handler the browser launches as the ordinary user was refused with "Access to the
path is denied" and no browser sign-in could ever complete. The inbox is now created with an
explicit descriptor owned by and restricted to the user's own SID, and the handler verifies the
companion by process image instead of the owner comparison. A protocol-handler trace in the
per-user profile, copied into the activity log when a browser sign-in does not complete, makes
the hand-off visible. No wire change.

## 1.1.0

Diagnostics for new accounts and servers. The session client now narrates every step of discovery,
authentication metadata, native grant validation, provider discovery, browser callback handling,
password sign-in, session restore and renewal, identity checks, device binding and the realtime
socket upgrade, and says exactly which server-side requirement is missing when it refuses a server;
the service logs each sign-in, sign-out, enrollment, authorization and reconnect step and every
failure with its inner causes; the companion logs its side and shows the failure reason in the
dialog instead of a generic message. TLS certificate rejections are logged with the policy error,
certificate identity and remedy. A new **Allow untrusted server certificate for this server** option (Connection
settings, bound to one server origin, persisted in the protected store, honored by the service and
companion, logged with a warning on every session) supports development servers with self-signed or
private-authority certificates without weakening any other server connection.
Log settings gain **Debug logging** (request routes, status codes, timings, realtime frames and
full failure detail; never credentials) and replace the daily cap that silenced the log with size
rollover: a file that reaches the limit (default 100 MiB, up to 1024) becomes a numbered segment and
logging continues, keeping at most a configurable number of segments per day (default 10). No wire
change; native printing stays 2.3 with wire payload v1.

## 1.0.1

Packaging fix; no application, wire or dependency change. The `v1.0.0` release run passed
every build, test, security, CodeQL and script gate and the Azure OIDC login, then stopped
before signing: the Artifact Signing action installs its SignCLI tool with
`dotnet tool install --add-source`, which the repository's NuGet package source mapping
rejects. No signed 1.0.0 installer was published and that tag is unchanged. The release
workflow now installs only the pinned SignCLI (`0.9.1-beta.26227.3`) through the mapped
repository `nuget.config` into the exact folder the action expects, before the first signing
step, so the action finds it and the mapping is not weakened. Native printing stays 2.3 with
wire payload v1.

## 1.0.0

Product version 1.0.0 for the same cohort as 0.4.0-rc.4. A stable `v1.0.0` tag runs the
release workflow: after every existing gate it signs the service, companion and CLI binaries
and then the MSI with Azure Artifact Signing, verifies the exact publisher, and publishes a
non-prerelease with checksums and provenance generated after signing (see
docs/RELEASE-SIGNING.md). The numeric file version is `1.0.0.65535`, above every earlier
candidate, so the updater offers it over an installed 0.4.0-rc.4. Native printing stays 2.3
with wire payload v1; 1.0 is not a wire change. No code or dependency change comes with the
version. Physical acceptance covers 4 × 6, Letter and 4 × 1; 4 × 2 is deferred.

## 0.4.0-rc.4 — unsigned prerelease candidate

Release identity only: rc.4 is strictly newer than the local, unpublished 0.4.0-rc.3
build and ships the same cohort, so the updater accepts it over an installed rc.3. The
MSI ProductVersion and `VersionPrefix` stay 0.4.0; `VersionSuffix` and the packaging
defaults move to `rc.4`, and the ordered numeric file version becomes `0.4.0.4`. There is
no code, test or dependency change. Everything described under rc.3 below (media
selection, 4 × 2 and 4 × 1 test labels, phase-timing log, ordered file version) applies.
The rc.4 head is version metadata and docs on top of pre-release product source
`5c2bfc9`, whose full hosted CI run 41381287355 passed 499 tests (166 Core + 333 CLI) with
zero warnings and errors. rc.4 itself is not yet tested or published; its tag pipeline
must pass all gates afresh before publication. Earlier operator acceptance covers 4 × 6
and Letter output, and the 4 × 1 layout was confirmed on the local rc.3 build of the same
product code; 4 × 2 is deferred. The MSI is unsigned and no GA or 1.0 readiness is
claimed.

## 0.4.0-rc.3 — local candidate (superseded by rc.4)

Release identity: this is a strictly newer semantic version than 0.4.0-rc.1 and
0.4.0-rc.2 with the same numeric core, so the MSI ProductVersion (0.4.0) and
`VersionPrefix` are unchanged, `VersionSuffix` and the packaging defaults move to `rc.3`,
and the updater (which needs a strictly newer version) and the installer's same-version
upgrade support both accept it. The rc.2 MSI was installed, but Windows Installer skipped
its executables because rc.1 and rc.2 shared the numeric file version 0.4.0.0 ("existing
file is of an equal version"). Packaging now stamps each executable with an ordered
numeric file version: `X.Y.Z-rc.N` becomes `X.Y.Z.N` (N 1..65534) and stable `X.Y.Z`
becomes `X.Y.Z.65535`, and any other prerelease label is refused. Future upgrades replace
files automatically. No behavior, dependency or lock change otherwise. The protocol
package `0.4.0rc1` is an independent wire-compatibility version and is unchanged. Older
notes below that name 0.4.0-rc.1 or rc.2 describe the same cohort.

This cohort goes beyond the older installed CI 0.4.0-rc.1 build. The rc.3 candidate
was built and installed locally with a normal MSI upgrade (exit 0), matching all three
components; no repair was needed. It is not publicly released or tagged. Hosted Windows
CI and the update-feed handoff await the migration owner.
Upgrade the server first: selected-media jobs require
printing extension 2.3.0 (wire payload v1 unchanged). Against a 2.0, 2.1 or 2.2
(rendering) server the client keeps its three-capability HELLO and offers no media
profiles, so existing behavior is unchanged.

- Add 4 × 1 and 4 × 2 in test labels (ZPL at 203/300/600 dpi, one PDF choice each) to the existing Print test choices; the 4 × 6 choices and defaults are unchanged. These PDFs carry explicit actual-size job paper (101.6 × 25.4 / 50.8 mm) applied to a job-local copy of the queue defaults (so its Direct Thermal and other settings are kept) and checked against the device context before StartDoc. Needs the matching unreleased 0.4 server; older clients reject job paper. Load the correct stock first; nothing prints until the operator confirms.
- Report each queue's machine-default paper dimensions and resolution as an opaque, revision-bound media profile, and advertise `printer-media-v1` only when the discovered server supports it. Defaults are configured facts, not physical stock sensing.
- Refuse a selected-media job before spooling when the profile is unknown or its revision has drifted, and again at the native submission boundary. A proven pre-submit refusal is recorded as FAILED and is never treated as an uncertain outcome.
- Normalize job-only driver copies and scale for selected PDFs and verify the effective device-context dimensions and resolution before StartDoc. ZPL (RAW) only rechecks the profile immediately before StartDocPrinter.
- Log one phase-timing line per processed job (inventory, claim and receipt, download, revalidation, submit gate, native submit) with the job identifier only, to attribute print latency from the service log. No new request, payload or printer access.
- Pin the printing protocol to the final reviewed contract, commit `42c0d249f81395f00408ec0f92638dcbbf5deb03` ([Gitea PR 2](https://git.kof22.com/QRun-IO/qqq-print-protocol/pulls/2)), in `.github/workflows/ci.yml`; native printing stays 2.3 with wire payload v1. The earlier pin `a26c18c9…` is historical.
- Split `PrintWorker` job processing into named submission, revalidation, dispatch-gate and native-submit steps (analyzer MA0051 rejected the first form of the timing instrumentation). Behavior, state order and lease release are unchanged.
- Document every type, method, constructor and non-auto property with how/why headers and apply the `.editorconfig` structural spacing. Each batch was token-compared as comment and blank-line only.

Evidence: this cohort includes changes beyond the installed CI 0.4 build, and the
latest release head is neither installed nor tagged. On the merged integration tree the
local gate passed: the production UI build under strict XML and analyzers with zero
warnings, and the 35 existing paper/media tests; the server's strict full verify (R31)
also passed. Core.Tests is not built locally
because of a Defender quarantine and is verified only on hosted CI. The prior protected
base `0336d5f` had 477 hosted tests; this source head still needs a normal Gitea
Core, CLI and build run. The earlier PR-head result (35ed93c) is historical. The
existing Auth/S3 qualification is accepted and no new desktop or provider proof is
claimed. Still pending: hosted CI, installation of the new label implementation, and
a small-stock paper test. No 1.0 claim.

## 0.3.2-rc.1 — 2026-11-01

- Verify that the app, service and CLI all have the requested release and identical full build versions before recording MSI update success. Report missing, unreadable or mixed builds without claiming rollback.
- Correct the Windows App SDK credit to reference its Microsoft terms and installed third-party notices.
- Give the isolated native test host the same PerMonitorV2 DPI declaration as production while retaining its unelevated execution level.

The updater regressions first failed against the earlier implementation, then
passed with the correction. The integrated client gate passed 426 tests with
zero warnings, errors or skips. Actual in-app update handoff and the historical
layout-cycle cause remain separate acceptance items.

## 0.3.1-rc.1 — 2026-11-01

- Show explicit WebSocket connection state and server authority in the footer, with live upload/download rates and service-lifetime application-payload totals. A lightweight local observation refresh adds no QQQ job/control polling.
- Recognize Windows-normalized startup account names only when they resolve to the exact expected SID; preserve the remaining task ownership checks. Rename the tray startup section to distinguish it from the automatic print service.
- Leave the exact fatal native `LayoutCycleException` unhandled after AppKit logs it, allowing normal WinUI termination instead of retaining a corrupted companion window. The service continues; the operator can reopen the companion. This is failure-handling mitigation, not a fix for the original layout trigger.
- Add an isolated native WinUI regression host using actual dialog/action/refresh code and synthetic IPC, with no printing or installed-state changes.

The installed 0.3.0 companion's post-print LayoutCycleException remains under
investigation. Nonprinting geometry and modal-refresh scenarios have not
reproduced it; these changes do not yet establish a layout-cycle fix.

## 0.3.0-rc.1 — 2026-10-31

Upgrade the server before this client: native printing API 2.0 requires a
synchronized authenticated WebSocket, with no job/control polling fallback.
Installed and physical-output acceptance remain separate from source/test gates.

- Require the server's printing 2.x extension and authenticated persistent realtime channel. Replace periodic job/control HTTP polling with pushed state, commands, settings and job notifications.
- Stream verified documents into protected staging files, with configurable aggregate bandwidth, storage, download concurrency, per-printer ordered submission and native submission deadlines.
- Preserve durable receipts and command outcomes across reconnects; keep control traffic responsive during document transfers.
- Expand job details and actions directly beneath the selected row, with consistent sortable table headers and bounded history paging.

## 0.2.0-rc.1 — 2026-10-31

Release preparation only; publication and installed qualification remain pending.

Upgrade the server first: the new client requires the semantic printing extension
and standard QQQ sign-in; older bearer-only servers need an upgrade. Existing
pairings and history are preserved, but unbound devices need explicit account association.

- Discover QQQ's semantic printing/authentication capabilities and sign in through
  standard table-based or explicitly advertised native browser authentication.
  Protect the service session with DPAPI; resume through QQQ and preserve pairing
  and journals when authorization is lost.
- Add independent device-control polling, durable desired state/command receipts
  and exact owned spool cancellation; disconnect keeps the control path available.
- Route normal CLI operations through the authenticated service; direct legacy
  diagnostics require an explicit compatibility flag.
- Add installer-owned browser callback forwarding, cancellable sign-in and
  customer-branded callback text. Installed callback/provider acceptance remains
  unqualified; no signing or stable 1.0 readiness is claimed.
- Support explicit replacement pairing after revocation and safe recovery from
  damaged saved credentials. Pending setup survives restart without presenting
  old device proof or enabling printing; previous history and choices remain intact.
- Pass 365 tests and build WinUI/service/CLI with zero warnings/errors/skips.

## 0.1.9-rc.1 — 2026-10-31

- Keep management responses available until the client closes its one-command connection or the existing deadline expires. This prevents Windows from discarding unread success and error responses during server disconnect.
- Preserve current and older newline-framed clients without a new acknowledgement protocol or a blocking pipe drain. Cancellation and service shutdown remain bounded.
- Reproduce the failure through the real runtime with delayed readers; verify successful responses, error responses, next-client reuse, deadlines and shutdown. All eight reproduction cases and 255 combined regression tests pass; the solution/UI build has zero warnings/errors.
- Complete the state-preserving CI-package upgrade and all 16 installed UI checks, including authenticated management and history queries. No physical print was requested by these checks.
- Publish the corrective prerelease through [release workflow 41190815995](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41190815995); verify all four release assets and provenance attestations. The unsigned published MSI and the installed CI MSI are distinct builds; [acceptance notes](docs/ACCEPTANCE.md) record their hashes and scope.

## 0.1.8-rc.1 — audit candidate, superseded before publication

- Bound and cancel local management frames and server responses; apply a deadline to streamed document bodies.
- Drain successful queued jobs without an extra idle delay while retaining independent heartbeats and failure backoff.
- Preserve valid long job/printer names, close failed SQLite initialization handles and guard late UI completions after exit.
- Carry explicit operator duplicate-risk consent for failed jobs that may already have reached a printer.
- Require release security/CodeQL/script gates and verified artifacts before publication; add build provenance attestations.
- Package from fresh output directories with locked restores and matching source, MSI and component versions.
- Authenticate the installed service before sending local commands, retain pipe ownership, and reject unsafe machine-state owners, access grants or links before privileged use.
- Protect sign-in task definitions through registration and validate machine state before an upgrade removes the prior product.
- Fresh regression gate: 249 tests, zero build warnings/errors. Installed audit acceptance and hosted provenance verification were pending at this superseded candidate checkpoint; the completed 0.1.9 checks are recorded above.
- The local candidate installed successfully with MSI exit 0 and preserved credentials/journal, but a subsequent management-history check exposed the response-disconnect race corrected in 0.1.9. Service heartbeat and printer readiness were confirmed independently through QQQ.

## 0.1.7-rc.1 — 2026-10-30

- Built-in QQQ-branded warehouse diagnostics use the server's bundled legible font across PDF and ZPL.
- Native test controls offer Letter, A4 and 4 × 6 labels with explicit 203/300/600-dpi ZPL choices.
- Prefer ZPL for label-capable queues and suggest resolution from an explicit driver hint; operators verify stock and resolution before printing.
- Existing API callers keep compatible defaults when testProfile is omitted.

## 0.1.6-rc.1 — 2026-10-30

- Expand selected printer information within its row, with actions first, labeled fields, a bounded description preview and expandable Windows diagnostics.
- Open printer settings and job details by double-click, while preserving checkbox/button actions and text selection. Equivalent Settings and Details buttons remain available.
- Use wider, window-responsive native dialogs for profile editing and structured job summary/event history.
- Add sortable headers with printer Name ascending and job Created descending defaults. Creation time is preserved independently of status updates; missing historical creation times remain explicit.
- Apply job ordering across the matching local history and bounded cloud queue preview before paging, using displayed labels and stable identifier tie-breaks. Sort indicators update only after a successful query.
- Installed **0.1.6, source d9033ae**, completed with exit 0 and no reboot, retaining credentials, four printers and two jobs. The source gate passed **211 tests (42 Core + 169 Windows/CLI)** with zero warnings/errors; [exact-source CI 41131558158](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41131558158) succeeded. Management, profile, logs, pagination and branding checks passed. All **15 installed UI checks have passing results across fourteen plus one runs**: the first run passed 14/15, then a test-only arbitrary-text double-click selector was corrected and the focused remaining check passed.
- The service was later observed Stopped with exit 0 and no matching crash evidence; the cause remains unknown. A normal UAC-authorized restart succeeded and the final check found it Running, with cloud connectivity offline. No new physical print or separate visual inspection was performed. These results do not establish every Narrator, keyboard, resize, long-text, scaling or high-contrast scenario.

## 0.1.5-rc.1 — 2026-10-30

Installed acceptance subsequently passed 12 interactive checks across a ten-test run and a focused two-test run. This evidence applies to 0.1.5, not the 0.1.6 changes above.

- Compact printer/job tables, stable row selection, full-text details and bounded 50-row job pages with prefix search.
- Client-managed computer/printer names, descriptions and locations synchronized to QQQ without changing physical queue identities.
- Indexed history queries, 200-event recovery batches and bounded detail maintenance; synthetic 10,000/30,000-job regression coverage.
- Shared AppKit daily diagnostic logs with configurable retention/size, visible cap state and explicit safe clearing.
- Same-MSI customer branding for the companion, installed product/service labels and Start menu shortcut; optional customer PNG/ICO artwork.
- User confirmed the 0.1.4 tray icon works. No physical print was sent for this scope.

## 0.1.4-rc.1 — 2026-10-30

- Restore the native File and Help menu bar, including Settings, Close, Exit, activity logs, help links, Check for Updates and About. Update commands share the existing AppKit coordinator and busy state.
- Keep ownership of cached tray images when sending their handles to Windows. H.NotifyIcon disposes objects assigned to its Icon property; reusing those cached objects broke returning status transitions. Failed icon updates now retry on the next refresh.
- Native WinUI diagnostic reproduced the disposed-icon failure and passed after the ownership fix, including Windows registration. Actual installed tray visibility remains a separate acceptance check.

## 0.1.3-rc.1 — 2026-10-30

- Complete shared AppKit settings: manual and optional startup update checks, splash duration, version/channel, help and release links, and dependency credits.
- Use AppKit's update coordinator with a protected machine MSI adapter so the service, desktop app and CLI upgrade together after confirmation.
- Verify release eligibility, download size and SHA-256; finish active submission before pausing claims, preserve enrollment/history, and record installer outcomes for diagnostics.
- Keep update prompts within the window lifetime and wait for existing modal dialogs.
- Retain the 0.1.2 startup correction. Locked build and 163 Core/Windows tests pass; installed UI and real updater acceptance are recorded separately in docs/APPKIT-PARITY.md.

## 0.1.2-rc.1 — 2026-10-30

- Fix installed companion startup: assign the built-in navigation Settings identifier after its template loads, instead of passing an uncreated item to WinUI.
- Preserve the original launched process in desktop tests so native startup failures report the actual exit code.
- The 0.1.1 MSI upgrade retained credentials and restarted the service, but installed UI testing found this startup crash; 0.1.1 was not published as a replacement release.
- Always rebuild the WiX package so changes to the version or payload directory cannot reuse stale incremental output.

## 0.1.1-rc.1 — 2026-10-29

- Native Windows navigation, stable printer rows, searchable job identity/history, confirmed test/reprint/cancel actions and persistent failure review.
- One shared AppKit activity log, synchronous tray icons, single-instance restoration, reconnect and explicit credential recovery.
- Service start/restart controls, opt-in installed sign-in startup, bounded SCM recovery and narrowly scoped full-uninstall task cleanup.
- Isolated native printer workers with timeout/orphan cleanup; interactive save-to-file queues excluded from unattended printing.
- Connection-scoped outbox, interrupted job recovery, safe event-detail retention and per-user CLI/service identity separation.
- Automated verification: 102 Windows tests, zero compiler/analyzer warnings. Installed acceptance is recorded separately.

- Fix desktop startup by including the generated WinUI resource index and compiled window markup in the published app and MSI.
- Fail installer packaging when required app resources are missing or empty.
- Supersedes 0.1.0-rc.1, whose desktop companion failed before showing its window. The initial build and unit tests did not detect this packaging defect.

## 0.1.0-rc.1 — 2026-10-29

Initial public prerelease for QQQ 4.1+.

### Added

- Windows AppKit/WinUI companion and background Windows service installed together by an x64 MSI.
- Single-use enrollment, protected service credentials and outbound HTTPS polling using printing protocol v1.
- Windows queue discovery with persistent local printer exposure choices.
- System-tray health and a details surface for connection, printer status, jobs and recent service logs.
- Runtime customer branding, persistent connection footer and native connection/service-log dialogs.
- PDF rendering through the Windows PDF/GDI path; RAW ZPL submission for Zebra-compatible queues.
- Local SQLite receipt/event journal, payload integrity checks and duplicate/uncertain-submission handling.
- CLI enrollment, printer selection, status/snapshot and foreground diagnostic worker.
- Shared Java/.NET contract fixtures, unit/Windows persistence tests, Windows CI and unsigned prerelease MSI automation.

### Validation and limits

Physical laser PDF output was confirmed locally, including the installed service path. Zebra hardware qualification remains pending. This release is unsigned and uses polling; it does not claim physical completion from a spool acknowledgment. Custom PDF media, automatic cancellation/reprint flows, retention, renderer isolation and production signing remain follow-up work. See docs/ACCEPTANCE.md.
