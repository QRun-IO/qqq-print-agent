# Upgrade-preservation qualification plan — candidate 0.4.0-rc.1

Status: **plan only.** Nothing here has been run. The installed baseline is 0.3.2-rc.1 (MSI SHA-256 `1191C337265469EB06136AF232DB57FAC6BC5014357521D80B35E5CCA5AF6E18`) against the local QQQ on port 18081 (API 1.8 / printing 2.0). This plan follows the observer pattern recorded for the 0.3.1 and 0.3.2 acceptances in [ACCEPTANCE.md](ACCEPTANCE.md). It needs explicit user authorization before any installation, and it never submits a job.

## Preconditions (all must hold before an authorized install)

1. Final candidate head has a green hosted gate, including the Core test job. A green hosted gate does not clear the local Defender detection of the Core test assembly.
2. The candidate MSI comes from that exact head's CI artifact. Record its SHA-256 and compare with `SHA256SUMS` in the artifact.
3. Static MSI inspection (no install): ProductVersion `0.4.0`; app, service and CLI payloads all carry the same full informational version `0.4.0-rc.1+<head sha>`; Windows App SDK resources, service configuration (LocalSystem, automatic start, two 30-second recovery restarts), callback registration and the packaged notice files match the 0.3.2 baseline except for intended differences. Stable component identities must match the baseline so Windows Installer upgrades rather than duplicates.
4. A read-only "before" report exists for the installed 0.3.2 state (below).

## Before/after observations (read-only, no secrets)

Capture the same fields before and after. Use hashes or counts for protected material; never print credentials, device keys, tokens or document payloads.

| Item | Method | Pass condition |
| --- | --- | --- |
| Component versions | File version/ProductVersion of app, service and CLI | After: all three equal `0.4.0-rc.1+<head>`; bytes equal the CI payload |
| Machine credential file | Existence and SHA-256 only | Unchanged by the upgrade |
| Pairing and identity | Device ID presence and server URL (no token) | Unchanged |
| Local job history | Per-row digest of job ID, state, creation time, printer ID | Every prior row present with identical digest; additions accounted for separately |
| Server job history | Same digests from the server view | Prior rows unchanged |
| Printer exposure and profiles | Exposed set and per-printer profile fields | Unchanged |
| Log settings | Retention days and daily cap | Unchanged |
| Service configuration | SCM start type, account, recovery actions | Unchanged |
| Connectivity | Native QQQ session and authenticated WebSocket state | Restored without operator action |
| Companion | In-app update path (Not now, then Update and restart) | Same handoff as 0.3.2; app reopens Connected |

## Compatibility checks specific to this candidate

- **Older server, new client.** Against the local printing 2.0 server the HELLO must carry exactly the three pre-existing capabilities and no `printer-media-v1`, inventory must carry no `mediaProfiles`, and existing jobs must behave as before. This is the expected state on the installed local server and is itself a required qualification result.
- **Capability only on 2.3+.** `printer-media-v1` and `mediaProfiles` appear only after the client discovers a stable printing 2.3+ server in major 2. A printing 2.2 (rendering) server must leave the client a three-capability peer. Qualify against a server that actually runs the 2.3.0 candidate, in an isolated environment, not the installed 18081 instance.
- **Latency diagnostic line.** After upgrade, an authorized later job (not part of this plan) writes one `timing` line per job to the service log with the job ID only. Confirm that the line contains no document text.

## Failure handling

- Windows Installer same-upgrade-code downgrade is not supported. Keep the 0.3.2 MSI and its hash so a manual return can be planned, and treat that as a separate authorized operation.
- An MSI exit code of 3010 means restart required; report it as such. Do not delete journal, staging or credential files to recover.
- A mismatch in any prior-row digest, credential hash or exposure set stops qualification and is reported as a failure with the earlier evidence preserved.

## Explicitly not covered here

Physical output with a selected media profile, Authenticode signing, update download or installer failure/cancellation, reboot recovery, installed browser/private-callback sign-in against a published API 1.9+ server, and any 1.0 claim. Each needs its own authorization and evidence.
