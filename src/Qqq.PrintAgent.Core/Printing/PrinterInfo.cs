/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>A printer the agent discovered and may offer to the server: identity, driver, port, status, accepted formats and media profiles. The id is stable so jobs and settings keep pointing at the same printer.</summary>
public sealed record PrinterInfo(string PrinterId, string Name, string DriverName, string PortName, string Status, string[] Formats, string? DisplayName = null, string? Description = null, string? Location = null,
   IReadOnlyList<PrinterMediaProfile>? MediaProfiles = null);


