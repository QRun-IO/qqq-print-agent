/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentControlConcurrencyTests
{
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

   private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
   {
      while(!condition())
      { await Task.Delay(10, token); }
   }
}
