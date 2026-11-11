/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One process the rules hold on to: what identifies it (the program it runs and when it started), whether it has
/// ended, and how it ended. Holding it keeps the answers about that one process, whatever else is started meanwhile.
/// It can be waited for and is never ended through this interface.
/// </summary>
internal interface IRunningProcess : IDisposable
{
   /// <summary>True once the process has ended.</summary>
   bool HasExited { get; }



   /// <summary>The full path of the program the process runs, or null when it has no main module. Reading it fails with what Windows reported when the process may not be inspected or has ended.</summary>
   string? ProgramPath { get; }



   /// <summary>When the process started, in universal time ticks. Together with the id it names one process: an id is reused, a start time with it is not.</summary>
   long StartTicks { get; }



   /// <summary>The code the process ended with; only meaningful once it has ended.</summary>
   int ExitCode { get; }



   /// <summary>Waits until the process has ended or the token is cancelled, which ends the wait with the cancellation and leaves the process running.</summary>
   Task WaitForExitAsync(CancellationToken cancellationToken);
}
