# Repository guidance

This is QRun-IO/qqq-print-agent, built on KofTwentyTwo AppKit's Windows service variant. Follow the current user request and repository .editorconfig/central package versions.

- Verify with `./build/Verify.ps1`; package with `./build/Build-Installer.ps1 -ProductVersion 0.1.1 -ReleaseVersion 0.1.1-rc.1`.
- Core owns protocol/state/policy; Windows owns OS adapters/storage/runtime; Service owns hosting; UI owns native views/tray plumbing.
- Zero compiler/analyzer warnings; locked dependencies. Coverage is reported for the initial prerelease under docs/adr/0002-prerelease-quality-and-packaging.md.
- Keep the service alive independently of the companion. No per-user updater in the machine service package.
- Never log or publish credentials, enrollment codes, print documents or local journals. Update docs/security/threat-model.md for boundary changes.
- Do not physically print without the user's authorization. Describe spool acceptance separately from confirmed paper output.
- Preserve unrelated changes and attribution. Do not commit/push/release without authorization.
