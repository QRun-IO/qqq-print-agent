# Printer approval as seen by the agent

Sharing a printer is a local choice; approving it is the server's. The agent must show both for the
server it is connected to, so a printer shared before a server change is never mistaken for one the
new server approved.

## Printing extension 2.4: the server pushes approval (client 1.2.0, QBit 1.1.0, protocol 1.1.0)

When discovery advertises a stable printing `2.4.0` or newer extension, the client adds
`printer-approval-v1` to its HELLO capabilities. The server then sends a `PRINTERS` frame
(`{ "printers": [ { "printerId", "enabled" } ] }`, the complete approval state of every printer
in the device's latest accepted inventory) after each accepted INVENTORY and whenever an
administrator approves or disables one of this device's printers. The frame is authoritative:
enabled printers are approved, a shared printer listed as disabled or not listed yet is awaiting
approval, and the operator catalog is not read on such a server. The reading is reset with the
connection like every other negotiated state. The contract is in the protocol repository
(`docs/REALTIME.md`, `RealtimePrinters` in the schema, `fixtures/realtime-printers.message.json`).

## Printing extension 2.3: the client reads the operator catalog

- The inventory snapshot (`INVENTORY` over the realtime channel) is
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

## Why both paths exist

The catalog is user-scoped. On a 2.3 server, a signed-in user who is not permitted to read it leaves
every shared printer at `NOT_REPORTED` with the reason in the tooltip and the activity log. The 2.4
PRINTERS frame is device-scoped and pushed on change, so it needs no operator permission and
updates the Printers page the moment an approval changes. Pair the 1.2.0 client with QBit 1.1.0
(extension 2.4.0) for that behaviour; against QBit 1.0.0 (2.3.0) the client stays on the catalog
and the wire is unchanged. The compatibility matrix and the negotiation rule (`printer-approval-v1`
minimum 2.4.0, capabilities re-evaluated on every socket) are in the protocol repository's
`docs/VERSIONS.md`.
