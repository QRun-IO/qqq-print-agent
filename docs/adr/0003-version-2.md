# ADR-0003: Version 2.0 — several servers, a connection wizard and enforced quality gates

Status: accepted by the owner on 2026-11-10; in progress on `release/2.0`. Supersedes the coverage waiver in [ADR-0002](0002-prerelease-quality-and-packaging.md); its MSI validation exceptions stand. Updated on 2026-11-10: the status of the coverage exception, and the hardware check as a release condition.

## Context

Through 1.2.x the service holds exactly one server connection: one saved pairing, one session, one realtime socket, and a management pipe whose requests carry no server identity. Testing against a second server means signing out and pairing again. The connection dialog puts the address, a certificate exception, three pairing choices, discovery, two sign-in methods and an enrollment code on one scrolling form.

A baseline audit on 2026-11-10 measured merged line coverage of 87% for Core, 69% for the Windows layer and 38% for the CLI, with the WinUI companion unmeasured and no gate. It found the runtime concentrated in one class holding every piece of connection state, about 1,300 lines of decision and formatting logic inside view classes, and a polling path in the runtime that only tests reach. ADR-0002 waived the coverage gate for the first prerelease; the product has been stable since 1.0.1.

## Decisions

**Several servers at once.** The service keeps up to eight server connections. Each has its own sign-in, pairing, printer sharing, printer profiles, media choices, certificate exception, journal scope and staging directory. A printer may be shared with more than one server; one machine-wide queue per printer keeps two servers from interleaving on a device. One connection failing must not stop another. Every edition gets this; none is limited to one server.

**In-place upgrade, no way back.** The 1.x pairing, history and printer choices become the first server without re-pairing. The installer already refuses downgrades. 1.x ends when 2.0.0 is released: the updater offers 2.0 to 1.x installations through the normal prompt and no 1.2.x patch follows.

**A wizard replaces the connection dialog.** Its steps are address, sign-in, pairing, printers to share, done. The pairing step detects what the server needs rather than asking the operator to pick from three options first.

**Presentation logic leaves the views.** Decisions and formatting move to a UI-free view-model assembly; views bind to it. The companion shows servers as a tree, each with its own printers and jobs; logs, settings, the Windows service and updates stay machine-level.

**The management pipe gets a versioned contract.** Requests name the server they address, replies are typed instead of matched by text prefix, and a client or service that does not understand the other's version says so.

**Coverage is gated.** `build/Verify.ps1` fails when an assembly falls below its minimum in `build/minimum-coverage.json`. Each minimum is the measured percentage rounded down to a whole number, less one point: a few lines in timing-dependent waits are reached on some runs and not others (five lines of the Windows layer moved between two runs of the same commit), and a minimum set at the measured value would fail at random. Minimums only move up, by the same rule, in the change that adds the tests. An assembly that has reached its release bar is recorded at the bar, not a point under it: the command line is at 100, where every line is reached by tests that do not depend on timing, and a point of slack would only hide a regression. The release bar is 100% for Core, the view-model assembly and the CLI, and 90% for the Windows layer, where the remainder is native printing, installer and service-control code behind thin adapters marked `[ExcludeFromCodeCoverage]` with a written reason. The 90% figure deviates from K22-TEST-10. The exception record for it is EX-0016 in the KofTwentyTwo standards repository, in pull request 58, which is open (head `7a58fb4` on 2026-11-10). The record's status is "Proposed, awaiting approval": an earlier wording was approved, a review then corrected the wording, and the corrected wording has not been approved. It lists K22-TEST-10 and, in part, K22-CODE-CS-11; it states a release bar of 90 and the minimum "73 at commit `6f0237d`"; and it makes a check on real printers a condition of releasing 2.0. Until it is approved, the deviation is not covered by an approved exception. The 90% is a release bar and not what the gate holds today. The recorded minimum for the Windows layer was 73 at `6f0237d` (74.84% measured) and has since risen to 80 with pull request 41 and to 86 with pull request 42 (`build/minimum-coverage.json` at `e2ca086`), against 87.47% measured (3,835 of 4,384 lines) on the reviewed head of pull request 42. K22-TEST-10 also asks for 80% coverage of changed lines; nothing measures that yet, and it is open work for this release.

**Test doubles are hand-written fakes at boundaries.** No mocking library is added. The polling path that only tests reach is removed once its tests run against the realtime path.

**Delivery.** Work lands as small pull requests on `release/2.0`; `main` stays 1.2.x until release. Gitea is the primary host, so the repository standard is to be met with Gitea's equivalents; an exception record mapping the GitHub-only requirements is to be proposed and is not yet filed.

## Consequences

