/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Where a file is staged for a privileged Windows component to read, and the two checks that make that safe: one that the
/// folder is the protected machine state (it throws when the folder cannot be trusted), and one that a file just written
/// there has a trusted owner and access list (it throws otherwise). The installed product passes the machine state guard's
/// checks; a test passes a temporary folder and checks of its own, because a temporary file can never pass the real ones.
/// </summary>
internal sealed record ProtectedStaging(string Directory, Action<string> EnsureFolder, Action<string> ValidateFile);
