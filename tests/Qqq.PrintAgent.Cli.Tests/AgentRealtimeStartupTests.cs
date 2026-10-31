using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRealtimeStartupTests
{
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task InitialAcknowledgementDelayUsesNegotiatedPolicyAndReportsFailureStage(bool minimumDeadline)
   {
      // Model a delayed server/test continuation, without CPU starvation or global
      // thread-pool changes that would contaminate concurrently running tests.
      RealtimeSettings? settings = minimumDeadline ? new RealtimeSettings(Revision: 1, AckTimeoutMillis: 1000) : null;
      await using var fixture = new RealtimeRuntimeFixture(settings: settings);
      fixture.BeforeAcknowledgement = async message =>
      {
         if(message.Type is "STATE_APPLIED")
         { await Task.Delay(1200); }
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
