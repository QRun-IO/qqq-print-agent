# Stable release signing

Stable `vX.Y.Z` tags publish a signed MSI; `vX.Y.Z-rc.N` tags stay unsigned prereleases. Signing uses the
same Azure Artifact Signing account, profile and publisher as
[MarkdownViewr.Windows](https://github.com/KofTwentyTwo/MarkdownViewr.Windows) (its `docs/azure-signing-admin.md`
and `docs/release-signing.md`), with the same pinned `azure/login` and `azure/artifact-signing-action`. This
repository adds no signing service, key export or secret.

## What the release workflow does

`.github/workflows/release.yml` first validates the tag; for a stable tag its `version` job also requires the tagged
commit to be an ancestor of `origin/main` (as MarkdownViewr.Windows does), so an off-main stable tag stops before
any gate or signing. It then runs every existing gate (Windows build/test/package, security, CodeQL, scripts).
Release-candidate tags skip the ancestry check and stay unsigned. For a stable tag the `sign` job then runs in the protected `release` environment (tags `v*` only) with
OIDC and no stored secret:

1. `build/Assert-ReleaseSigning.ps1` checks the protected variables below (configuration only).
2. Locked restore, `Test-WindowsSdkInventory -VerifyPackages`, `Test-BrandedInstaller`, `Test-PackagingInputs`.
3. `Build-Installer.ps1 -PublishOnly` publishes and checks the service, companion and CLI payloads (versions,
   file versions, WinUI resources, SDK inventory) without building the MSI.
4. `Sign-Release.ps1 -Operation Stage` copies only the owned files into a staging folder: the three executables
   plus `Qqq.PrintAgent*.dll` and `KofTwentyTwo.AppKit*.dll` beside them. Third-party binaries keep their own
   signatures.
5. `azure/login`, then `azure/artifact-signing-action` signs the stage (SHA256, RFC3161 timestamp).
6. `Sign-Release.ps1 -Operation Apply` accepts a staged file only if it changed, passes `signtool verify /pa /all /v`
   and `Get-AuthenticodeSignature` (valid, timestamped) and has exactly `ARTIFACT_SIGNING_SUBJECT`. The signtool
   output must show exactly one verified signature, zero warnings and errors, and every `Hash of file (...)` line
   must be `sha256` (certificate-chain text naming an algorithm is not counted). Only then are the files copied
   back, and the payload is verified again in place.
7. `Build-Installer.ps1 -SkipPublish` builds the MSI from the signed payload (all payload checks run again).
8. The MSI is staged, signed by the same action, and accepted by the same publisher check.
9. `Package-Release.ps1 -AssembleOnly -ExpectedPublisherSubject` re-verifies the MSI and writes the four release
   files (versioned MSI, `Install-Branded.ps1`, `branding.example.json`, `SHA256SUMS`). Checksums are computed after
   signing.

The `publish` job requires every gate and, for a stable tag, a successful `sign` job. It then verifies the
checksums, attests the artifacts and runs `gh release create --verify-tag --latest`, which cannot overwrite an
existing release. An unsigned prerelease path is unchanged.

## Required protected-environment variables (non-secret)

Set in the GitHub `release` environment of this repository (see the MarkdownViewr runbook for the procedure):

| Variable | Value |
| --- | --- |
| `AZURE_TENANT_ID` | Tenant GUID (MarkdownViewr reference: `4ebeb0ad-5782-487a-a1b4-5080255774cf`) |
| `AZURE_SUBSCRIPTION_ID` | Signing resource's subscription (reference: `8ef2479e-3346-4cba-93ad-101c6cd237a0`) |
| `AZURE_CLIENT_ID` | A client GUID with trust for **this** repository (below); never copy another repository's |
| `ARTIFACT_SIGNING_ENDPOINT` | Regional endpoint (reference: `https://eus.codesigning.azure.net/`) |
| `ARTIFACT_SIGNING_ACCOUNT` | Account (reference: `kof22signing`) |
| `ARTIFACT_SIGNING_PROFILE` | Active PublicTrust profile (reference: `releases`) |
| `ARTIFACT_SIGNING_SUBJECT` | Full certificate subject (reference: `CN=James Maes, O=James Maes, L=Chester, S=il, C=US`) |

The reference values are copied from the MarkdownViewr runbook; read the deployed resource back before use.

