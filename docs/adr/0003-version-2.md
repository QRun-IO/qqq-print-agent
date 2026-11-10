# ADR-0003: Version 2.0 — several servers, a connection wizard and enforced quality gates

Status: accepted by the owner on 2026-11-10; in progress on `release/2.0`. Supersedes the coverage waiver in [ADR-0002](0002-prerelease-quality-and-packaging.md); its MSI validation exceptions stand.

## Context

Through 1.2.x the service holds exactly one server connection: one saved pairing, one session, one realtime socket, and a management pipe whose requests carry no server identity. Testing against a second server means signing out and pairing again. The connection dialog puts the address, a certificate exception, three pairing choices, discovery, two sign-in methods and an enrollment code on one scrolling form.

A baseline audit on 2026-11-10 measured merged line coverage of 87% for Core, 69% for the Windows layer and 38% for the CLI, with the WinUI companion unmeasured and no gate. It found the runtime concentrated in one class holding every piece of connection state, about 1,300 lines of decision and formatting logic inside view classes, and a polling path in the runtime that only tests reach. ADR-0002 waived the coverage gate for the first prerelease; the product has been stable since 1.0.1.

## Decisions

**Several servers at once.** The service keeps up to eight server connections. Each has its own sign-in, pairing, printer sharing, printer profiles, media choices, certificate exception, journal scope and staging directory. A printer may be shared with more than one server; one machine-wide queue per printer keeps two servers from interleaving on a device. One connection failing must not stop another. Every edition gets this; none is limited to one server.

**In-place upgrade, no way back.** The 1.x pairing, history and printer choices become the first server without re-pairing. The installer already refuses downgrades. 1.x ends when 2.0.0 is released: the updater offers 2.0 to 1.x installations through the normal prompt and no 1.2.x patch follows.

**A wizard replaces the connection dialog.** Its steps are address, sign-in, pairing, printers to share, done. The pairing step detects what the server needs rather than asking the operator to pick from three options first.

**Presentation logic leaves the views.** Decisions and formatting move to a UI-free view-model assembly; views bind to it. The companion shows servers as a tree, each with its own printers and jobs; logs, settings, the Windows service and updates stay machine-level.

**The management pipe gets a versioned contract.** Requests name the server they address, replies are typed instead of matched by text prefix, and a client or service that does not understand the other's version says so.

**Coverage is gated.** `build/Verify.ps1` fails when an assembly falls below its minimum in `build/minimum-coverage.json`. Each minimum is the measured percentage rounded down to a whole number, less one point: a few lines in timing-dependent waits are reached on some runs and not others (five lines of the Windows layer moved between two runs of the same commit), and a minimum set at the measured value would fail at random. Minimums only move up, by the same rule, in the change that adds the tests. The release bar is 100% for Core, the view-model assembly and the CLI, and 90% for the Windows layer, where the remainder is native printing, installer and service-control code behind thin adapters marked `[ExcludeFromCodeCoverage]` with a written reason. The 90% figure deviates from K22-TEST-10; an exception record for it is to be proposed to the standards register and is not yet filed. K22-TEST-10 also asks for 80% coverage of changed lines; nothing measures that yet, and it is open work for this release.

**Test doubles are hand-written fakes at boundaries.** No mocking library is added. The polling path that only tests reach is removed once its tests run against the realtime path.

**Delivery.** Work lands as small pull requests on `release/2.0`; `main` stays 1.2.x until release. Gitea is the primary host, so the repository standard is to be met with Gitea's equivalents; an exception record mapping the GitHub-only requirements is to be proposed and is not yet filed.

## Consequences

The native printing API, wire payload and WebSocket subprotocol do not change: each server still sees one device. The pipe contract, the on-disk state layout and the CLI's option names do change, and 2.0 is the release that breaks them.

One LocalSystem service holding credentials for several servers has a larger blast radius than one. Isolation between connections is a security boundary and belongs in the [threat model](../security/threat-model.md) before the multi-server service merges.

Until the release bar is reached, a green gate means "no worse than the recorded minimum", not "fully covered".
