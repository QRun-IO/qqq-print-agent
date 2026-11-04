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

## Published stable result — 2026-11-03

The authorized GitHub signing bridge completed [release run 41532214447](https://github.com/QRun-IO/qqq-print-agent/actions/runs/41532214447) for `v1.0.1`, source `5bf00f3291923f7afb34c6cce61276e69649689b`. Its build, test, security, CodeQL, script, OIDC signing and publication gates passed. The signed MSI and matching assets are published on [Gitea](https://git.kof22.com/QRun-IO/qqq-print-agent/releases/tag/v1.0.1) and [GitHub](https://github.com/QRun-IO/qqq-print-agent/releases/tag/v1.0.1); the latter remains the installed updater feed.

The downloaded MSI's checksum matched `SHA256SUMS`; Authenticode reported **Valid**, the exact configured publisher `CN=James Maes, O=James Maes, L=Chester, S=il, C=US`, and a timestamp. Release provenance was verified. The local installation on November 3 exited 0 with companion, service and CLI all reporting `1.0.1+5bf00f3291923f7afb34c6cce61276e69649689b`, file version `1.0.1.65535`, and service state **Running**. See [release readiness](RELEASE-READINESS.md).

The printing-only Azure client uses the exact immutable repository/environment subject, a profile-scoped signer role and protected environment variables, with no stored client secret. The same signing account/profile and verification rules described above remain in use.

## Historical bootstrap failure and limits

The first hosted stable attempt, `v1.0.0`, passed its prerequisite gates and Azure OIDC login but stopped before signing because the action's SignCLI installer conflicted with NuGet package source mapping. No signed 1.0.0 installer was published. Version 1.0.1 fixes the bootstrap by preinstalling only pinned SignCLI `0.9.1-beta.26227.3` through the mapped `nuget.config` into the action's expected `%LOCALAPPDATA%\ArtifactSigning\sign\sign.<version>` layout. The 1.0.0 tag is unchanged.

The earlier local signing proof used copies of the RC4 payload and MSI; it was not the hosted release. The successful 1.0.1 run now supplies the CI identity/signing evidence. QQQ 4.1.0-RC.21 and qbit-printing 1.0.0 provide the released native-authentication cohort; the older RC20 prerequisite is superseded.

`Apply` still requires exactly the staged files, and publisher matching remains an exact string comparison. `Install-Branded.ps1` and `branding.example.json` are not Authenticode-signed; their integrity rests on release checksums and provenance. Signing establishes publisher identity, not physical printer compatibility or universal deployment qualification.
