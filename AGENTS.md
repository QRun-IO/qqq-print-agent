# Repository guidance

This is QRun-IO/qqq-print-agent, built on KofTwentyTwo AppKit's Windows service variant. Follow the current user request and repository .editorconfig/central package versions.

- Gitea is primary for ordinary branches and pull requests. GitHub is the source shadow, with the authorized existing release/signing and CI/security workflows retained; the installed updater requires GitHub release assets. Do not remove that bridge as part of a source-host migration.
- Verify with `./build/Verify.ps1`; package with `./build/Build-Installer.ps1` (the version comes from `VersionPrefix` in `Directory.Build.props`; add `-ReleaseVersion X.Y.Z-rc.N` for a test build).
- Core owns protocol/state/policy; Windows owns OS adapters/storage/runtime; Service owns hosting; UI owns native views/tray plumbing.
- Zero compiler/analyzer warnings; locked dependencies. Line coverage is gated per assembly by `build/Verify.ps1` (docs/adr/0003-version-2.md); raise a minimum in `build/minimum-coverage.json` with the tests that earn it and never lower one.
- Keep the service alive independently of the companion. Use AppKit update UX with the machine MSI adapter; update service, companion and CLI together. Do not install a per-user-only replacement over the machine package.
- Never log or publish credentials, enrollment codes, print documents or local journals. Update docs/security/threat-model.md for boundary changes.
- Do not physically print without the user's authorization. Describe spool acceptance separately from confirmed paper output.
- Preserve unrelated changes and attribution. Do not commit/push/release without authorization.
