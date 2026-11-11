/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What the service control manager says about a service at one moment: its state as Windows numbers it (4 is
/// running) and the id of the process hosting it (0 when none does).
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct PeerServiceStatus(uint State, uint ProcessId);
