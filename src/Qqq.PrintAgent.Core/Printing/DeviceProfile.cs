/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Optional client-owned labels. Null preserves metadata; empty text clears it.</summary>
public sealed record DeviceProfile(string? Name = null, string? Description = null, string? Location = null);
