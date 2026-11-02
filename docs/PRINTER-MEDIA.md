# Windows queue-default media profiles

The candidate client advertises `printer-media-v1` only when standard discovery
reports a stable printing extension version of at least 2.3.0 within major two.
Printing 2.0, 2.1 and 2.2 (rendering, no media negotiation) servers retain the original three realtime capabilities and
legacy unselected jobs. Each WebSocket establishment refreshes standard discovery
once under the authenticated session gate, then uses that refreshed snapshot
for HELLO. This handles same-session server upgrades and downgrades without an
HTTP polling schedule. An unverified test or
custom transport does not claim media support.

The client includes optional `mediaProfiles` in exposed-printer inventory. It reports at most one profile per
queue: the machine-wide Windows driver default. It does not enumerate stock that
an operator might have available, infer dimensions from document pages, or claim
that the configured stock is physically loaded.

Discovery reads `GetPrinter` level 8, rather than the current user's level 9
preferences. Custom paper dimensions come from both valid DEVMODE width/length
fields, converted from tenths of a millimeter to integer microns. Otherwise, a
single matching paper ID in the driver's `DeviceCapabilities` paper/size arrays
is required. Landscape defaults swap the effective page axes. Positive explicit
DEVMODE resolutions are retained; symbolic quality values require resolution
from a driver DC created with that exact machine DEVMODE. Unknown, incomplete,
ambiguous or out-of-contract values produce no profile, without guessing.

Each profile has a stable opaque queue/default ID, a lowercase SHA-256 revision,
a display name, width/height in microns, horizontal/vertical DPI,
`source: WINDOWS_QUEUE_DEFAULT` and `applyMode: DRIVER_DEFAULT`. The revision
includes driver identity, resolved dimensions/resolution, and the complete
returned public/private DEVMODE bytes. This is deliberately conservative: even a
driver-private setting change invalidates an old selection. Drivers returning
volatile private bytes may require reselection; the client does not guess which
vendor fields can be ignored. The fingerprint is not a credential.

Consumers submit `options.mediaProfile: {id, revision}` exactly as advertised.
It cannot be combined with `paperWidthMm` or `paperHeightMm`. The worker checks
the current selection after download and before its submission state. The
native helper checks again immediately before opening a spool document. A known
media refusal is propagated across the helper boundary and recorded as FAILED
with no document submitted; arbitrary native failures and timeouts retain their
existing uncertain-outcome handling and are not retried automatically.

For selected PDF jobs, the captured machine DEVMODE remains the selection
fingerprint. A separate job-only copy requests one driver copy and 100% driver
scale, only for fields supported by the captured defaults, through
`DocumentProperties(DM_IN_BUFFER | DM_OUT_BUFFER)`. Unsupported fields remain
absent; drivers without native scale support use the software renderer's scale.
This allows
the driver to reconcile its private settings without prompting or changing
queue defaults. Software remains responsible for requested copies and
ACTUAL_SIZE/FIT rendering. The normalized result must confirm these overrides
and preserve the original support flags and selected geometry/DPI; otherwise
submission is refused. A previously supported override cannot silently disappear.
`CreateDC` receives those normalized bytes, never a null/current-user default.
Before `StartDoc`, the effective DC must also report matching physical page
dimensions and exact horizontal/vertical DPI. Dimension tolerance is one device
pixel plus 50 microns (half the DEVMODE 0.1 mm form unit), including landscape
axes. Unknown or substituted dimensions/DPI are refused before spooling.
RAW ZPL has no DEVMODE application mechanism: the helper rechecks the current
profile immediately before `StartDocPrinter`, but cannot lock loaded stock or
prevent a physical/device-setting change afterward. `DRIVER_DEFAULT` never
changes queue settings, calibrates hardware or loads stock. Such changes require
operator verification and fresh discovery. Legacy jobs without a media selection
retain their existing path.

The candidate's actual read-only probe reported the configured label default as
101,600 × 152,400 microns at 300 × 300 DPI, with an identical fingerprint across
three separate helper launches. Synthetic tests also cover 101,600 × 25,400 and
101,600 × 50,800 microns; these are not advertised as current configuration.
Physical queue names, driver selectors and inventory evidence remain private.
No print, installation, queue change or service operation was performed for this
feature's discovery qualification. End-to-end server acceptance and physical
printing of a profile-selected job remain separate gates.

A subsequent read-only probe of the compiled corrected adapter successfully
normalized the configured label driver's supported copy field while retaining
its absent native scale field. The resulting DC reported 1,200 × 1,800 physical
pixels at 300 × 300 DPI. It invoked no StartDoc and changed no queue defaults.
All 24 focused adapter regression cases pass; this does not resolve the separate
Core test-assembly quarantine or qualify a physical profile-selected print.

Native references: [machine-wide PRINTER_INFO_8 defaults](https://learn.microsoft.com/en-us/windows/win32/printdocs/printer-info-8),
[DEVMODE dimensions and resolution](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-devmodew),
and [driver paper capabilities](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-devicecapabilitiesw).
Job-only driver normalization follows [DocumentProperties](https://learn.microsoft.com/en-us/windows/win32/printdocs/documentproperties);
effective page checks use [GetDeviceCaps](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdevicecaps).
