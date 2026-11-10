/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.UpdatePrepare"/>. Claiming is paused either way. <see cref="Ready"/> says
/// whether the installer may replace files now; when it is false, the other two say what was still in progress when the
/// service stopped waiting: jobs claimed before the pause, a claim the server had not answered yet, or both.
/// </summary>
public sealed record UpdatePrepareResult(bool Ready, int JobsInHand, bool ClaimPending);
