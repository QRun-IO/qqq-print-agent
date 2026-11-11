/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real watch behind <see cref="IParentWatch"/>: it waits for the parent process by its id and ends this process
/// when the parent is gone.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Waits for another process by its id and calls Environment.Exit when that process has exited or does not exist. A test cannot run this to its end inside the test process, because it would end the test run, and the wait depends on a second real process. Nothing about printing is decided here; the watch itself is three outcomes: a wait cancelled by the helper means its operation finished, a parent that exited means exit code 2, and a parent that no longer exists means exit code 2. They are exercised end to end, with real processes, by PrinterWorkerIsolationTests.WorkerExitsWhenItsParentDiesDuringANativeOperation. When the watch is started and stopped, and everything the helper does in between, is decided in PrinterWorkerHost, which is tested with a fake of IParentWatch.")]
internal sealed class ParentProcessWatch : IParentWatch
{
   /// <summary>Waits for the parent service to exit and then terminates this helper immediately, so a blocked driver call can never leave an orphan process running after the service is gone.</summary>
   public async Task WatchAsync(int parentId, CancellationToken token)
   {
      try
      {
         using var parent = Process.GetProcessById(parentId);
         await parent.WaitForExitAsync(token).ConfigureAwait(false);
         Environment.Exit(2);
      }
      catch(OperationCanceledException) when(token.IsCancellationRequested)
      {
         // The requested operation completed normally.
      }
      catch(ArgumentException)
      {
         Environment.Exit(2);
      }
   }
}
