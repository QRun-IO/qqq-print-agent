# ADR-0002: Initial prerelease quality and packaging gates

Status: accepted for the owner-authorized happy-path prerelease. Date: 2026-10-29.

The original template mandates 100% core/CLI coverage and automated desktop acceptance. The new connector includes native printing and a privileged service; those requirements are not yet established. The owner requested a working happy path, public setup and CI that actually verifies this release rather than claiming an unattained gate.

For v0.1.0-rc.1, require locked restore, zero-warning C# build, meaningful core/Windows tests, shared protocol fixtures and MSI packaging. Collect and publish coverage, but do not claim or enforce 100% until the missing tests exist. Compile UI tests and execute them separately on an elevated interactive desktop; hosted CI does not silently mark them as passed.

Keep the coverage tooling and track the full-coverage goal. This exception does not suppress NuGet vulnerability warnings, compiler/analyzer errors, failed tests or installer build failures. Physical printer qualification remains separately recorded evidence.
