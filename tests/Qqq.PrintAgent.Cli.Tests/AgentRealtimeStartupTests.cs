/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>First steps of a realtime connection: how long the agent waits for the server's first acknowledgement, and what it reports when that wait fails.</summary>
public sealed class AgentRealtimeStartupTests
{
   /// <summary>
   /// A server that acknowledges the first state report 1.2 seconds late is accepted under the default policy. With the minimum
   /// one-second deadline and the acknowledgement withheld, connecting fails with an error naming the stage and the message type,
   /// the runtime logs a timeout, and no session cookie or device proof appears in that error. Nothing prints and no HTTP polling happens.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task InitialAcknowledgementDelayUsesNegotiatedPolicyAndReportsFailureStage(bool minimumDeadline)
   {
      // Do not race a 1200 ms ACK against a 1000 ms timer: under scheduler load
      // their continuations can run in either order. For the timeout case, keep
      // the ACK withheld until the runtime has actually reported that timeout.
      RealtimeSettings? settings = minimumDeadline ? new RealtimeSettings(Revision: 1, AckTimeoutMillis: 1000) : null;
      await using var fixture = new RealtimeRuntimeFixture(settings: settings);
      fixture.BeforeAcknowledgement = async message =>
      {
         if(message.Type is "STATE_APPLIED")
         {
            if(minimumDeadline)
            {
               Assert.False(fixture.Runtime.GetSnapshot().Connected);
               await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains("timed out", StringComparison.OrdinalIgnoreCase)));
            }
            else
            { await Task.Delay(1200); }
         }
      };
      if(minimumDeadline)
      {
         InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ConnectAsync());
         Assert.Contains("initial realtime state synchronization", failure.Message, StringComparison.Ordinal);
         Assert.Contains("STATE_APPLIED", failure.Message, StringComparison.Ordinal);
         await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains("timed out", StringComparison.OrdinalIgnoreCase)));
         Assert.DoesNotContain("synthetic-proof", failure.Message, StringComparison.Ordinal);
         Assert.DoesNotContain("synthetic-session", failure.Message, StringComparison.Ordinal);
      }
      else
      {
         _ = await fixture.ConnectAsync();
         Assert.True(fixture.Runtime.GetSnapshot().Connected);
      }
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }
}
