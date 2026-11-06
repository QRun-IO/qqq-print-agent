# Printing cohort handoff for the Forge agent — 2026-11-05

Reusable changes were made in the printer client and the printing QBit repositories. Existing
authentication, discovery, enrollment, inventory and printing contracts are preserved; there are
**no breaking changes**. Nothing was installed over Forge's working client or dependencies; adoption,
pinning and the pairing tests (browser sign-in, session resume, enrollment, server printing,
TC22 → Echo → Z2) are yours. The previous cohort stays available for rollback.

## Exact versions and artifacts

| Component | Version | Tag / source | Artifact | SHA-256 |
| --- | --- | --- | --- | --- |
| Protocol (contract + fixtures) | 1.1.0 | `v1.1.0` at `5005f3c9e27053b6c327bb923695012832c0fdcc` | https://git.kof22.com/QRun-IO/qqq-print-protocol/releases/tag/v1.1.0 — `qqq-print-protocol-1.1.0.tar.gz` | `541f0f3054cbfe9ce16e7dea58fad01b55a7f0e9246ade8f30df1bb826940703` |
| Printing QBit (server) | **1.1.2**, native printing extension **2.4.0**, on QQQ **4.1.0-RC.22** from the Gitea registry (1.1.1 is the same code on RC.21 from Maven Central; 1.1.0 is superseded: do not adopt it) | `v1.1.2` at `f5639a98be9a2f15d4c61cf0955fe4ecef85824f`, push build 6435 + tag build 6475 | Maven `com.kingsrook.qbits:qbit-printing:1.1.2` from `https://git.kof22.com/api/packages/QRun-IO/maven` (repository id `qrun-io`, reader token); same bytes on https://git.kof22.com/QRun-IO/qbit-printing/releases/tag/v1.1.2 | JAR `786e6e570c5df29b01529562efb246a4f4562bb1d9fff6bea77d264e0d7536ce`, POM `7691a4e1cb7b03c7a559b0e2b92eacd92f86545b98bddafac86fc70f8602b03d` |
| Windows print client | **1.2.1** (1.2.0 content + release-pipeline fix; tag v1.2.0 produced no installer) | `v1.2.1` at `3b9d75d18f9cc8b80f4cf6d5aae81cccf8d61243`, GitHub release run 42223525626 | Signed installers: `QqqPrintAgent-v1.2.1-x64.msi` (generic) and `GgForgePrintAgent-v1.2.1-x64.msi` (GG Forge edition), `SHA256SUMS`, on https://github.com/QRun-IO/qqq-print-agent/releases/tag/v1.2.1 and byte-identical on https://git.kof22.com/QRun-IO/qqq-print-agent/releases/tag/v1.2.1 | generic `09920872b3447f8679b404f2b5ebd7d9fe82204b8007db63d58e4287f436f2eb`, GG Forge `5ce489092c1403449174b8a35eda039cd452e8685cbfe436d99aea3e15a5d8b4`; Authenticode valid (CN=James Maes, Microsoft timestamp); GitHub provenance attestation verified |

Previous cohort for rollback: protocol 1.0.0, QBit 1.0.0 (extension 2.3.0), Windows 1.0.1 / 1.1.1.

**2026-11-06 update (QBit 1.1.1).** Forge's qualification of 1.1.0 found that the realtime upgrade fails before WELCOME behind an ingress that lowercases header names (the device id was read case-sensitively from the WebSocket context; the same defect exists in 1.0.0, which Forge ran with a local patch). 1.1.1 resolves the device once on the authorized upgrade and carries it on the connection, with a regression test that connects with lowercase header names. No contract change: the pairing matrix and everything else in this handoff is unchanged; replace the `1.0.0-forge…headers1` coordinate with the published 1.1.1 and drop the local patch.

