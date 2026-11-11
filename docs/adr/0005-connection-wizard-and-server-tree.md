# ADR-0005: The connection wizard and the server tree

Status: accepted for 2.0 under the owner's direction of 2026-11-10; the wizard and the tree are not built, and the window does not use any part of this record yet; revised the same day after an independent review; steps 1 and 2 of the wizard were changed on 2026-11-10 after a retrospective review (ADR-0004, "Resolved after the retrospective reviews"); the gaps found while the build was planned are settled in "Resolved while planning the build, 2026-11-10/11" below, which also says what is merged and lists the sentences corrected in place on 2026-11-11. Builds on [ADR-0003](0003-version-2.md) (a wizard replaces the connection dialog; servers are shown as a tree) and [ADR-0004](0004-multi-server-runtime.md) (server identity, the pipe contract).

## Context

The 1.2.x connection dialog is one scrolling form: address, a development certificate exception, a three-way pairing choice the operator must make before signing in, a discover button, two sign-in methods and an enrollment code. Every message the window shows is mirrored into the dialog's own feedback bar, which is never closed, so the dialog opens showing whatever was said last. The left pane lists Printers, Jobs and Logs for the one connection.

## Decisions

### The wizard is a page, not a dialog

Add server opens the wizard in the content area. A dialog cannot be resized by the operator, cannot host another dialog (the certificate warning, the removal confirmation), and was the source of the clipped layout the current form works around. When the computer has no servers and the service reports that its stored data is ready, the wizard is what the window shows; with no servers in any other state the window shows the Computer page, which says why (G5 below).

It has five steps, a step indicator, and Back, Next and Cancel. Enter moves forward and Escape cancels. Each step shows only what that step needs, and its own result; nothing from another step or another page appears in it.

1. **Server.** The address, and a name that defaults to the host (what a name may be is in ADR-0004, "Server names"; a host that breaks that rule is not proposed as it stands). Checking the server asks the service to run the compatibility check (`servers.check`), because the service is what will connect and it trusts certificates as LocalSystem, not as the signed-in user. The step shows the five stages in order (reaching the address, a trusted secure connection, a QQQ server, a printing extension it can print through, a sign-in method it implements), each passed, failed with the reason, or not reached. Next is enabled only when all five pass. A certificate failure shows the certificate that was presented (subject, issuer, expiry, fingerprint, and why Windows refused it) and says how to go on. When Windows refused the certificate because it does not trust the issuing authority, installing that authority's root certificate in this computer's trusted root store is the lasting fix and needs no exception, and switching on the development-only exception for this one server, with its warning, is the second way (G12 below). For any other refusal the step shows the certificate and offers neither. The warning comes in two stages, because the sign-in provider is known only from the server's sign-in description, which cannot be read while the server's own certificate is refused: the first warning names the server's address and says that the sign-in provider's address will be shown after the check; the re-check that follows returns the provider's address, and the step then shows both addresses the exception covers before the operator goes on. (An earlier wording had one warning name both addresses before the exception was accepted, which cannot be done in that case.) Nothing is stored yet: the choice travels with the wizard and is saved when the server is. An address that is already a server on this computer says so and offers to open it.
2. **Sign in.** The methods the server offers and no others. Browser sign-in shows that it is waiting, can be cancelled, and gives up after three minutes; password sign-in clears the password after every attempt. The sign-in runs in the companion, as the user. If the user's Windows account refuses a certificate that the service accepted, the step says that it is this account that refused it. When sign-in succeeds the wizard adds the server (`servers.add`), which stores the server, the certificate exception if one was accepted and the session together or not at all. The request carries the provider's address that the check reported, for comparison only: when the service's own read names another provider, names one where the check named none (the request then carries no address, which says so), or names none where the check named one, the add is refused with `provider_changed` and nothing is stored, and the wizard goes back to step 1 and checks the server again, so the operator sees the provider the server names now. When it is the sign-in provider's certificate that is refused, this step shows it and offers the same two ways forward as step 1.
3. **Pair this computer.** For a new server this is the enrollment code and where to create it. A refused code says so and can be tried again. When the outcome is not known (the reply was lost, or the service stopped answering), the step says exactly that, shows what the service reports was left behind, and offers to check again rather than to enroll again. The check reads a snapshot that was sent after it was asked for, never one already in flight ("Resolved while reviewing the management client, 2026-11-11").
4. **Printers.** The Windows printers that can print unattended, none selected. Sharing can be skipped and done later.
5. **Done.** The server's name, who is signed in, how many printers are shared and that the server must approve them, and a button that opens the server.

