# Microsoft Windows SDK terms

The project's MIT license applies to this project's code. Microsoft Windows
SDK packages retain their separate terms; they are not relicensed as MIT.
The maintainer remains responsible for satisfying applicable redistribution
and end-user protection obligations. Copying notices and passing these checks does not
certify that those obligations have been fulfilled or create a customer EULA.

The exact inventory is recorded in
[`licenses/windows-sdk-packages.json`](../licenses/windows-sdk-packages.json),
including each package's NuGet content hash, signed archive SHA-256, separate
license and notice files, and their original byte hashes. This is the existing
SDK dependency set, not permission to add other packages from these families.

| Package | Exact version | Role |
| --- | --- | --- |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | Runtime SDK dependency |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Build only |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | Build only |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | Reference pack and Windows runtime projections |
| Microsoft.WindowsAppSDK | 2.5.1 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.AI | 2.5.5 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.Foundation | 2.3.12 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.ML | 2.1.94 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.Runtime | 2.5.1 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.Search | 2.5.5 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | Runtime SDK dependency |
| Microsoft.WindowsAppSDK.WinUI | 2.3.9 | Runtime SDK dependency |

The [Windows App SDK package license](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1/License)
and accompanying notice are preserved byte-for-byte. The five distinct license
texts include both ML packages' separate terms, the MSIX SDK license, and the
Windows SDK RTF. Five distinct vendor notices are also retained. Identical files
shared by multiple packages are mapped to one copy in the inventory manifest;
provider-specific terms are not replaced by the common App SDK license.

The application and native test host copy the complete `licenses/` tree into
`ThirdPartyNotices/`, alongside the project's separate root `LICENSE` and
`NOTICE` files in their output directory. Git preserves the vendor bytes without
line-ending conversion.

The [BuildTools package](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.28000.2705)
identifies [Microsoft's Windows SDK terms](https://aka.ms/WinSDKLicenseURL).
Both BuildTools packages remain build-only dependencies. Intentional
redistribution of any SDK tool would require a separate scope and terms review.
SDK.NET.Ref is resolved implicitly by the .NET SDK, rather than appearing in
source package locks. Its three pinned `lib/net8.0` runtime projection DLLs may
enter a payload; that does not claim all three are shipped. Its other package
files are excluded, apart from retained legal text.

`build/Test-WindowsSdkInventory.ps1` checks central versions, every selected
SDK occurrence and NuGet content hash in every source lockfile, all 15 exact
package archive hashes and cache content hashes, and all ten retained legal
files. Restored application assets must contain all 14 locked SDK packages and
the exact implicit reference-pack download. Other available project assets are
also checked for SDK version and hash changes. NuGet's normalized content hash
is distinct from the signed archive's raw SHA-512 and SHA-256.

Required CI runs the guard after locked restore. Packaging also verifies the
actual App notices and manifest, then rejects identical build/reference package
content, including renamed tools, anywhere in the three-process payload before
creating the MSI. Only the exact reference-pack runtime projection bytes and
legal text are exempt from this payload exclusion. This check does not grant a
license exemption.

The maintainer accepted this exact 15-package scope on 2026-11-01.
[EX-0014](https://github.com/KofTwentyTwo/standards/blob/fbfc8d2cf3b5e16ae9750b04cf9de1794f1e7152/exceptions/register.md#ex-0014)
is the approved record, merged through the normal protected workflow in
[standards PR 56](https://github.com/KofTwentyTwo/standards/pull/56). The caller
allows only these 15 package names through the automated license check, plus the
one separately approved EX-0015 development-only `pkg:npm/uri-js` entry. The SDK
guard accepts exactly that finite union; any other npm or other-ecosystem purl, or
a versioned or duplicate entry, is rejected, and `check-script-quality.py` pins the
uri-js version, lock integrity and license bytes.

EX-0014 is limited to these exact package/version/artifact tuples through
2027-04-30 UTC inclusive. The required CI and pre-MSI guard rejects use from
2027-05-01 UTC, any change to the pinned inventory manifest, and any expansion
of the finite package-name allowance. The dependency-review action does not
enforce PURL versions, so the separate version, content-hash and archive-hash
checks remain mandatory. A changed version, license or payload scope requires
renewed approval. AppKit receives no exception. All other dependency license,
vulnerability, provenance and release gates remain in force.
