# Contributing

QQQ Print Agent is developed in the [QRun-IO organization](https://github.com/QRun-IO). It uses the KofTwentyTwo Windows AppKit service architecture. Contributions are accepted under the [MIT license](LICENSE); preserve existing attribution.

## Set up

Use Windows x64, Git and the .NET 10 SDK selected by `global.json`. Check out `qqq-print-agent` and `qqq-print-protocol` into sibling directories. Check out the protocol commit pinned in `.github/workflows/ci.yml`; its JSON fixtures are linked into the contract tests.

```powershell
./build/Verify.ps1
./build/Build-Installer.ps1 -ProductVersion 0.1.0 -ReleaseVersion 0.1.0-rc.1
```

Verification uses locked NuGet restore, a warnings-as-errors solution build, core tests, Windows/CLI tests and a coverage report. Packaging creates a self-contained x64 MSI. Neither command installs the service or prints a document.

## Changes and review

- Branch from `main`, use Conventional Commit titles, and include a DCO sign-off (`git commit -s`). Sign commits where your account supports signing.
- Keep business/protocol logic in Core, Windows integration in Windows, hosting in Service, and view/tray plumbing in the AppKit companion.
- Use central versions in `Directory.Packages.props`; regenerate affected `packages.lock.json` files when dependencies change.
- Follow `.editorconfig`: three-space indentation, Allman braces and the repository's C# analyzers. Builds must have zero warnings.
- Add tests for behavior and failure boundaries. Run the protocol fixtures when changing the wire contract.
- Update user/admin docs for visible changes, and the threat model when changing privileges, network interfaces or credential handling.
- Do not include secrets, document contents, customer labels or local journals in issues, fixtures or commits.
- Disclose substantial AI assistance in the PR and include a model-naming Co-Authored-By trailer when appropriate.

For the initial prerelease, coverage is reported rather than forced to the template's unattained 100% target. This is an explicit temporary exception documented in [ADR-0002](docs/adr/0002-prerelease-quality-and-packaging.md), not a claim of full coverage. Elevated desktop tests are separate from hosted CI because they need an interactive administrator session.

## UI and hardware tests

Build the app first. In an elevated interactive Windows session, set `K22_UITEST_EXE` to the companion executable and run the UI test project. Hardware testing must use a deliberately selected printer and recognizable test document. Coordinate physical printing with the machine owner; CI never prints.

A passing unit suite does not establish printer compatibility. Record Windows version, driver/model, format, paper/label size, and whether the output was physically inspected. Report spool acceptance separately from physical completion.

## Issues and releases

Open ordinary bugs or proposals in [Issues](https://github.com/QRun-IO/qqq-print-agent/issues). Use [private security reporting](SECURITY.md) for vulnerabilities. Release authorization and tagging belong to maintainers; see [RELEASING.md](docs/RELEASING.md).