The native printing API, wire payload and WebSocket subprotocol do not change: each server still sees one device. The pipe contract, the on-disk state layout and the CLI's option names do change, and 2.0 is the release that breaks them.

One LocalSystem service holding credentials for several servers has a larger blast radius than one. Isolation between connections is a security boundary and belongs in the [threat model](../security/threat-model.md) before the multi-server service merges.

Until the release bar is reached, a green gate means "no worse than the recorded minimum", not "fully covered".

## Release condition: the hardware check

Added on 2026-11-10. This is a condition for releasing 2.0, beside the two already on record: the release rule of the [baseline audit](../AUDIT-2026-11-10.md) and the coverage bars above. The exception record EX-0016 (above) makes a check on real printers a condition of releasing 2.0 as well.

The owner agreed on 2026-11-10 to two checks on real printers on the development PC: a Zebra ZT411 at 300 dpi for ZPL and a Canon office printer for PDF. Each print is approved by the owner at the time it is sent; nothing is printed on the strength of this record alone. Neither check has been run.

- **Early check.** After the printing seams step, the step that puts the native printing calls behind adapters. That step merged on 2026-11-10 as pull request 42 (`e2ca086`), so the early check is due. It has not been run. It runs without installing anything, by driving the built print helper executable. Its purpose is what the unit tests of that step cannot judge, because they run against fakes: the marshaling of the real calls, what real drivers answer, the real spooler's errors and the geometry of the output.
- **Final check.** On the release candidate, through the installed service (session 0, LocalSystem).
- **Inside both checks: a job asks about its own queue only.** Added on 2026-11-10 with the change recorded in [ADR-0004](0004-multi-server-runtime.md), "About printing". That change deliberately reduces what reaches Windows: for a job, queues other than the job's own get no per-queue call, where every queue's settings were read three times. Its tests run against fakes, so they cannot judge what only a real spooler and real drivers show, and each check takes that on, on a PC that has more queues installed than the one printed to: that the spooler's enumeration alone loads no driver; that the lookup of one queue reports what the full discovery reports for it (identifier, formats, media profile identifier and revision); that a job prints while another queue is paused or its device is switched off; and that a helper ended at its limit leaves the spooler usable. The early check can drive the two new helper operations (`list-without-media`, `find-with-media`) as it drives the others. Not run.
- "Ready to merge to `main`" is held until the final check is recorded in [ACCEPTANCE.md](../ACCEPTANCE.md), with the Windows version, driver and model, format, and paper or label size.
- The steps headed "No paper" that submit a job (2 and 3 below) put real jobs in the Windows spooler. They count as prints for the owner's approval, and the owner confirms that the queue is paused before each is sent. If the queue is not paused, paper comes out.
- The 1.2.1 values that steps 1 and 3 compare with are captured before the candidate is installed. Installing the candidate is the upgrade-qualification run, and it uses up the only 1.2.1 baseline on that PC, because the installer does not go back to a lower version.
- Each record keeps two statements apart: that the spooler accepted the job (the job is recorded SPOOLED with its spooler job number), and that paper came out, with what the owner saw on it. The first never stands in for the second.

A proposed order, from the reviewer of the printing seams step. It is a proposal: the owner has not agreed to it, and the reviewer wrote it without the author's own list. It was written for a run through the installed service, so it describes the final check; the early check drives the helper and can take only the steps that need no service, and which those are is decided when the early check is prepared.

1. No paper: discovery compared with 1.2.1 on the same machine, against values captured from 1.2.1 before the candidate was installed. Identifiers, formats, and media profile identifiers and revision hashes must be equal. Leave it refreshing for a while and look for helper crashes.
2. No paper, with the ZT411 queue paused: submit one ZPL job and inspect its name (`QQQ <jobId>`), its data type (RAW) and its size; cancel it from the product. Then delete a second job by hand and cancel it; the expected answer is ALREADY_ABSENT.
3. No paper, with the Canon queue paused: submit the same PDF from 1.2.1 (before the candidate is installed, keeping its pages and size) and from the build under test, compare pages and size, and cancel both.
4. One ZPL label.
5. A PDF ruler page on the Canon at actual size.
6. A PDF with selected media on the ZT411; scan a barcode.
7. Three copies; a ZPL file over 64 KiB; two pages in two copies (the order must be 1, 2, 1, 2).
8. FIT, with a page larger and a page smaller than the paper.
9. Job paper: 4 × 1 on the 4 × 6 default must print short or be refused before the document is started.
10. Change the printing defaults between selection and print; the expected result is a media mismatch and an empty queue.
11. Kill the service mid-job; no helper process may be left behind.
12. Fifty mixed jobs.

The reviewer also noted that the size of the rendered bitmap in session 0 needs one log line, or a harness with no printer, to be measured.
