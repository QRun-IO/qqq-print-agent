# ADR-0005: The connection wizard and the server tree

Status: accepted for 2.0 under the owner's direction of 2026-11-10; not yet implemented; revised the same day after an independent review. Builds on [ADR-0003](0003-version-2.md) (a wizard replaces the connection dialog; servers are shown as a tree) and [ADR-0004](0004-multi-server-runtime.md) (server identity, the pipe contract).

## Context

The 1.2.x connection dialog is one scrolling form: address, a development certificate exception, a three-way pairing choice the operator must make before signing in, a discover button, two sign-in methods and an enrollment code. Every message the window shows is mirrored into the dialog's own feedback bar, which is never closed, so the dialog opens showing whatever was said last. The left pane lists Printers, Jobs and Logs for the one connection.

## Decisions

### The wizard is a page, not a dialog

Add server opens the wizard in the content area. A dialog cannot be resized by the operator, cannot host another dialog (the certificate warning, the removal confirmation), and was the source of the clipped layout the current form works around. When the computer has no servers, the wizard is what the window shows.

It has five steps, a step indicator, and Back, Next and Cancel. Enter moves forward and Escape cancels. Each step shows only what that step needs, and its own result; nothing from another step or another page appears in it.

1. **Server.** The address, and a name that defaults to the host. Checking the server asks the service to run the compatibility check (`servers.check`), because the service is what will connect and it trusts certificates as LocalSystem, not as the signed-in user. The step shows the five stages in order (reaching the address, a trusted secure connection, a QQQ server, a printing extension it can print through, a sign-in method it implements), each passed, failed with the reason, or not reached. Next is enabled only when all five pass. A certificate failure shows the certificate that was presented (subject, issuer, expiry, fingerprint, and why Windows refused it) and offers two ways forward: install the authority's root certificate in this computer's trusted root store, which is the lasting fix and needs no exception; or switch on the development-only exception for this one server, with its warning, which names the two addresses it will cover (the server and its sign-in provider). A re-check follows. Nothing is stored yet: the choice travels with the wizard and is saved when the server is. An address that is already a server on this computer says so and offers to open it.
2. **Sign in.** The methods the server offers and no others. Browser sign-in shows that it is waiting, can be cancelled, and gives up after three minutes; password sign-in clears the password after every attempt. The sign-in runs in the companion, as the user. If the user's Windows account refuses a certificate that the service accepted, the step says that it is this account that refused it. When sign-in succeeds the wizard adds the server (`servers.add`), which stores the server, the certificate exception if one was accepted and the session together or not at all. When it is the sign-in provider's certificate that is refused, this step shows it and offers the same two ways forward as step 1.
3. **Pair this computer.** For a new server this is the enrollment code and where to create it. A refused code says so and can be tried again. When the outcome is not known (the reply was lost, or the service stopped answering), the step says exactly that, shows what the service reports was left behind, and offers to check again rather than to enroll again.
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

The two repairs are: associate the existing pairing with this account (for a pairing created before accounts were linked), or set up a new pairing (for a revoked or replaced device), which needs a new sign-in and a new enrollment code and keeps history and printer choices.

The wizard cannot say why a bind was refused. The server answers 401, 403 or a failure with no reason, and 2.0 does not change the wire ([ADR-0003](0003-version-2.md)). So the choice between the repairs is deferred to the one case that needs it, not removed. A reason code on a refused bind is a request to the printing protocol, outside this release.

Repairing a server starts from that server's Connection page and enters the wizard at the sign-in step. The compatibility check runs again first, without showing step 1 unless it fails, because the sign-in methods on offer come from it.

### The tree

- One node per server, in the operator's order, showing its name and health as both a glyph and text, and a marker for as long as its certificate exception is switched on. Beneath it: Printers, Jobs and Connection. Selecting the server shows its overview.
- Add server, disabled with an explanation at eight.
- Computer (this computer's details, the Windows service, companion startup), Logs and Settings (appearance, updates, about).

Printers and Jobs are the pages 1.2.x has, scoped to the selected server. Connection gathers, per server, what 1.2.x spreads over the Settings page, the footer and the connection dialog: status, who is signed in, the realtime meter, sign in again, sign out, reconnect, the certificate exception, rename and remove.

A server's context menu offers reconnect, sign out, rename and remove. Removing asks first and says what it does: printing from that server stops, its pairing and session are deleted from this computer, its job history is no longer shown and is deleted after the retention period, and its device record on the server is not touched.

After an upgrade from 1.x the existing connection is the first server, named after its host, and the wizard is not shown.

### One status at a time, per place

- The footer and the tray icon show the worst health across servers; the footer names the count in each state and opens the first server that needs attention; the tray tooltip names each server and its state.
- Each server has its own busy state. A sign-in in progress on one server does not disable or silently drop commands on another, and refresh never stops.
- Browser sign-in is the one thing that cannot be per server, because Windows gives each user one callback inbox and one redirect address. One dispatcher owns the inbox and routes each callback by its OAuth state to the sign-in that started it, so two servers can be signing in at once and a late callback for one cannot fail the other.
- A message belongs to the page that caused it and is cleared when the operator leaves that page. The wizard never shows a message from elsewhere.

### Accessibility

Every interactive control has an AutomationId that stays stable across releases. Only one server's pages are on screen at a time, so identifiers inside them are the same for every server and carry nothing machine-generated. A server's tree node is `Server.<n>`, its position in the tree counted from one, with the server's name as its accessible name; its children are found through it. Tree items, rows and steps have accessible names. Progress and results are announced once, after the text is final. Everything is reachable and operable from the keyboard, and state is never conveyed by color alone.

Existing identifiers the UI tests use (`NavigatePrinters`, `NavigateJobs`, `NavigateLogs`, `ServerAddress`, `EnrollmentCode`, `EnrollDevice` and the rest) keep their meaning where the control still exists; where a control is replaced, the test moves with it in the same change.

### Where the logic lives

Every decision above (which step is next, whether Next is enabled, what a failure means, which repairs to offer, what each node shows) is in the UI-free presentation assembly and covered by unit tests. The views bind to it. The interactive UI tests cover the shell: that the tree and wizard render, navigate and announce.

## Consequences

The pairing choice no longer exists as a control up front, so documentation and support text that name its three options change. The wizard needs the typed compatibility result from Core, the service-side check and typed replies from the pipe; it cannot be built on message matching. Sign-in messages that name controls of the old dialog are rewritten.
