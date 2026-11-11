/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What one run of the task scheduler's command-line program came to: its exit code and what it printed, or, when it did
/// not finish within its limit and was stopped, the cancellation that ended the wait.
/// </summary>
internal sealed record TaskSchedulerRun(int ExitCode, string Output, Exception? TimedOut = null);
