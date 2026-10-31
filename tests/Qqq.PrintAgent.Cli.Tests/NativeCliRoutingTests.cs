using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class NativeCliRoutingTests
{
   [Theory]
   [InlineData("printers", "printers")]
   [InlineData("run", "reconnect")]
   [InlineData("sign-out", "sign-out")]
   public async Task NormalCommandsUseServiceRatherThanPrivateBearerTransport(string command, string expected)
   {
      PipeRequest? sent = null;
      int result = await Program.DispatchAsync([command], request =>
      {
         sent = request;
         return Task.FromResult("Synthetic service accepted request.");
      });
      Assert.Equal(0, result);
      Assert.Equal(expected, sent!.Operation);
   }

   [Fact]
   public async Task ForegroundOptionsRequireExplicitCompatibilityBeforeAnyServiceCall()
   {
      await Assert.ThrowsAsync<ArgumentException>(() => Program.DispatchAsync(["run", "--once"], _ => throw new InvalidOperationException("Must not contact service.")));
      Assert.Throws<ArgumentException>(() => Program.UseLegacyCompatibility(["run", "--legacy-compat", "--service"]));
      Assert.True(Program.UseLegacyCompatibility(["run", "--legacy-compat"]));
      Assert.False(Program.UseLegacyCompatibility(["run"]));
   }
}
