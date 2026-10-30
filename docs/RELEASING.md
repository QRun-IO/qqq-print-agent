# Release process

## Prereleases

The `prerelease` workflow runs on tags matching `vX.Y.Z-rc.N`, or can be dispatched against an existing matching tag. It validates the tag, invokes the same Windows CI build/test/package workflow, downloads that run's installer artifact and creates a GitHub prerelease containing:

- `QqqPrintAgent-vX.Y.Z-rc.N-x64.msi`
- `SHA256SUMS`

The workflow has read-only permissions except its final publication job, which receives `contents: write`. Action/reusable-workflow dependencies are pinned by commit. The protocol fixture checkout is pinned to a reviewed commit. A failed verification prevents publication.

The prerelease is explicitly unsigned. No signing secret, production release, winget submission, attestation or completed in-app upgrade qualification is implied. The final job refuses to overwrite an existing GitHub release; use a new prerelease tag for changed binaries.

## Maintainer checklist

1. Update CHANGELOG.md, acceptance evidence and version inputs. Ensure no local enrollment/journal/payload files are tracked.
2. Review the protocol pin, dependency lock files, unsigned-release notice and open limitations.
3. Run `./build/Verify.ps1` and build the MSI locally or inspect the corresponding green CI run.
4. Obtain explicit release authorization, create/push the immutable prerelease tag, and wait for the workflow.
5. Verify the published filenames/checksum and record the workflow URL and hardware acceptance.

If publishing the initial release manually, attach only reviewed artifacts and disclose whether they were built locally. Do not claim GitHub build provenance for a local MSI.

## Production signing follow-up

`Build-Installer.ps1` accepts a certificate thumbprint and timestamp server for local signing. Production automation must integrate the organization's approved signing service and sign/verify first-party executables and the final MSI. That setup is not part of this prerelease. Upgrade/reboot/service-account qualification and broader hardware acceptance are also required before calling it a production release.

## Update-feed contract

The 0.1.3-rc.1 companion uses AppKit update prompts with a machine MSI adapter. Release assets are also updater inputs: preserve the exact versioned MSI filename and matching `SHA256SUMS` entry. The fixed feed is this repository's GitHub releases; branding and QQQ configuration cannot redirect it. Stable installations ignore prereleases, while RC installations may receive a newer RC or stable version. The minimum offered core version is 0.1.3. Never replace a published release's binary under the same version.

MSI has a numeric three-part ProductVersion; informational version preserves `-rc.N`. Same-numeric-version MSI upgrades are enabled for RC-to-RC/stable transitions, while the updater enforces a strictly newer semantic release. This intentional authoring requires a narrow ICE61 exception alongside the existing Microsoft-resource ICE03 exception; no other MSI checks are disabled. Manual MSI deployments must follow semantic release order because MSI alone cannot order RC suffixes. See [ADR-0002](adr/0002-prerelease-quality-and-packaging.md). The helper verifies the installed companion's actual ProductVersion against the requested release after exit 0. Exit 3010 remains restart-required.

Same-feed SHA-256 establishes asset consistency, not independent publisher authentication. Signed first-party binaries/MSI and signing automation remain separate work. The helper requires administrator rights, installs service/UI/CLI together and retains credentials/journal; no per-user updater or privileged persistent daemon is shipped. Completed staging folders and install logs are retained for administrator review.

The 0.1.3 source gate passed 163 tests with zero warnings/errors and locked restore. Before claiming update acceptance, record a real installed upgrade, version coherence, retained state, cancellation/failure handling, companion relaunch and reboot-required behavior. Current installed UI and actual in-app upgrade acceptance are pending; UI test compilation and packaging alone are insufficient.