**2026-11-06 update (QBit 1.1.2, Gitea-only runtime).** James's direction: every QRun artifact resolves from the QRun-IO Gitea registry, no more Maven Central. 1.1.2 is the 1.1.1 code certified on QQQ **4.1.0-RC.22** (the core version Forge already pins; it exists only in the Gitea registry) with `-Pui-demo verify`, 200 tests, 0 failures, and published by the shared `ci-workflows` pipeline. `qqq-frontend-next` 1.0.0-RC.37 is also mirrored into the registry byte-for-byte from Central. Pin `com.kingsrook.qbits:qbit-printing:1.1.2` together with core 4.1.0-RC.22 and read both from `https://git.kof22.com/api/packages/QRun-IO/maven`; third-party libraries still come through the Maven Central mirror until the registry proxies them. No contract change; pairing matrix unchanged.

## What changed, by contract

- **Printer approval over the realtime channel (extension 2.4.0, additive).** Client advertises
  `printer-approval-v1` in HELLO only when discovery shows a stable printing 2.x with minor >= 4.
  Server then sends a `PRINTERS` frame (`{ "printers": [ { "printerId", "enabled" } ] }`, the complete
  approval state of the device's exposed printers) after each accepted INVENTORY and whenever an
  administrator approves or disables one of that device's printers. Not acknowledged. A 2.3 peer on
  either side keeps the exact 1.0 wire: 1.0.x/1.1.x clients never advertise it and never receive it;
  the 1.2.1 client against QBit 1.0.0 reads the operator catalog instead (user-scoped; shows "Not
  reported" without operator permission).
- **Remove printers / Remove device (QBit).** Archives with `removed_at`; hidden from lists, catalog,
  claims and the PRINTERS frame; a re-shared printer returns as awaiting approval; retention purges
  removed rows that no job references. QQQ 4.1 has no table-level default filter, so the raw Printers
  / Print devices tables still show removed rows until a "Removed at is empty" saved view is added.
- **Client sign-in nonce.** Sends `expectedNonce` under REQUIRED, OPTIONAL and the discovery capability
  `nativeSessionNonceValidation`; legacy servers unchanged (PR 11 content, already merged earlier).
- **Client UX/ops.** Server column on the Printers page; verbose sign-in diagnostics with DEBUG level;
  log size rollover; per-server untrusted-certificate option (development only, origin-bound);
  connection dialog rework; Clear logs button in the log viewer; client editions (GG Forge build is a
  separate MSI with its own update asset; one edition per PC, shared UpgradeCode).

## Pairing matrix (what must hold in your tests)

| Server | Client | Expected |
| --- | --- | --- |
| QBit 1.0.0 (2.3.0) | 1.2.1 | No `printer-approval-v1` in HELLO; catalog fallback; everything else as 1.1.1 |
| QBit 1.1.2 (2.4.0) | 1.0.1 / 1.1.1 | Four-capability HELLO at most; no PRINTERS frame; unchanged behaviour |
| QBit 1.1.2 (2.4.0) | 1.2.1 | Five-capability HELLO; PRINTERS after inventory and on approval change; Printers page shows Approved / Awaiting approval live |

## Evidence

- Protocol: `npm run check` (79 schema definitions, 82 fixtures), lint, typecheck, quality all green on 5005f3c.
- QBit: `mvnw -Pui-demo verify` 199 tests, 0 failures, 0 Checkstyle; main run 5473 verify + secrets + gate + post-publish green; registry files read back byte-for-byte.
- Client: hosted GitHub CI run 42116396114 on the PR head (197 Core + 362 Windows tests, 0 failures); Gitea required checks green on both merges; release run 42223525626 (verify, security, CodeQL, sign generic + GG Forge, assemble, publish).

## Notes for adoption

- Pin all three together. The client's CI fixture pin is `ac835f8b9396b641b525669249fdb4a1e2a50c99`; the QBit's is `5005f3c9e27053b6c327bb923695012832c0fdcc` (docs PRs 29 and 12 pending).
- Forge must approve the shared printers on the server after the first inventory from a 1.2.0 client, as before; the difference is that the client now shows that state.
- Coordinate installation on the Forge print station yourself; the GG Forge rc.3 build on James's PC is a local test build (file version 1.2.0.3) and will update to the signed 1.2.1 from its own GG Forge asset.