Cancelling before sign-in leaves nothing behind. Cancelling after it asks whether to keep the server (signed in, not paired) or remove it.

### The ordinary pairing is tried first; a choice is asked for only on refusal

The old form made the operator pick one of three pairing options before signing in. The wizard tries the ordinary thing and asks only when that fails. What the service can tell apart, and what the wizard does:

| Outcome | What the wizard does |
| --- | --- |
| No pairing on this computer for this server | Asks for an enrollment code. |
| The saved pairing binds | Nothing to decide; this is the common case. |
| The pairing binds, but for a different account than the one approved for it | Says which account is approved and asks the operator to sign in as that account. |
| The server refuses the bind | Says that the server refused this pairing for this account, and offers the two repairs with what each does. |

The two repairs are: associate the existing pairing with this account (for a pairing created before accounts were linked), or set up a new pairing (for a revoked or replaced device), which needs a new sign-in and a new enrollment code and keeps history and printer choices. The new sign-in falls away, for either repair, once the condition of G11 below is met; it is not met yet.

The wizard cannot say why a bind was refused. The server answers 401, 403 or a failure with no reason, and 2.0 does not change the wire ([ADR-0003](0003-version-2.md)). So the choice between the repairs is deferred to the one case that needs it, not removed. A reason code on a refused bind is a request to the printing protocol, outside this release.

Repairing a server starts from that server's Connection page and enters the wizard at the sign-in step. The compatibility check runs again first, without showing step 1 unless it fails, because the sign-in methods on offer come from it.

### The tree

