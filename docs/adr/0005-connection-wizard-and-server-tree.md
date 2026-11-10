# ADR-0005: The connection wizard and the server tree

Status: accepted for 2.0 under the owner's direction of 2026-11-10; not yet implemented. Builds on [ADR-0003](0003-version-2.md) (a wizard replaces the connection dialog; servers are shown as a tree) and [ADR-0004](0004-multi-server-runtime.md) (server identity, the pipe contract).

## Context

The 1.2.x connection dialog is one scrolling form: address, a development certificate exception, a three-way pairing choice the operator must make before signing in, a discover button, two sign-in methods and an enrollment code. Its result messages share one feedback bar with the rest of the window. The left pane lists Printers, Jobs and Logs for the one connection.

## Decisions

### The wizard is a page, not a dialog

Add server opens the wizard in the content area. A dialog cannot be resized by the operator, cannot host another dialog (the certificate warning, the removal confirmation), and was the source of the clipped layout the current form works around. When the computer has no servers, the wizard is what the window shows.

It has five steps, a step indicator, and Back, Next and Cancel. Enter moves forward and Escape cancels. Each step shows only what that step needs, and its own result; nothing from another step or another page appears in it.

1. **Server.** The address, and a name that defaults to the host. Checking the server runs the compatibility check and shows its five stages in order (reaching the address, a trusted secure connection, a QQQ server, a printing extension it can print through, a sign-in method it implements), each passed, failed with the reason, or not reached. Next is enabled only when all five pass. A certificate failure shows what was refused and offers the development-only exception for this one server, with its warning, followed by a re-check. An address that is already a server on this computer says so and offers to open it.
2. **Sign in.** The methods the server offers and no others. Browser sign-in shows that it is waiting, can be cancelled, and gives up after three minutes; password sign-in clears the password after every attempt. The server entry is created on this computer only after sign-in succeeds.
3. **Pair this computer.** For a new server this is the enrollment code and where to create it. The step explains what a failure left behind in the words the service reports.
4. **Printers.** The Windows printers that can print unattended, none selected. Sharing can be skipped and done later.
5. **Done.** The server's name, who is signed in, how many printers are shared and that the server must approve them, and a button that opens the server.

Cancelling before sign-in leaves nothing behind. Cancelling after it asks whether to keep the server (signed in, not paired) or remove it.

### Pairing is detected, not chosen up front

The three choices of the old form become outcomes the wizard reaches by trying the ordinary thing first:

- A server with no pairing on this computer needs an enrollment code.
- Signing in again to a paired server binds the saved pairing. That is the common case and needs no decision.
- When the server refuses that bind, the wizard says why and offers the two repairs, each with what it does: associate the existing pairing with this account (for a pairing created before accounts were linked), or set up a new pairing (for a revoked or replaced device), which needs a new sign-in and a new enrollment code and keeps history and printer choices.

Repairing a server starts from that server's Connection page and enters the wizard at the sign-in step.

### The tree

- One node per server, in the operator's order, showing its name and health as both a glyph and text. Beneath it: Printers, Jobs and Connection. Selecting the server shows its overview.
- Add server, disabled with an explanation at eight.
- Computer (this computer's details, the Windows service, companion startup), Logs and Settings (appearance, updates, about).

Printers and Jobs are the pages 1.2.x has, scoped to the selected server. Connection holds what the Settings page's "Server connection" section held, per server: status, who is signed in, the realtime meter, sign in again, sign out, reconnect, the certificate exception, rename and remove.

A server's context menu offers reconnect, sign out, rename and remove. Removing asks first and says what it does: printing from that server stops, its pairing and session are deleted from this computer, its job history stays until retention prunes it, and its device record on the server is not touched.

After an upgrade from 1.x the existing connection is the first server, named after its host, and the wizard is not shown.

### One status at a time, per place

- The footer and the tray icon show the worst health across servers; the footer names the count in each state and opens the first server that needs attention; the tray tooltip names each server and its state.
- Each server has its own busy state. A sign-in in progress on one server does not disable or silently drop commands on another, and refresh never stops.
- A message belongs to the page that caused it and is cleared when the operator leaves that page. The wizard never shows a message from elsewhere.

### Accessibility

Every interactive control has an AutomationId that stays stable across releases; identifiers inside a server's pages include the server's identifier so that two servers never share one. Tree items, rows and steps have accessible names. Progress and results are announced once, after the text is final. Everything is reachable and operable from the keyboard, and state is never conveyed by color alone.

Existing identifiers the UI tests use (`NavigatePrinters`, `NavigateJobs`, `NavigateLogs`, `ServerAddress`, `EnrollmentCode`, `EnrollDevice` and the rest) keep their meaning where the control still exists; where a control is replaced, the test moves with it in the same change.

### Where the logic lives

Every decision above (which step is next, whether Next is enabled, what a failure means, which repairs to offer, what each node shows) is in the UI-free presentation assembly and covered by unit tests. The views bind to it. The interactive UI tests cover the shell: that the tree and wizard render, navigate and announce.

## Consequences

The pairing choice no longer exists as a control, so documentation and support text that name its three options change. The wizard needs the typed compatibility result from Core and typed replies from the pipe; it cannot be built on message matching.
