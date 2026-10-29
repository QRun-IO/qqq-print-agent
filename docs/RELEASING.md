# Release process

## Prereleases

The `prerelease` workflow runs on tags matching `vX.Y.Z-rc.N`, or can be dispatched against an existing matching tag. It validates the tag, invokes the same Windows CI build/test/package workflow, downloads that run's installer artifact and creates a GitHub prerelease containing:

- `QqqPrintAgent-vX.Y.Z-rc.N-x64.msi`
- `SHA256SUMS`

The workflow has read-only permissions except its final publication job, which receives `contents: write`. Action/reusable-workflow dependencies are pinned by commit. The protocol fixture checkout is pinned to a reviewed commit. A failed verification prevents publication.

The prerelease is explicitly unsigned. No signing secret, production release, winget submission, attestation or automatic desktop update is implied. The final job refuses to overwrite an existing GitHub release; use a new prerelease tag for changed binaries.

## Maintainer checklist

1. Update CHANGELOG.md, acceptance evidence and version inputs. Ensure no local enrollment/journal/payload files are tracked.
2. Review the protocol pin, dependency lock files, unsigned-release notice and open limitations.
3. Run `./build/Verify.ps1` and build the MSI locally or inspect the corresponding green CI run.
4. Obtain explicit release authorization, create/push the immutable prerelease tag, and wait for the workflow.
5. Verify the published filenames/checksum and record the workflow URL and hardware acceptance.

If publishing the initial release manually, attach only reviewed artifacts and disclose whether they were built locally. Do not claim GitHub build provenance for a local MSI.

## Production signing follow-up

`Build-Installer.ps1` accepts a certificate thumbprint and timestamp server for local signing. Production automation must integrate the organization's approved signing service and sign/verify first-party executables and the final MSI. That setup is not part of this prerelease. Upgrade/reboot/service-account qualification and broader hardware acceptance are also required before calling it a production release.
