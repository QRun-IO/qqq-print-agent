# ADR-0006: A print station runs as its own identity, not a person's sign-in

Status: proposed 2026-11-10; not implemented. Depends on the server contract in
[qbit-printing `docs/STATION-IDENTITY.md`](https://git.kof22.com/QRun-IO/qbit-printing/src/branch/feat/station-identity-20261110/docs/STATION-IDENTITY.md)
(printing extension 2.5.0), which is itself a draft. This record changes the device-to-server boundary, so the
threat model changes with the implementation, not before it.

## Context

The service prints with nobody at the computer, but it authenticates as the person who signed in on it. It stores
that person's QQQ session token, renews the session every five minutes (`SessionResumeInterval`,
`AgentRuntime.Authentication`), and stops printing when the session ends (`BlockAuthorization`) until a person
signs in again. [QQQ-SIGN-IN.md](../QQQ-SIGN-IN.md) says so plainly: unattended operation "can require a new
sign-in".

Two things make that untenable for a print station:

- Hosts are adopting automatic logoff: a session ends after 15 minutes without a real user action, and after 12
  hours whatever happens. The service has no user action to report, so a station would stop 15 minutes after
  every sign-in. Renewing harder is not an answer: a client that keeps a session alive with nobody there is
  exactly what the rule exists to prevent.
- Everything the station does is recorded as that person, around the clock, from a computer they are not at.

The server side now offers a second way in. An administrator creates a one-use **station enrollment code**. The
client redeems it at `POST /qqq/v1/qbit/printing/stations/enroll` with no session and receives the device
pairing plus a credential for an identity that belongs to the station alone. With that credential the station
obtains short access tokens from the identity provider and sends them as `Authorization: Bearer`. The identity
can do only what a station does, on its own device.

## Decision

The client gains a second authorization mode per server, **station**, beside the existing **person** mode.
Person mode is unchanged and remains the way to connect a desk printer that someone sits at.

### Enrollment

- CLI: `qqq-print-agent enroll-station --server URL`, reading the code from `QQQ_PRINT_ENROLLMENT_CODE` or
  standard input exactly as `enroll` does. It needs no sign-in, so unlike `servers.add` it can be scripted for a
  computer nobody logs in to.
- Wizard ([ADR-0005](0005-connection-wizard-and-server-tree.md)): step 2 offers "This computer is a print
  station" when the server's discovery document lists printing 2.5 or later. Choosing it skips the browser and
  asks for the station code; the remaining steps are the same.
- The service performs the exchange, never the companion: the response carries two secrets and they go straight
  to the store. The pipe request carries the code; nothing secret comes back over the pipe.
- A refused code (401) leaves any existing pairing and session untouched, as a failed enrollment does today.
- Enrolling a station on a server that has a person's session ends that session (server sign-out, then the
  stored token is deleted). The person's pairing is replaced: a station device is a new device on the server.

### What is stored

`AgentAuthentication` gains a station credential (`username`, `secret`, `expiresAt`) as an alternative to the
session credential; exactly one is present. It is written the way the session file is: DPAPI under the service
account, in `servers/<serverId>/`, inside the directory that only SYSTEM and Administrators can read
([ADR-0004](0004-multi-server-runtime.md)). Access tokens are held in memory only. The companion never
receives the credential or a token; snapshots and logs never contain them.

### Authorizing requests

- The token endpoint comes from the provider discovery document the client already fetches for browser sign-in
  (it validates the issuer there; `token_endpoint` is read from the same document and must be HTTPS on the
  issuer's origin). The client id comes from the server's authentication metadata, as it does for sign-in.
- The request is the provider's client-credentials grant for a service account: `grant_type=client_credentials`,
  `client_id`, `username`, `password=<secret>`, `scope` as published by the server. No client secret.
- This is the first request the client sends to an origin other than the server. It goes through its own
  transport with the same rules as `QqqSessionClient` (no redirects, no cookie jar, bounded response, 20 second
  limit) and is allowed to exactly one origin: the issuer the server published.
- A token is replaced when less than a fifth of its lifetime or 60 seconds remains, whichever is more. The
  server ends the realtime connection when the token it was opened with expires, so the runtime reconnects the
  socket whenever the token changes. That is the path a rotated session token already takes.
- `QqqSessionClient` sends `Authorization: Bearer <token>` where it sends the session cookie today, on HTTP and
  on the socket upgrade, never both. Device headers are unchanged.
- `GET printing/session` must answer with the station's own principal and this device. `RequireSameIdentity`
  applies as it does for a person.

### Failures

| What happens | What the client does |
| --- | --- |
| The provider is unreachable or answers 5xx | Keeps the pairing and credential, retries with the reconnect backoff, shows "Cannot reach the sign-in service". Printing resumes by itself. |
| The provider answers `invalid_grant` or `invalid_client` | The identity was revoked or its secret expired. Stops printing, keeps history and printer choices, shows "This station must be enrolled again". No retry loop. |
| The server answers 401 or 403 to a valid token | The device was revoked or the identity lost its permission. Same as above. |
| The secret is within 14 days of `expiresAt` | Shows a warning in the server's Connection page and in the snapshot, so an administrator can re-enroll before it stops. |

Rotation and recovery are both re-enrollment with a new code. The client never asks a person to sign in on
behalf of a station.

### What does not change

The print pipeline, the journal, claims and acknowledgements, printer exposure, updates, the pipe ACL and
person mode. The wire payload stays v1.

## Consequences

- [ADR-0003](0003-version-2.md) said 2.0 does not change the wire. This uses a new server route and a new
  credential on existing routes, so it ships in the first release after the server supports printing 2.5, and
  the compatibility check must report an older server as "this server cannot enroll print stations", not as a
  sign-in failure.
- The threat model gains a row: *Station credential at rest: DPAPI under the service account, ProgramData ACL;
  a local administrator or SYSTEM compromise yields the station's identity, which can act only for this device
  and is revoked from the server in one action.* The "Device to server" row gains: *or the station's own
  identity plus device proof.*
- New code is in Core (credential type, token client, bearer mode of `QqqSessionClient`), Windows (store,
  runtime mode, enrollment), CLI (verb) and Presentation (wizard choice), each to its coverage minimum. Core and
  Presentation stay at 100%.
- A signed release is required before any station can use it.

## Tests that must exist before this is accepted as implemented

- Token client: discovery validation, the exact form body, refusal of redirects and of an endpoint off the
  issuer's origin, `invalid_grant` versus outage, renewal timing, no secret or token in any log or exception.
- Session client: bearer and cookie are mutually exclusive; the socket upgrade carries the bearer; a 401 with a
  bearer maps to "enroll again".
- Runtime, against the worker fixture server: enroll with a code, print, token renewal reconnects the socket
  without losing or duplicating a job, revocation stops printing without a retry loop, restart restores the
  station from the store with no network sign-in.
- The proof this exists for: with the fixture server ending stored sessions after a few seconds without user
  activity, a station keeps printing past that time and a person-mode connection does not.
