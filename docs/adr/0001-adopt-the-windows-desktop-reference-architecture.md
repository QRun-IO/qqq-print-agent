# ADR-0001: Windows AppKit service variant

Status: implemented for the initial prerelease. Date: 2026-10-29.

The owner requires the existing Windows AppKit foundation used by GCLO and MarkdownViewr.Windows. Cloud printing must continue without a logged-in desktop user.

Use the KofTwentyTwo Windows template's AppKit/WinUI shell with its documented Windows service variant: a .NET Worker Service, native Windows adapters, a CLI, and a signed-capable machine WiX MSI. The service owns cloud credentials and printing. The companion communicates over a local administrator pipe. Per-user Velopack hooks and self-updates are removed from the shipped companion.

The initial MSI is unsigned and labeled as a prerelease. Printer queues must be visible to LocalSystem; per-user connections are not guaranteed. AppKit preferences remain per-user, while service state lives in an ACL-protected ProgramData directory.
