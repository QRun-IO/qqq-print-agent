namespace Qqq.PrintAgent.Cli.Tests;

public sealed class ProgramTests
{
   [Fact]
   public async Task Help_ExitsSuccessfully()
   {
      Assert.Equal(0, await Program.Main(["help"]));
   }

   [Fact]
   public async Task Enroll_MissingServerFails()
   {
      Assert.Equal(1, await Program.Main(["enroll"]));
   }
}
