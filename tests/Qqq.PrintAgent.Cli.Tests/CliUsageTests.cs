/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Help and unknown commands. Asking for help is a success and prints the usage on standard output; a command the tool does
/// not know is a failure and prints the usage on standard error, so a script that captures output never mistakes help text
/// for a result. Neither contacts the service.
/// </summary>
public sealed class CliUsageTests
{
   /// <summary>The exact help text an operator sees. It is repeated here on purpose: changing what the tool tells operators should be a deliberate edit in two places, not a side effect.</summary>
   private const string ExpectedUsage = """
      Usage: qqq-print-agent <command>

      Commands:
        printers                                  List the printers the service can see, as JSON.
        enroll --server URL                       Pair this computer with a QQQ server using a one-time enrollment code.
        expose --printer ID --exposed true|false  Share a printer with QQQ, or stop sharing it.
        run                                       Ask the service to refresh its connection to QQQ.
        sign-out                                  End the service's QQQ session.
        service-status                            Show the service's connection status.
        snapshot                                  Show the service's state as JSON: connection, printers, recent jobs and logs.
        help                                      Show this text (also --help, -h).

      Every command is carried out by the installed print service, which owns the sign-in, the pairing and all printing.
      Run from an administrator prompt, and sign in through the companion app first.
      The enrollment code is read from the QQQ_PRINT_ENROLLMENT_CODE environment variable, or typed at a prompt;
      it is never accepted on the command line.
      Exit code: 0 on success; 1 when the arguments are wrong, the service cannot be reached or the service reports an error.
      """;



   /// <summary>No arguments, help, --help and -h all print exactly the usage on standard output, write nothing to standard error, send nothing to the service and exit 0. Words after the help word do not change that.</summary>
   [Theory]
   [InlineData]
   [InlineData("help")]
   [InlineData("--help")]
   [InlineData("-h")]
   [InlineData("help", "enroll")]
   [InlineData("--help", "--anything")]
   public async Task AskingForHelp_PrintsUsageOnStandardOutputAndSucceeds(params string[] args)
   {
      using CliFixture cli = new();

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(0, exitCode);
      Assert.Equal(ExpectedUsage.ReplaceLineEndings("\n") + "\n", cli.Output.ReplaceLineEndings("\n"));
      Assert.Equal("", cli.Error);
      Assert.Empty(cli.Sent);
   }



   /// <summary>A first word that is not a command is refused: standard error gets the reason and the usage, standard output stays empty, nothing is sent and the exit code is 1. Command names are case-sensitive, the service's own operation names are not commands, and the option that selected the removed legacy mode is no longer a word the tool knows.</summary>
   [Theory]
   [InlineData("status")]
   [InlineData("Printers")]
   [InlineData("--legacy-compat")]
   [InlineData("--legacy-compat", "run", "--once")]
   [InlineData("--service")]
   [InlineData("--version")]
   [InlineData("")]
   public async Task UnknownCommand_PrintsUsageOnStandardErrorAndFails(params string[] args)
   {
      using CliFixture cli = new();

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(1, exitCode);
      Assert.Equal("", cli.Output);
      Assert.Equal("Unknown command.\n" + ExpectedUsage.ReplaceLineEndings("\n") + "\n", cli.Error.ReplaceLineEndings("\n"));
      Assert.Empty(cli.Sent);
   }



   /// <summary>The refusal never repeats what was typed. A mistyped command line can carry a secret (an enrollment code, a sign-in callback address) and standard error is often captured into logs.</summary>
   [Fact]
   public async Task UnknownCommand_IsNotEchoed()
   {
      using CliFixture cli = new();

      int exitCode = await cli.RunAsync("frobnicate-7Q2", "synthetic-secret-91");

      Assert.Equal(1, exitCode);
      Assert.DoesNotContain("frobnicate-7Q2", cli.Everything, StringComparison.Ordinal);
      Assert.DoesNotContain("synthetic-secret-91", cli.Everything, StringComparison.Ordinal);
   }



   /// <summary>The usage lists every command the tool accepts, each at the start of its own line, so help cannot fall behind the commands that exist.</summary>
   [Fact]
   public void Usage_ListsEveryCommand()
   {
      string[] lines = CommandLine.Usage.ReplaceLineEndings("\n").Split('\n');

      Assert.Equal(7, CommandLine.CommandNames.Count);
      Assert.All(CommandLine.CommandNames, name => Assert.Contains(lines, line => line.StartsWith("  " + name + " ", StringComparison.Ordinal)));
   }



   /// <summary>The usage no longer describes the removed legacy mode or any option that existed only for it.</summary>
   [Theory]
   [InlineData("legacy")]
   [InlineData("--data-dir")]
   [InlineData("--once")]
   [InlineData("--service")]
   public void Usage_DoesNotMentionTheRemovedLegacyMode(string removed)
   {
      Assert.DoesNotContain(removed, CommandLine.Usage, StringComparison.OrdinalIgnoreCase);
   }



   /// <summary>The real entry point, with the real console and pipe client bound, answers help with exit code 0 and never needs the service for it. This is the only test that runs the production composition in this process.</summary>
   [Fact]
   public async Task RealEntryPoint_AnswersHelpWithoutTheService()
   {
      Assert.Equal(0, await Program.Main(["--help"]));
   }
}