## Identity binding for this repository

The CI identity must be an approved single-tenant Entra application and service principal with **no client secret**,
a federated credential for the exact release-environment subject, and the `Artifact Signing Certificate Profile Signer`
role scoped to the profile only:

```json
{
  "name": "qqq-print-agent-release",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:QRun-IO@<owner-id>/qqq-print-agent@<repo-id>:environment:release",
  "audiences": ["api://AzureADTokenExchange"]
}
```

Read the immutable owner and repository IDs from `gh api repos/QRun-IO/qqq-print-agent/actions/oidc/customization/sub`
(and confirm `use_immutable_subject`). Trust must not be broadened to pull requests or other environments. MarkdownViewr's
and gclo's identities must not be reused for this repository without separate approval and their own trust.

## Configuration status and limits

- Azure setup is configured: existing account `kof22signing`, profile `releases` (Active, PublicTrust), a
  printing-only client `083a3874-ebbb-453f-b625-71bbfefd3227` with the exact immutable subject
  `repo:QRun-IO@93010898/qqq-print-agent@1438820325:environment:release`, a profile-scoped signer role and the
  seven environment variables, with no secret. The first hosted stable run (`v1.0.0`) passed every build, test,
  security, CodeQL and script gate and the Azure OIDC login (so the CI identity exchange works), then stopped before
  signing: the action's SignCLI installer uses `dotnet tool install --add-source`, which the repository's NuGet
  package source mapping rejects. No signed installer was published. The `sign` job now preinstalls only the
  pinned SignCLI (`0.9.1-beta.26227.3`) through the mapped `nuget.config` into
  `%LOCALAPPDATA%\ArtifactSigning\sign\sign.<version>` (the layout the action expects, checked for
  `tools\net8.0\any\sign.dll`) before the first signing step. Signing itself in CI is **not yet proven**; a local
  administrator proof cannot show it, only the next hosted stable run (1.0.1) will.
- The `azure/artifact-signing-action` inputs used (`endpoint`, `signing-account-name`, `certificate-profile-name`,
  `files-folder`, `files-folder-filter`, `files-folder-recurse`, `file-digest`, `timestamp-rfc3161`, `timestamp-digest`)
  were checked against the `action.yml` of the pinned commit (`c7ab2a86…`, v2.0.0) supplied by root. The action
  filters by extension, which is why only owned files are staged, and it authenticates through the Azure CLI
  credential left by `azure/login` (the Azure CLI credential type is not excluded by default).
- Proof so far (local, existing Azure client): the signing code and configuration are implemented, and the source head
  `53fe1b8281806eda7adc314f92c8205aefcb51ce` passed clean hosted Windows CI run 41442796961 (499 tests, zero failures and
  skips; build and MSI zero warnings and errors). All 19 first-party binary copies of an installed, published RC4 payload,
  including `Cli/qqq-print-agent.dll`, were Azure-signed and each passed the strict `Sign-Release.ps1 -Operation Verify`
  (exact publisher subject, SHA256, timestamp, one signature, zero warnings and errors); the source payload was
  untouched. A disposable copy of the official RC4 MSI was also Azure-signed and passed `Apply`/`Verify`. This is a
  local administrator proof of the signer and the verification path. It does **not** prove the CI identity's OIDC
  exchange, nor signed payload files inside the old MSI; that is shown only by the first hosted stable-tag run.
  Installed and published remains RC4; no new stable tag exists.
- `Install-Branded.ps1` and `branding.example.json` are not Authenticode-signed; their integrity rests on the
  checksums in `SHA256SUMS` and the release provenance attestation.
- A published QQQ core with native session login is still awaited; the last official release, RC20 (API 1.8), does
  not have it, and a stable tag waits for it. The Windows release branch (Gitea PR 2) awaits a normal protected merge
  after the central native 1725 repair for Windows ACL and file sharing on Linux.
- `Apply` requires the signed stage to contain exactly the staged files, and fails closed otherwise.
- The subject comparison is an exact string match against the signer certificate; a formatting difference (for
  example `S=` versus `ST=`) fails the release rather than weakening the check.
- The current source version is 1.0.1 (the `v1.0.0` tag is unchanged and published nothing signed). Signing proves
  publisher identity, not product qualification; it is not production hardware or server acceptance.
