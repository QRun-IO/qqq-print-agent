# Contributing

QQQ Print Agent is developed in the [QRun-IO organization on Gitea](https://git.kof22.com/QRun-IO). It uses the KofTwentyTwo Windows AppKit service architecture. Contributions are accepted under the [MIT license](LICENSE); preserve existing attribution.

## Set up

Use Windows x64, Git and the .NET 10 SDK selected by `global.json`. Check out `qqq-print-agent` and `qqq-print-protocol` into sibling directories. Check out the protocol commit pinned in `.github/workflows/ci.yml` (the `ref` of its "Checkout pinned protocol fixtures" step); its JSON fixtures are linked into the contract tests. To leave an existing protocol checkout where it is, export that commit's `fixtures` folder elsewhere and pass it with `./build/Verify.ps1 -ProtocolFixtures <dir>`.

```powershell
./build/Verify.ps1
./build/Build-Installer.ps1
```

Verification uses locked NuGet restore, a warnings-as-errors solution build, core tests, Windows/CLI tests and a coverage gate. Packaging creates a self-contained x64 MSI. Neither command installs the service or prints a document.

## Changes and review

Publish ordinary branches and pull requests to the [primary Gitea repository](https://git.kof22.com/QRun-IO/qqq-print-agent). GitHub is a source shadow; the existing authorized release/signing workflow and CI/security workflows remain there. GitHub release assets also serve the installed updater. See [Releasing](docs/RELEASING.md) before changing either distribution path.

- Branch from `main`, use Conventional Commit titles, and include a DCO sign-off (`git commit -s`). Sign commits where your account supports signing.
- Keep business/protocol logic in Core, Windows integration in Windows, hosting in Service, and view/tray plumbing in the AppKit companion.
- Use central versions in `Directory.Packages.props`; regenerate affected `packages.lock.json` files when dependencies change.
- Follow `.editorconfig`: three-space indentation, Allman braces and the repository's C# analyzers. Builds must have zero warnings.
- Add tests for behavior and failure boundaries. Run the protocol fixtures when changing the wire contract.
- Update user/admin docs for visible changes, and the threat model when changing privileges, network interfaces or credential handling.
- Do not include secrets, document contents, customer labels or local journals in issues, fixtures or commits.
- Disclose substantial AI assistance in the PR and include a model-naming Co-Authored-By trailer when appropriate.

Line coverage is gated per assembly ([ADR-0003](docs/adr/0003-version-2.md)). `build/Verify.ps1` merges the suites' reports and fails when an assembly is below its minimum in `build/minimum-coverage.json`. The minimums are a floor that only moves up: when a change adds tests, raise the entry in the same change to the measured percentage rounded down, less one point (a few timing-dependent lines differ between runs); never lower one to make a change pass. An assembly that has reached its release bar is recorded at the bar, with no point of slack: that is why the CLI stands at 100. The release bar is 100% for Core and the CLI and 90% for the Windows layer, so for an assembly still below its bar a green gate means "within a point of the recorded baseline", not "fully covered". Code a test cannot reach (native calls, process hooks) goes in a thin adapter marked `[ExcludeFromCodeCoverage(Justification = "...")]`, with the decisions around it kept in covered code. Elevated desktop tests are separate from hosted CI because they need an interactive administrator session.

## UI and hardware tests

Build the app first. In an elevated interactive Windows session, set `K22_UITEST_EXE` to the companion executable and run the UI test project. Hardware testing must use a deliberately selected printer and recognizable test document. Coordinate physical printing with the machine owner; CI never prints.

A passing unit suite does not establish printer compatibility. Record Windows version, driver/model, format, paper/label size, and whether the output was physically inspected. Report spool acceptance separately from physical completion.

## Issues and releases

Open ordinary bugs or proposals in [Issues](https://git.kof22.com/QRun-IO/qqq-print-agent/issues). Use [private security reporting](SECURITY.md) for vulnerabilities. Release authorization and tagging belong to maintainers; see [RELEASING.md](docs/RELEASING.md).

## Script quality

Owned scripts follow the KofTwentyTwo standard at revision `fbfc8d2cf3b5e16ae9750b04cf9de1794f1e7152` (configs adapted from its `python` and `typescript` profiles): three-space indentation, Ruff 0.17.3 format/lint (locked in `uv.lock`) and strict Pyright 1.1.414 (Microsoft's npm package, locked in `package-lock.json` and run through Node) for `.github/scripts/*.py`, and ESLint 10 with Stylistic (Allman) plus strict `tsc` over JSDoc for `tools/**/*.mjs` and `eslint.config.mjs`. Restore with `uv sync --locked` and `npm ci --ignore-scripts`, then run:

```text
uv run --locked --no-sync python .github/scripts/test-script-quality.py
uv run --locked --no-sync python .github/scripts/check-script-quality.py
npm run typecheck
npm run lint
npm test
```

`check-script-quality.py` fails for any tracked Python outside `.github/scripts/` or JavaScript outside the `tsconfig.json` include list, and requires Pyright's analyzed-file count to equal the selected scripts. Pyright must run as `pyright -p .github`: it silently skips dot directories from a root config. Documented exceptions: CLI `print`/`console.log` only in `check-*.py` and `tools/`; synthetic test assertions and boundary constants; narrow inline `noqa` comments for fixed no-shell Git/interpreter calls and ordered fail-closed validators. Do not loosen a rule or add a type waiver to pass. `package.json` overrides `minimatch` to 11.0.1, the patched release for [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249) (affected before 11.0.1); keep it until ESLint's own dependency range includes a patched release. The only npm license exception, [EX-0015](https://github.com/KofTwentyTwo/standards/blob/main/exceptions/register.md#ex-0015) for development-only `uri-js` 4.4.1, is enforced by `check-script-quality.py` (version, lock integrity, installed license bytes and the exact `pkg:npm/uri-js` caller entry); changing that package or the entry needs renewed approval. The virtual agent's existing QRun.IO attribution is unchanged.
