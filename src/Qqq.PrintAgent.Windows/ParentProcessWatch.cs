/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real watch behind <see cref="IParentWatch"/>: it waits for the parent process by its id and ends this process
/// when the parent is gone. A lookup or wait that fails for any reason other than the parent not running is not handled
/// here; see <see cref="WatchAsync"/>.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Waits for another process by its id and calls Environment.Exit when that process has exited or is not running. A test cannot run this to its end inside the test process, because it would end the test run, and the wait depends on a second real process. Nothing about printing is decided here; the watch itself has four outcomes. A wait cancelled by the helper means its operation finished; every test that runs the fixture helper to its end goes through it with real processes. A parent that exits during the wait means exit code 2: PrinterWorkerIsolationTests.WorkerExitsWhenItsParentDiesDuringANativeOperation. A parent that is already gone when the helper looks for it means exit code 2 before any request is read: PrinterWorkerIsolationTests.WorkerExitsAtOnceWhenItsParentIsAlreadyGone. A lookup or wait that fails in any other way is not caught here: the task fails, nothing ends the helper, the operation runs unwatched, and the failure comes out only when PrinterWorkerHost.RunAsync awaits the watch in its finally block; no test arranges such a failure in the real watch, and what RunAsync does with a watch that failed is shown by PrinterWorkerHostTests.WatchThatFailsLeavesTheOperationUnwatchedAndSurfacesWhenTheHelperEnds with a fake. When the watch is started and stopped, and everything the helper does in between, is decided in PrinterWorkerHost, which is tested with a fake of IParentWatch.")]
internal sealed class ParentProcessWatch : IParentWatch
{
   /// <summary>
   /// Waits for the parent service to exit and then terminates this helper immediately with exit code 2, so a blocked
   /// driver call can never leave an orphan process running after the service is gone. A parent that is not running when
   /// it is looked up ends the helper the same way, at once. A cancelled wait is the normal end: the helper's operation
   /// finished. Any other failure of the lookup or the wait is not caught: the returned task fails and this process is not
   /// ended, so the helper carries out its operation with nobody watching the parent, and the failure is seen only by
   /// whoever awaits the task, which is <see cref="PrinterWorkerHost"/> once the operation is done.
   /// </summary>
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
