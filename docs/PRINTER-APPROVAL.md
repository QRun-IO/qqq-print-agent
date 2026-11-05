# Printer approval as seen by the agent

Sharing a printer is a local choice; approving it is the server's. The agent must show both for the
server it is connected to, so a printer shared before a server change is never mistaken for one the
new server approved.

## What the client does today (1.2.0)

- The inventory snapshot (`INVENTORY` over the realtime channel, `PUT /device/printers` otherwise) is
  resent on every new connection, so a server that has never seen this computer's printers receives
  them as soon as the agent connects. New printers are disabled on the server until approved.
- The device routes never report approval, so after each accepted inventory, and about once a
  minute while connected, the service reads the operator printer catalog (`GET /operator/printers`,
  printing extension 2.3) and keeps the entries whose `deviceId` is this device. That catalog lists
  only approved, exposed destinations the signed-in user may use, so an entry is the server's
  confirmation of approval. The reading is kept in memory and reset with the connection.
- Each printer in the status snapshot carries a `serverState`: `APPROVED`, `AWAITING_APPROVAL`,
  `NOT_SHARED`, `NOT_REPORTED` (the catalog could not be read; the reason is in `printerSync.detail`
  and the activity log) or `NOT_CHECKED` (no server asked yet). The Printers page shows it in the
  Server column, the summary line and the expanded details.

## The gap

The catalog is user-scoped. A signed-in user who is not permitted to read it, or a server older than
printing extension 2.3, leaves every shared printer at `NOT_REPORTED`. The device itself has no
route that says "approved", so a server-side addition is the complete fix.

## Proposed device-facing extension (for the server team)

Additive, no new wire version:

1. The `INVENTORY` acknowledgement and the `PUT /device/printers` `Acknowledgment` gain an optional
   `printers` array of `{ "printerId": string, "enabled": boolean }` for every printer in the
   accepted snapshot, reflecting the server's current approval.
2. A new realtime frame `PRINTERS` with the same payload, sent to the connected device whenever an
   administrator approves or disables one of its printers, so the companion updates without
   waiting for the next catalog read.
3. Discovery advertises the capability as `nativePrinterApproval` so the agent can prefer it over
   the catalog and stop polling the catalog when it is present.

When this lands, the agent takes approval from (1) and (2) first and uses the catalog only on
servers without the capability. Until then, the catalog path above is the behaviour.
