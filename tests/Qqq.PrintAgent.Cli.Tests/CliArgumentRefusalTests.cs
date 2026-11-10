/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Malformed command lines. A command accepts exactly its own options, each once and each with a value, and nothing else.
/// Anything different is refused before the service is contacted and before the enrollment code is asked for: the reason and
/// the usage go to standard error, standard output stays empty and the exit code is 1. The tool never guesses what was meant.
/// </summary>
public sealed class CliArgumentRefusalTests
{
   /// <summary>A command that takes no options refuses any extra word, including help flags after the command and every option of the removed legacy mode (--legacy-compat, --data-dir, --once, --service).</summary>
   [Theory]
   [InlineData("printers", "extra")]
   [InlineData("printers", "--help")]
   [InlineData("printers", "--legacy-compat")]
   [InlineData("printers", "--legacy-compat", "--data-dir", "C:\\synthetic")]
   [InlineData("run", "--once")]
   [InlineData("run", "--once", "--legacy-compat")]
   [InlineData("run", "--data-dir", "C:\\synthetic")]
   [InlineData("run", "--service")]
   [InlineData("sign-out", "--service")]
   [InlineData("sign-out", "now")]
   [InlineData("service-status", "--service")]
   [InlineData("service-status", "-h")]
   [InlineData("snapshot", "--data-dir", "C:\\synthetic")]
   [InlineData("snapshot", "")]
   public async Task CommandWithoutOptions_RefusesAnythingExtra(params string[] args)
   {
      await AssertRefusedAsync($"Invalid arguments for '{args[0]}'.", args);
   }



   /// <summary>Enrolling requires exactly --server with a value: a missing, blank, repeated, misspelled or positional server, any other option, and any trailing word are all refused.</summary>
   [Theory]
   [InlineData("enroll")]
   [InlineData("enroll", "--server")]
   [InlineData("enroll", "--server", "")]
   [InlineData("enroll", "--server", "   ")]
   [InlineData("enroll", "https://qqq.example.test")]
   [InlineData("enroll", "https://qqq.example.test", "--server")]
   [InlineData("enroll", "--Server", "https://qqq.example.test")]
   [InlineData("enroll", "--printer", "synthetic-queue-1")]
   [InlineData("enroll", "--server", "https://qqq.example.test", "--server", "https://other.example.test")]
   [InlineData("enroll", "--server", "https://qqq.example.test", "extra")]
   [InlineData("enroll", "--server", "https://qqq.example.test", "--service")]
   [InlineData("enroll", "--server", "https://qqq.example.test", "--legacy-compat")]
   [InlineData("enroll", "--server", "https://qqq.example.test", "--data-dir", "C:\\synthetic")]
   [InlineData("enroll", "--legacy-compat", "--server", "https://qqq.example.test")]
   public async Task Enroll_RefusesAnythingButOneServer(params string[] args)
   {
      await AssertRefusedAsync("Invalid arguments for 'enroll'.", args);
   }



   /// <summary>Sharing a printer requires exactly --printer and --exposed, each once with a value: either one missing, blank or repeated, any other option, and any trailing word are all refused.</summary>
   [Theory]
   [InlineData("expose")]
   [InlineData("expose", "--printer", "synthetic-queue-1")]
   [InlineData("expose", "--exposed", "true")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--exposed")]
   [InlineData("expose", "--printer", "--exposed", "true")]
   [InlineData("expose", "--printer", "", "--exposed", "true")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--exposed", " ")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--printer", "synthetic-queue-2")]
   [InlineData("expose", "--exposed", "true", "--exposed", "false")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--server", "https://qqq.example.test")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--exposed", "true", "extra")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--exposed", "true", "--service")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--exposed", "true", "--legacy-compat")]
   [InlineData("expose", "synthetic-queue-1", "true")]
   public async Task Expose_RefusesAnythingButOnePrinterAndOneChoice(params string[] args)
   {
      await AssertRefusedAsync("Invalid arguments for 'expose'.", args);
   }



   /// <summary>The choice must be the word true or false; a number, yes or no, or any other word is refused with a message that says so.</summary>
   [Theory]
   [InlineData("yes")]
   [InlineData("no")]
   [InlineData("1")]
   [InlineData("0")]
   [InlineData("on")]
   [InlineData("truee")]
   public async Task Expose_RefusesAChoiceThatIsNotTrueOrFalse(string choice)
   {
      await AssertRefusedAsync("--exposed must be true or false.", "expose", "--printer", "synthetic-queue-1", "--exposed", choice);
      await AssertRefusedAsync("--exposed must be true or false.", "expose", "--exposed", choice, "--printer", "synthetic-queue-1");
   }



   /// <summary>Runs the arguments against a command line whose enrollment code is available, and requires a complete refusal: exit code 1, the reason and the usage on standard error, nothing on standard output, nothing sent, and neither the environment nor standard input consulted.</summary>
   private static async Task AssertRefusedAsync(string reason, params string[] args)
   {
      using CliFixture cli = new();
      cli.Variables[CommandLine.EnrollmentCodeVariable] = "synthetic-code-4471";
      cli.TypedLines.Enqueue("synthetic-code-4471");

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(1, exitCode);
      Assert.Equal("", cli.Output);
      Assert.Equal(reason + "\n" + CommandLine.Usage.ReplaceLineEndings("\n") + "\n", cli.Error.ReplaceLineEndings("\n"));
      Assert.Empty(cli.Sent);
      Assert.Empty(cli.VariablesRead);
      Assert.Equal(0, cli.LinesRead);
   }
}
