# Release process

## Prereleases

The [`prerelease` workflow](../.github/workflows/release.yml) runs on tags matching `vX.Y.Z-rc.N`, or can be dispatched against an existing matching tag. It validates the tag and requires the Windows build/test/package, security, CodeQL and PowerShell script gates. It then downloads that run's installer artifact, verifies every packaged checksum, attests the artifacts and creates a GitHub prerelease containing:

- `QqqPrintAgent-vX.Y.Z-rc.N-x64.msi`
- `SHA256SUMS`
- `Install-Branded.ps1`
- `branding.example.json`

Source contents are read-only outside the publication job. That final job receives release-write, OIDC and attestation permissions; security analysis receives only the permissions needed to report its results. Action/reusable-workflow dependencies are pinned by commit. The protocol fixture checkout is pinned to a reviewed commit. Any failed prerequisite or checksum/attestation step prevents publication.

The MSI remains explicitly unsigned. GitHub provenance identifies the build workflow and artifact digest; it is not Authenticode publisher signing or production qualification. Verify attestations for the exact published files using `gh attestation verify <asset-path> --repo QRun-IO/qqq-print-agent`, and record the result. Older releases may lack attestations; the current workflow does not retroactively attest them. No signing secret, winget submission or completed in-app upgrade qualification is implied. The final job refuses to overwrite an existing GitHub release; use a new prerelease tag for changed binaries.

## Maintainer checklist

1. Update CHANGELOG.md, acceptance evidence and version inputs. Ensure no local enrollment/journal/payload files are tracked.
2. Review the protocol pin, dependency lock files, unsigned-release notice and open limitations.
3. Run `./build/Verify.ps1` and build the MSI locally or inspect the corresponding green CI run.
4. Obtain explicit release authorization, create/push the immutable prerelease tag, and wait for the workflow.
5. Verify every published filename/checksum and its provenance attestation. Record the workflow URL and version-specific installed/hardware acceptance, distinguishing observed results from pending qualification.

If publishing the initial release manually, attach only reviewed artifacts and disclose whether they were built locally. Do not claim GitHub build provenance for a local MSI.

## Production signing follow-up

`Build-Installer.ps1` accepts a certificate thumbprint and timestamp server for local signing. Production automation must integrate the organization's approved signing service and sign/verify first-party executables and the final MSI. That setup is not part of this prerelease. Upgrade/reboot/service-account qualification and broader hardware acceptance are also required before calling it a production release.

## Update-feed contract

The companion uses AppKit update prompts with a machine MSI adapter. Release assets are also updater inputs: preserve the exact versioned MSI filename and matching `SHA256SUMS` entry. The updater checks release identity, size and SHA-256; provenance verification is a separate release/operator check. The fixed feed is this repository's GitHub releases; branding and QQQ configuration cannot redirect it. Stable installations ignore prereleases, while RC installations may receive a newer RC or stable version. The minimum offered core version is 0.1.3. Never replace a published release's binary under the same version.

MSI has a numeric three-part ProductVersion; informational version preserves `-rc.N`. Same-numeric-version MSI upgrades are enabled for RC-to-RC/stable transitions, while the updater enforces a strictly newer semantic release. This intentional authoring requires a narrow ICE61 exception alongside the existing Microsoft-resource ICE03 exception; no other MSI checks are disabled. Manual MSI deployments must follow semantic release order because MSI alone cannot order RC suffixes. See [ADR-0002](adr/0002-prerelease-quality-and-packaging.md). The helper verifies the installed companion's actual ProductVersion against the requested release after exit 0. Exit 3010 remains restart-required.

Same-feed SHA-256 establishes asset consistency, not independent publisher authentication. Signed first-party binaries/MSI and signing automation remain separate work. The helper requires administrator rights, installs service/UI/CLI together and retains credentials/journal; no per-user updater or privileged persistent daemon is shipped. Completed staging folders and install logs are retained for administrator review.

The 0.1.3 source gate passed 163 tests with zero warnings/errors and locked restore. Before claiming update acceptance, record a real installed upgrade, version coherence, retained state, cancellation/failure handling, companion relaunch and reboot-required behavior. Current installed UI and actual in-app upgrade acceptance are pending; UI test compilation and packaging alone are insufficient.
