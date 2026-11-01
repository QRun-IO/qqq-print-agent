/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Control polling and printing run side by side: a server disconnect or reconnect instruction must be applied while a print is still in progress, without interrupting that print.</summary>
public sealed class AgentControlConcurrencyTests
{
   /// <summary>
   /// With a print blocked inside the spooler, the server marks the device disconnected: control polls continue and the new state
   /// is applied, heartbeats stop (no normal traffic while disconnected) and no further claim is made, yet the blocked job stays
   /// SUBMITTING. Reconnecting restores normal state, and when the print is released the job completes as SPOOLED.
   /// </summary>
   [Fact]
   public async Task RemoteDisconnectAndReconnectDoNotInterruptBlockedPrintOrControlPolling()
   {
      using ControlFixture fixture = new();
      fixture.OfferJob = true;
      fixture.Printer.BlockPrint = true;
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
      using AgentRuntime runtime = fixture.Runtime();
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         await fixture.Printer.Entered.Task.WaitAsync(stop.Token);
         fixture.Desired = new DeviceDesiredState(2, true, "DISCONNECTED");
         await UntilAsync(() => runtime.GetSnapshot().Control!.DesiredState.Revision == 2, stop.Token);
         int polls = Volatile.Read(ref fixture.Polls);
         await UntilAsync(() => Volatile.Read(ref fixture.Polls) >= polls + 3, stop.Token);
         int heartbeats = Volatile.Read(ref fixture.Heartbeats);
         await UntilAsync(() => Volatile.Read(ref fixture.Polls) >= polls + 6, stop.Token);
         Assert.Equal(heartbeats, Volatile.Read(ref fixture.Heartbeats));
         Assert.Equal(1, Volatile.Read(ref fixture.Claims));
         Assert.Equal("SUBMITTING", Assert.Single(runtime.GetSnapshot().Jobs).Status);
         fixture.Desired = new DeviceDesiredState(3, false, "CONNECTED");
         await UntilAsync(() => runtime.GetSnapshot().Control!.DesiredState.Revision == 3, stop.Token);
         fixture.Printer.Release.TrySetResult();
         await UntilAsync(() => runtime.GetSnapshot().Jobs.Any(job => string.Equals(job.Status, "SPOOLED", StringComparison.Ordinal)), stop.Token);
      }
      finally
      {
         await stop.CancelAsync();
         try
         { await running; }
         catch(OperationCanceledException) { }
      }
   }



   /// <summary>Polls a condition every 10 ms until it holds; the caller's token supplies the overall deadline.</summary>
   private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
   {
      while(!condition())
      { await Task.Delay(10, token); }
   }
}
