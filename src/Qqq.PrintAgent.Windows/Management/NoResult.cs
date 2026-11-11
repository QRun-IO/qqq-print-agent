/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The result of an operation that succeeded and has nothing to report. It is an empty object on the wire, so every successful reply has a result and a later version can give this one members.</summary>
public sealed record NoResult;