- One node per server, in the operator's order, showing its name and health as both a glyph and text, and a marker for as long as its certificate exception is switched on. Beneath it: Printers, Jobs and Connection. Selecting the server shows its overview.
- Add server, disabled with an explanation at eight.
- Computer (this computer's details, the Windows service, companion startup), Logs and Settings (appearance, updates, about).

Printers and Jobs are the pages 1.2.x has, scoped to the selected server. Connection gathers, per server, what 1.2.x spreads over the Settings page, the footer and the connection dialog: status, who is signed in, the realtime meter, sign in again, sign out, reconnect, the certificate exception, rename and remove.

A server's context menu offers reconnect, sign out, rename, move up, move down and remove (the two moves are G16 below). Removing asks first and says what it does: printing from that server stops, its pairing and session are deleted from this computer, its job history is no longer shown and is deleted after the retention period, and its device record on the server is not touched.

After an upgrade from 1.x the existing connection is the first server, named after its host (or, where the host breaks the name rule of ADR-0004, "Server names", after a name made from it that passes), and the wizard is not shown.

### One status at a time, per place

- The footer and the tray icon show the worst health across servers; the footer names the count in each state and opens the first server that needs attention; the tray tooltip gives the worst health, the count in each state and as many servers with their state as fit in what Windows shows of a tooltip, then how many are left out; the tray menu lists every server (G15 below).
- Each server has its own busy state. A sign-in in progress on one server does not disable or silently drop commands on another, and refresh never stops.
- Browser sign-in is the one thing that cannot be per server, because Windows gives each user one callback inbox and one redirect address. One dispatcher owns the inbox and routes each callback by its OAuth state to the sign-in that started it, so two servers can be signing in at once and a late callback for one cannot fail the other.
- A message belongs to the page that caused it and is cleared when the operator leaves that page. The wizard never shows a message from elsewhere.

### Accessibility

Every interactive control has an AutomationId that stays stable across releases. Only one server's pages are on screen at a time, so identifiers inside them are the same for every server and carry nothing machine-generated. A server's tree node is `Server.<n>`, its position in the tree counted from one, with the server's name as its accessible name; its children are found through it. Tree items, rows and steps have accessible names. Progress and results are announced once, after the text is final. Everything is reachable and operable from the keyboard, and state is never conveyed by color alone. What each of these requirements means as a test is defined under "What the re-audit scores accessibility against" below (G18).

Existing identifiers the UI tests use (`NavigatePrinters`, `NavigateJobs`, `NavigateLogs`, `ServerAddress`, `EnrollmentCode`, `EnrollDevice` and the rest) keep their meaning where the control still exists; where a control is replaced, the test moves with it in the same change.

### Where the logic lives

Every decision above (which step is next, whether Next is enabled, what a failure means, which repairs to offer, what each node shows) is in the UI-free presentation assembly and covered by unit tests. The views bind to it. The interactive UI tests cover the shell: that the tree and wizard render, navigate and announce.

## Consequences

The pairing choice no longer exists as a control up front, so documentation and support text that name its three options change. The wizard needs the typed compatibility result from Core, the service-side check and typed replies from the pipe; it cannot be built on message matching. Sign-in messages that name controls of the old dialog are rewritten.

## Resolved while planning the build, 2026-11-10/11

A planning pass on 2026-11-10 read this record against the code of `release/2.0` (at `65a99d3`; nothing was built or run for it) and listed twenty-one places where the record was silent, disagreed with the code or left a choice open. They keep the plan's numbers, G1 to G21. Each was settled on 2026-11-10 as the plan recommended, under the owner's standing instruction for this work (when a decision comes up, take the recommended option, record it in the decision record, and keep going), with one exception: for G1 and G2 the rule that was taken that day, and then built, is tighter than the plan's wording, and what is recorded here is the rule as built. They were written into this record on 2026-11-11. None of this is the owner's own words, and the owner can reverse any of it. The plan listed seven of them (G1, G2, G4, G5, G7, G11 and G19) as needing a decision before a step could start, which its summary puts as needing the owner or the service steps; it asked that four (G1, G2, G4 and G11) be raised with the service steps, and it called one part of G19, adding the layout tests to the solution, the owner's call. All seven were taken under the standing instruction like the rest, that part of G19 included; it is recorded with G19 that the owner may reverse it before the step that builds it. Two of them keep a condition that is stated with them (G7 and G11).

"Built" below means merged on `release/2.0` and read there at `02f34c3` on 2026-11-11. "Not built yet" means that no merged code does it. Neither the wizard nor the tree exists, the window calls none of the merged parts, and nothing serves version 2 of the pipe, so no decision here has reached an operator.

Sentences of this record that a decision contradicted, or that said less than the rule as built, were corrected in place on 2026-11-11, each with a pointer to its decision, except the third case in step 2:

- Under "The wizard is a page, not a dialog": when the wizard is what the window shows (G5).
- In step 1 of the wizard: when the certificate exception is offered (G12).
- In step 2 of the wizard: the third case of the provider comparison, a provider named by the check and none read by the service (G1).
- Under "The ordinary pairing is tried first": a new pairing still needs a new sign-in, as it did; a sentence was added that the new sign-in falls away once the condition of G11 is met, which it is not yet (G11).
- Under "The tree": the server's menu gained the two moves (G16).
- Under "One status at a time, per place": what the tray tooltip holds (G15). [ADR-0004](0004-multi-server-runtime.md), "Companion", says "the tooltip names each server"; it carries a pointer to G15 since 2026-11-11.
- The status line, and a pointer from "Accessibility" to the definition below (G18).

### The decisions

**G1. What `servers.add` says about the sign-in provider, and the code of its refusal.** `servers.add` always states the sign-in provider that the check showed the operator: the origin `servers.check` reported, or nothing when the check named none. Nothing is itself a statement, "the check named none", and is compared like any other value. The service compares what the request states with what it reads from the server itself, in both directions, and refuses with the code `provider_changed`, with no details and nothing stored, when the two differ: when it reads another provider, when it reads one where none was shown, and when it reads none where one was shown. The wizard then goes back to step 1 and checks again. The plan's wording was weaker (the origin "compared whenever it is given"); it was not taken ([ADR-0004](0004-multi-server-runtime.md), "Who names the sign-in provider").

- Rules out: an add that states no provider and is accepted whatever the service reads; `certificate_untrusted` or `invalid_request` as the code of this refusal; details on it.
- Built, in the contract, by pull request 51 (`4e01519`): the argument (`ServersAddArgs.ProviderOrigin`), the one rule both operations call (`SignInProviderOrigin.Agrees`), the code in the contract's table of codes with no details record, and the golden request `servers.add.request.json`, which carries `providerOrigin`. Text that names no origin is refused when the arguments are read, and a member left out and a null both state "none".
- Built by pull request 53 (`02f34c3`): the companion's own read of the sign-in description stops the sign-in when the provider it names is not the one the check reported, with the same three cases (`CompanionSignInState.ProviderChanged`).
- Built in the companion's client for version 2 of the pipe (`ManagementClient.AddServerAsync`): it takes the result of the check the operator saw, not an origin as text, states that check's provider, and refuses a check that failed; what that argument does not guarantee is under "Resolved while reviewing the management client, 2026-11-11".
- Not built yet: the comparison in the service, because nothing serves `servers.add`. The step that builds it has `ServersAddArgs.ProviderAgrees` to call.

**G2. `trust.set` has the same race, and how an operator accepts a provider that a server names later.** Before the exception is switched on for a server that exists, the Connection page runs `servers.check` for that server and shows both origins the exception would cover. `trust.set` then states the provider the operator was shown, as `servers.add` does: compared by the same rule, nothing included, and refused with the same code with nothing changed. A request that switches the exception off accepts nothing, so nothing is compared for it.

- Rules out: an exception that comes to cover an origin the operator was not shown; an origin that is compared only when it is given.
- Built, in the contract, by pull request 51: `TrustSetArgs.ProviderOrigin`, `TrustSetArgs.ProviderAgrees` and the golden request `trust.set.request.json`.
- Built in the companion's client (`ManagementClient.SetTrustAsync`): it takes the check's result too, and refuses to switch the exception on without one; unlike the add it accepts a check that failed, for the reason given under "Resolved while reviewing the management client, 2026-11-11".
- Not built yet: the Connection page, and the comparison in the service.

**G3. One rule for a name and one for an address, in the contract.** What a server's name may be and what an address may be are each one function of the pipe contract, which the service and the companion both call: `ServerName.Check`, which says which part of the rule a name breaks; `ServerName.Propose`, which makes a name from an address; and `ServerAddress.TryRead`.

- Rules out: a copy of either rule in the presentation assembly, which is how the three address rules of audit finding U16 came about.
- Built by pull request 51 (audit W39 says what the rules are and what is held by test).
- Not built yet: their callers in the window. The dialog's flow and the session client still apply their own checks to an address, as W39 records, so U16 stays open. The companion's client for version 2 takes the origin `ServerAddress.TryRead` returned and sends nothing for an address that rule refuses ("Resolved while reviewing the management client, 2026-11-11").

**G4. Who builds `transport.get` and `printers.discover` in the service.** The step of the multi-server build that builds the version 2 pipe server builds both, and makes `printers.discover` single-flight there. It is recorded in [ADR-0004](0004-multi-server-runtime.md), "Resolved while planning the wizard and tree build".

- Rules out: leaving either to the step that moves the window to version 2, which needs both from its first day.
- Not built yet.

**G5. When the wizard is what the window shows.** Only when the snapshot was answered, it reports the stored data as ready, and there are no servers. With no servers in any other state the window shows the Computer page, which says why: the upgrade from 1.x stopped at a file it names, a newer build wrote the data, the service was not reached, or a service of another pipe version answered. Add server is disabled with that reason.

- Rules out: a wizard on a service that would refuse every operation it sends (`state_newer`, `migration_incomplete`), and a wizard on a service that did not answer.
- Not built yet.

**G6. A computer whose 1.x files could not be read.** [ADR-0004](0004-multi-server-runtime.md), "The cases", says that with neither the pairing nor the session readable there are no servers, the files stay where they are and "the computer says why". The contract has no member that could say it: the stored state carries a status, the file an unfinished upgrade stopped at and two version numbers (`StoredState`). The decision is that the stored state gains a list of fixed values that name what was left in place unread; the plan's examples are `pairingUnreadable` and `sessionUnreadable`. It is a change to the contract for the service steps to make.

- Rules out: free text for the reason, and a companion that guesses it from the absence of servers.
- Not built yet. Until it is, such a computer shows the first-run wizard with no explanation.

**G7. Which control is the tree.** A `TreeView` in the left pane, in place of the `NavigationView` the window has today (`MainWindow.xaml:40`). The reason given is its automation: a tree with tree items that carry a level and a position, and the platform's own keys.

- The condition: this stands subject to a comparison of the two controls with Narrator and Accessibility Insights, made at the start of the step that builds the views and recorded here. How a `NavigationView` exposes nested items was not verified when the decision was taken, and has not been since. The view models are the same for either control.
- Rules out, once the comparison confirms it: a `NavigationView` with nested items.
- Not built yet.

**G8. Where Back works.** From Sign in to Server, where the step before stored nothing, and from Done to Printers. Not from Pair this computer and not from Printers, because the step before each of them stored something.

- Rules out: a Back that would have to undo a stored server or a stored pairing.
- Not built yet.

**G9. Leaving the wizard's page.** The wizard stays alive while the operator looks at another page. Add server shows that one is in progress, and starting another asks first whether to cancel it. There is one wizard at a time.

- Rules out: a wizard that navigation cancels, and two wizards at once.
- Not settled by the plan: what a step shows of its last result when the operator comes back. "One status at a time, per place" clears a page's message when the page is left.
- Not built yet.

**G10. A server that is signed in and not paired.** Its overview and its Connection page offer "Pair this computer", which enters the wizard at step 3. If the service then answers `not_signed_in`, the wizard goes to step 2. A repair still enters at the sign-in step.

- Rules out: sending a server that needs only a pairing through a sign-in first.
- Not built yet.

**G11. After `bind_refused`, whether the same credential is sent again with a repair.** The wizard keeps its session until the outcome is final and sends the same credential again with the repair the operator chose (`SessionSignInArgs.AssociatePairing` or `NewPairing`). When that is answered `sign_in_refused`, it falls back to a new sign-in.

- The condition: this holds only if the service, when it answers `bind_refused`, keeps nothing of the credential and leaves the session on the server alone. The service step that builds `session.signIn` must state which it does. That step is not built: in `src`, `session.signIn` is named only in the contract's own files (by search at `02f34c3`), so the condition is not met. Until it is, the decision is not in force and every repair starts with a new sign-in: this record said so of a new pairing, and the plan's other option says it of both. What the service does with such a session was not read when the decision was taken, and was not read for this section either.
- Rules out, once the condition is met: a second sign-in for a repair of a session the server has just accepted.
- Not built yet.

**G12. When the certificate exception is offered.** Only when the reasons Windows refused the certificate include that it does not trust the issuing authority (`untrustedAuthority` among the refusals of the certificate the check describes). A name mismatch alone, or no certificate at all, gets the certificate shown and no exception offered. If a check still fails after the exception was accepted, the step says that the exception does not cover it.

- Rules out: offering a development exception for a certificate issued to another name.
- Not built yet. The contract already carries the reasons (`PresentedCertificate.Refusals`). This decides what the wizard offers; it does not change Core, whose exception, once on, accepts a certificate from a covered origin whatever Windows held against it (`QqqSessionClient.EvaluateCertificate`).

**G13. This Windows account refuses a certificate that the service accepted.** Step 2 says that it is this account that refused it, shows the certificate, and offers no exception: an exception would be stored for a service that does not need one.

- Rules out: an exception stored with the server because of what this Windows account does not trust.
- Built in part by pull request 53: `CompanionSignIn` ends in `AccountRefusedCertificate` and keeps the refused certificate and who presented it (`CompanionSignIn.Rejection`).
- Not built yet: the step that shows it.

**G14. Health when there are no servers.** Red, with the text "Add a server to start printing". The plan's reason is that 1.x is red while it is not enrolled; the version 1 snapshot is (`AgentRuntime.GetSnapshot`).

- Rules out: a neutral state for a computer that cannot print for any server.
- Not built yet.

**G15. The tray tooltip.** The worst health, the count in each state, as many servers with their state as fit, and "+N more" for the rest; the tray menu lists every server. The plan counts 127 characters as what Windows shows of a tooltip, against eight names of up to 64 units; the number was not checked against Windows here.

- Rules out: a tooltip that names every server whatever their number and the length of their names.
- Not built yet.

**G16. How the operator orders the servers.** Move up and Move down, in the server's menu and with Alt+Up and Alt+Down. Each sends the whole new order (`servers.reorder`). There is no drag and drop.

- Rules out: drag and drop as the only way to reorder, which a keyboard cannot do.
- Not built yet.

**G17. Identifiers of rows.** A printer row is `Printer.<n>` and a job row `Job.<n>`, by position, each with an accessible name. The controls inside a row are found through the row.

- Rules out: an identifier made from a printer's or a job's own identifier, and inner identifiers that repeat with no row to find them through (audit U10).
- Not built yet.

**G18. What "accessible" is measured against.** The KofTwentyTwo standards have no rule that says what it is, so the table under "What the re-audit scores accessibility against" below is the definition.

- Rules out: scoring the criterion "Accessibility and UX consistency" against a reviewer's own list.
- Not built yet: none of the tests the table names exists.

**G19. How the interface is driven against a service that is not the real one.** The UI tests start the layout host, which compiles the product's own views into a test program (`tests/Qqq.PrintAgent.LayoutTests`), against a scripted version 2 service. The product gets no setting that points it at another pipe.

- Also taken under the standing instruction, and not built: the layout test project is added to the solution in the step that builds the interactive suites (plan step 15 of the wizard and tree build), so that every pull request builds it from then on (the plan names `Qqq.PrintAgent.slnx`, which is the solution `build/Verify.ps1` builds). The plan had left this half to the owner: it recommends it and calls it "the owner's call", because the project is outside the solution on purpose (neither solution file lists it, read at `02f34c3`). It was taken on the owner's behalf all the same, and the owner may reverse it before that step; it is listed for the owner in [TODO](../TODO.md). Nothing changes in either solution file until then, and until then each step that touches the layout tests builds them by hand and says so in its pull request.
- Rules out: a test route into the production transport.
- Not built yet.

**G20. Text a server influenced, in rows and nodes.** Audit finding W35 is to be closed in the contract. Until it is, the presentation assembly puts a status text, a job title and a job message through the contract's own printable form before it reaches a row or a node.

- Rules out: showing such text as it was received.
- Built in part by pull request 51: wherever a reply names an account, the principal, the tenant and the message beside them are printable and bounded (`ServerAuthentication`; W35 has what is still open).
- Not built yet: the rows and the nodes, and the same treatment for the other texts W35 lists. Found on 2026-11-11 while this was written: the contract's rule (`ManagementText`) is internal to the Windows assembly, which opens its internals to its tests and to the worker fixture only, so the presentation assembly cannot call it as it stands. The step that builds the rows has to make the rule reachable, or W35 is closed first.

**G21. Logging by template in the presentation assembly.** The presentation log takes a message template and its values, and new presentation code logs only that way (K22-CODE-CS-10). The older types, which join strings into a finished message, are not rewritten; they go when the window stops using them.

- Rules out: new code that builds a log message by joining strings.
- Built by pull request 53: `IPresentationLog` has the template form, and `CompanionSignIn`, `BrowserCallbackDispatcher` and `ProviderSignOut` log through it.
- Not built yet: the older types. The audit records them as U27.

Smaller points the plan settled with these, none of them built except where it says so:

- The step indicator of a repair counts only the steps of that run.
- The sign-in methods a step shows are those that the service's check and the companion's own read both found. Built by pull request 53 (`CompanionSignIn`), which logs a difference.
- The first page selected after an upgrade is the first server's Printers page, because that is where 1.x opened.

The plan has a fourth, what a server's overview shows, for which it points at the first test of its own step 9. It is not recorded here and is left to the step that builds the overview.

### Accepted limits

Recorded as limits of the design, not as work to do:

- The companion cannot show that another client is changing a server until one of its own requests is refused with `busy`. The snapshot does not say it.
- `servers.check` returns all five stages together, so step 1 shows no progress from stage to stage while the check runs.
- A check that the operator cancelled runs on in the service for up to 120 seconds, the limit of a long operation, and holds one of the pipe's sixteen instances while it does.
- Two companions in two Windows sessions can together ask for more than sixteen instances. Their reads are then skipped until their next turn.

### What the re-audit scores accessibility against

This is the definition G18 refers to. The [baseline audit](../AUDIT-2026-11-10.md) scores the companion on "Accessibility and UX consistency", and that criterion cannot be 9 or better with a Medium finding open against it. The plan names U9, U10 and U11 as the findings that therefore have to close; which criterion a finding counts against is for the re-audit's reviewer to say. The standards give it no content of their own: a search of `standards/`, `architecture/` and `policies/` in the standards repository at revision `707bbd33f0b9`, repeated on 2026-11-11, finds accessibility in one place, K22-AGENT-20, which tells an agent to preserve it when changing code and does not say what it is. What binds is "Accessibility" above and this section. The reference architecture for a Windows desktop application adds where the proof has to live: logic that can be tested without a window is outside the UI project and under the coverage gate (its section 2, with K22-TEST-10), and the window itself is tested end to end through UI Automation (its section 8).

What is to be built:

- **Tree.** The arrow keys move, expand and collapse; Home and End go to the first and the last node; Enter selects; the Menu key or Shift+F10 opens a server's menu; F2 renames; Delete removes, after the prompt; Alt+Up and Alt+Down reorder; F6 moves between the tree, the page and the footer. A server node's accessible name is the server's name, its health in words, and "certificate exception on" or "being removed" when that is so. Health is a glyph and a text, never color alone.
- **Wizard.** The step's title says "Step n of 5" and the step's name. Enter is the step's primary action and Escape is Cancel; inside the certificate warning, Escape closes the warning only. When the step changes, focus goes to the step's first input; after a result, to the place the view model names. A result is announced once.
- **Rows.** Every printer row and every job row has an accessible name, and its identifier is its position (G17).

How each requirement is shown. A requirement counts as shown only when both columns are: its gated test exists and passes on the commit the re-audit reads, and its interactive run is recorded in [ACCEPTANCE.md](../ACCEPTANCE.md) for a build of that commit. A row with no recorded run is not shown, and the finding it answers stays open. The middle column is in the presentation tests, which `build/Verify.ps1` runs and gates at 100% line coverage. The right column is the interactive suite, which the gate does not run; its runs are made on the interactive desktop and are to be recorded in ACCEPTANCE.md. The test names in the middle column are the plan's, and none of those tests exists yet; a name may change when its test is written, and what the row requires does not. The right column names each run and not what the run must observe to pass or to fail; that is to be defined, row by row, when the step that makes the runs (plan step 15) is written, and it is not defined here.

| Requirement | In the presentation tests (gated) | Interactive (recorded) |
| --- | --- | --- |
| Stable identifiers | Every interactive control in the companion's XAML, and every identifier set from code, has an identifier, and the set equals a recorded list, so a rename shows as a difference (`XamlContractTests`) | UI Automation finds each control by its identifier |
| `Server.<n>` and names | Server identifiers count from one and are renumbered after a reorder and after a removal; every node, row and step has a name that is not empty | The tree is a UI Automation tree with tree items |
| Not color alone | Every health has a glyph name and a text, different for each level, the unknown level included | A run in a high-contrast theme |
| Announced once, after the text is final | The number of announcements, and the text at the moment of each, for the footer and for every result of the wizard | Live-region events counted through UI Automation |
| Keyboard | The primary action and the cancel action of every state; every command has an access key that is unique in its menu, or a gesture; the focus target of every state | A walk of add, repair and remove with the keyboard only |
| Live regions declared | Status texts and message bars declare a live setting in the XAML (`XamlContractTests`) | A pass with Narrator |

## Resolved while reviewing the management client, 2026-11-11

The companion's client for version 2 of the pipe (`ManagementClient`, the presentation assembly) was reviewed independently before anything was built on it. Two findings needed a decision. Both were decided on 2026-11-11 by the coordinator of the 2.0 work, following the reviewer's recommended direction; they are not the owner's own words, and the owner can overrule them. The first reverses what the client did until then.

### A version mismatch is not proof that nothing was carried out

- What the client did: a reply that was not JSON, or was JSON without a version, was reported as "an older service answered", and a reply in another version as "a newer service answered". In both cases the result said that nothing was carried out and that there was nothing to check before repeating.
- What holds. A version 1 service does nothing with a version 2 request. The review ran it: all 30 golden version 2 requests, sent to this branch's version 1 runtime on a private pipe, were each answered `Error: Unknown command.`, and the printer backend was never called. For the released 1.x handlers (`v1.0.0`, `v1.2.1`) the same was read, not run. A newer service that keeps the contract answers in another version only to refuse the version.
- What does not hold. The client sees only the form of the reply. Text that is not JSON is also what any answer outside the envelope looks like: the pipe loop the 2.0 service grows from answers every exception, its own time limit included, as `Error: ...` text, and if one path of the version 2 dispatcher reaches that after an operation acted, the reply reads as "older service". A reply in another version need not be the refusal of the version: `{"v":"2","ok":true,"result":{}}` says the operation succeeded. And JSON without a version is not what a version 1 service sends at all (30 of 30 answers were not JSON), so its sender is not known.
- The decision.
  - The two failure values stay (`OlderService`, `NewerService`), and each still names the side to update.
  - For an operation the contract's operation table calls a read, the result is not unconfirmed, as before: a read changes nothing whoever answered it.
  - For every other operation the result is unconfirmed, and its message says so: which side to update, and that the operation may have been carried out and must be checked before it is repeated. No message says or implies that nothing was carried out.
  - JSON without a version is an unreadable reply (unconfirmed for every operation), not an older service.
  - Text that is not JSON stays "older service", with the rule above deciding whether there is something to check.
- Why this direction. With a real version mismatch every operation fails anyway, so reporting "check before repeating" costs the operator one look. The other direction told the operator to update one side and repeat a test print, a retry, an add, an enrollment or a sign-in that may have happened.
- What it means for the wizard and the pages. For an operation that is not a read, `OlderService` and `NewerService` are no longer among the failures after which nothing happened. They are treated as any result that is unconfirmed is treated, with the side to update named as well: after `servers.add` that is the state for an add whose outcome is not known ("Check" takes a snapshot), and after `pairing.enroll` the state for an enrollment whose outcome is not known. Whether there is something to check is read from the result's "unconfirmed" value, never from the failure value alone.
- What it asks of the service. No version 2 request may ever be answered outside the envelope, because the client reads any answer that is not JSON as an older service. The step that builds the version 2 pipe server needs a test of exactly that (recorded in `docs/TODO.md`).

### A snapshot that is known to be newer than the caller's last change

- What the client did: the snapshot had only the behaviour the refresh timer needs. A call made while a snapshot was in flight shared that one's answer, and with twelve requests in flight the call was skipped.
- What was wrong with that. A caller that reads the state to learn the outcome of a change could be given a snapshot the service had already answered from before the change, or none. The wizard's "Check" after an add whose outcome is not known ("nothing was stored") and after `enrollment_unconfirmed` ("a new code is offered only after a check found no pairing") are such callers, and a wrong answer there offers a repeat of the change. A page refreshed straight after a change could show the state from before it until the next tick. The version 1 client shares no answers, so it does not have this.
- The decision. The timer's behaviour stays the default: single-flight, and skipped at the ceiling. A caller can ask instead for a snapshot that is sent after its call (`SnapshotAsync(sentAfterTheCall: true)`). That call is never answered from a snapshot sent earlier: it waits out one that is in flight and then sends another. Callers that wait together may share the one that is sent after they all asked. At the ceiling it waits its turn and is not skipped. A caller that leaves while it waits has sent nothing. One snapshot is at the pipe at a time in either case.
- Who uses it. Every read that decides something about a change the same caller just made or could not confirm: both "Check" actions of the wizard, and a page's refresh after its own change. The timers keep the default.

### Smaller points settled in the same review

- **A check that failed is not the one the operator went on from.** Next is enabled only when all five stages pass, so `servers.add` is never rightly sent with a failed check. The client refuses one before anything is sent (it would otherwise state a provider, or none, that the operator did not accept). `trust.set` is not held to that: a server whose own certificate is refused is the one an exception is switched on for, and whether the check of an existing server can pass before the exception is on is for the service step to say. What the check argument does not guarantee, for either operation: the result is a public record, so one changed after the service returned it is sent as changed, and it does not name the address it was for, so a check of another server that names the same provider is not detected. The wizard and the Connection page have to keep each check with the address it was made for (G1, G2).
- **An address is passed on as the origin that was judged.** The client's two methods that take a server's address take the origin `ServerAddress.TryRead` returned, not the text the operator typed, and the request carries it as scheme, host and port (G3).
- **What the contract refuses is not sent, and the client adds no rule of its own.** A request is put to the contract's own check and its arguments are read back by the contract's own reader. Values those two accept are sent, whatever the service will say of them.
- **A reply is read only for what was asked.** A rename answered with another server's summary, an add answered with another address, a reorder answered with other servers and job details for another job are unreadable replies, so unconfirmed. The order a reorder's reply lists the servers in is not compared.
- **Text in a result reaches the view models as it arrived.** Only a refusal's message is already printable. The printable form of G20 is applied by the rows and the nodes.
