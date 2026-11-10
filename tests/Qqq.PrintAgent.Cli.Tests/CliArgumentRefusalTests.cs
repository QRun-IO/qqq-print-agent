/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Malformed command lines. A command accepts exactly its own options, each once and each with a value, and nothing else.
/// Anything different is refused before the service is contacted and before the enrollment code is asked for: one line
/// that says what is wrong, then the usage, on standard error; nothing on standard output; exit code 1. The reason names
/// only the tool's own commands and options, never what was typed, and the tool never guesses what was meant.
/// </summary>
public sealed class CliArgumentRefusalTests
{
   private const string Server = "https://qqq.example.test";
   private const string Printer = "synthetic-queue-1";



   /// <summary>A command that takes no options says so when anything follows it.</summary>
   [Theory]
   [InlineData("printers", "extra")]
   [InlineData("printers", "--server", Server)]
   [InlineData("run", "now")]
   [InlineData("sign-out", "now")]
   [InlineData("service-status", "--verbose")]
   [InlineData("snapshot", "")]
   [InlineData("snapshot", "--help", "extra")]
   public async Task CommandWithoutOptions_RefusesAnythingAfterIt(params string[] args)
   {
      await AssertRefusedAsync($"'{args[0]}' takes no arguments.", args);
   }



   /// <summary>An argument in an option's place that is not one of the command's options is refused with the options the command does accept: a positional value, a misspelled or differently cased option, another command's option, or a trailing word.</summary>
   [Theory]
   [InlineData("'enroll' accepts only --server.", "enroll", Server)]
   [InlineData("'enroll' accepts only --server.", "enroll", Server, "--server")]
   [InlineData("'enroll' accepts only --server.", "enroll", "--Server", Server)]
   [InlineData("'enroll' accepts only --server.", "enroll", "--printer", Printer)]
   [InlineData("'enroll' accepts only --server.", "enroll", "--server", Server, "extra")]
   [InlineData("'enroll' accepts only --server.", "enroll", "--code", "synthetic-code-4471", "--server", Server)]
   [InlineData("'expose' accepts only --printer and --exposed.", "expose", Printer, "true")]
   [InlineData("'expose' accepts only --printer and --exposed.", "expose", "--printer", Printer, "--server", Server)]
   [InlineData("'expose' accepts only --printer and --exposed.", "expose", "--printer", Printer, "--exposed", "true", "extra")]
   [InlineData("'expose' accepts only --printer and --exposed.", "expose", "--Exposed", "true", "--printer", Printer)]
   public async Task UnexpectedArgument_IsRefusedWithTheOptionsTheCommandAccepts(string reason, params string[] args)
   {
      await AssertRefusedAsync(reason, args);
   }



   /// <summary>A required option that is absent is named.</summary>
   [Theory]
   [InlineData("--server is required.", "enroll")]
   [InlineData("--printer is required.", "expose")]
   [InlineData("--printer is required.", "expose", "--exposed", "true")]
   [InlineData("--exposed is required.", "expose", "--printer", Printer)]
   public async Task MissingOption_IsNamed(string reason, params string[] args)
   {
      await AssertRefusedAsync(reason, args);
   }



   /// <summary>An option with nothing after it, with a blank value, or followed by another option name is refused as needing a value. An option name is never taken as a value, so a forgotten value cannot make the next option disappear into it.</summary>
   [Theory]
   [InlineData("--server needs a value.", "enroll", "--server")]
   [InlineData("--server needs a value.", "enroll", "--server", "")]
   [InlineData("--server needs a value.", "enroll", "--server", "   ")]
   [InlineData("--server needs a value.", "enroll", "--server", "--server")]
   [InlineData("--server needs a value.", "enroll", "--server", "--anything")]
   [InlineData("--printer needs a value.", "expose", "--printer", "--exposed", "true")]
   [InlineData("--printer needs a value.", "expose", "--printer", "--exposed", "--exposed", "true")]
   [InlineData("--printer needs a value.", "expose", "--printer", "", "--exposed", "true")]
   [InlineData("--exposed needs a value.", "expose", "--printer", Printer, "--exposed")]
   [InlineData("--exposed needs a value.", "expose", "--printer", Printer, "--exposed", " ")]
   [InlineData("--exposed needs a value.", "expose", "--exposed", "--printer", Printer)]
   public async Task OptionWithoutAValue_IsRefused(string reason, params string[] args)
   {
      await AssertRefusedAsync(reason, args);
   }



   /// <summary>An option given twice is refused, whether or not the two values agree; the tool does not pick one.</summary>
   [Theory]
   [InlineData("--server may be given only once.", "enroll", "--server", Server, "--server", "https://other.example.test")]
   [InlineData("--server may be given only once.", "enroll", "--server", Server, "--server", Server)]
   [InlineData("--printer may be given only once.", "expose", "--printer", Printer, "--printer", "synthetic-queue-2")]
   [InlineData("--exposed may be given only once.", "expose", "--exposed", "true", "--exposed", "false", "--printer", Printer)]
   public async Task RepeatedOption_IsRefused(string reason, params string[] args)
   {
      await AssertRefusedAsync(reason, args);
   }



   /// <summary>The choice must be the word true or false, in any letter case and with nothing around it. A number, yes or no, any other word, and true or false padded with white space are refused with a message that says so, wherever the option stands.</summary>
   [Theory]
   [InlineData("yes")]
   [InlineData("no")]
   [InlineData("1")]
   [InlineData("0")]
   [InlineData("on")]
   [InlineData("truee")]
   [InlineData(" true")]
   [InlineData("true ")]
   [InlineData("\tfalse")]
   public async Task Expose_RefusesAChoiceThatIsNotTrueOrFalse(string choice)
   {
      await AssertRefusedAsync("--exposed must be true or false.", "expose", "--printer", Printer, "--exposed", choice);
      await AssertRefusedAsync("--exposed must be true or false.", "expose", "--exposed", choice, "--printer", Printer);
   }



   /// <summary>Every option of the legacy mode removed in 2.0 is refused with one fixed sentence that names all four and says they are gone, wherever it appears and whatever else is on the command line, so a script written for 1.x learns why it no longer runs.</summary>
   [Theory]
   [InlineData("--legacy-compat")]
   [InlineData("--service")]
   [InlineData("--legacy-compat", "run", "--once")]
   [InlineData("run", "--once")]
   [InlineData("run", "--legacy-compat", "--once", "--data-dir", "C:\\synthetic")]
   [InlineData("printers", "--data-dir", "C:\\synthetic")]
   [InlineData("sign-out", "--service")]
   [InlineData("service-status", "--service")]
   [InlineData("enroll", "--service", "--server", Server)]
   [InlineData("enroll", "--server", Server, "--legacy-compat")]
   [InlineData("enroll", "--server", "--service")]
   [InlineData("expose", "--printer", Printer, "--exposed", "true", "--service")]
   [InlineData("unknown-word", "--once")]
   public async Task RemovedLegacyOption_IsRefusedWithTheReasonItIsGone(params string[] args)
   {
      await AssertRefusedAsync("The options --service, --legacy-compat, --data-dir and --once were removed in 2.0: the command line only manages the installed service.", args);
   }



   /// <summary>A help flag in last place after a real command is a request for help, not a mistake: the usage goes to standard output, the exit code is 0, nothing is sent and no enrollment code is asked for, even when the rest of the command line would have been refused.</summary>
   [Theory]
   [InlineData("printers", "--help")]
   [InlineData("printers", "-h")]
   [InlineData("enroll", "--help")]
   [InlineData("enroll", "--server", Server, "-h")]
   [InlineData("enroll", "--server", "--help")]
   [InlineData("expose", "--printer", Printer, "--exposed", "maybe", "--help")]
   [InlineData("run", "--once", "--help")]
   public async Task HelpFlagAfterACommand_PrintsTheUsageAndSucceeds(params string[] args)
   {
      using CliFixture cli = new();
      cli.TypedLines.Enqueue("synthetic-code-4471");

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(0, exitCode);
      Assert.Equal(CommandLine.Usage.ReplaceLineEndings("\n") + "\n", cli.Output.ReplaceLineEndings("\n"));
      Assert.Equal("", cli.Error);
      Assert.Empty(cli.Sent);
      Assert.Empty(cli.VariablesRead);
      Assert.Equal(0, cli.LinesRead);
   }



   /// <summary>A help flag helps only in last place after a real command. After a word that is not a command it does not turn the refusal into a success, and in the middle of a command line it is an argument like any other.</summary>
   [Theory]
   [InlineData("Unknown command.", "unknown-word", "--help")]
   [InlineData("Unknown command.", "--printer-worker", "-h")]
   [InlineData("'printers' takes no arguments.", "printers", "--help", "extra")]
   [InlineData("'enroll' accepts only --server.", "enroll", "-h", "--server", Server)]
   public async Task HelpFlagElsewhere_DoesNotRescueABadCommandLine(string reason, params string[] args)
   {
      await AssertRefusedAsync(reason, args);
   }



   /// <summary>Runs the arguments against a command line whose enrollment code is available every way it could be asked for, and requires a complete refusal: exit code 1, the reason and the usage on standard error, nothing on standard output, nothing sent, and the environment, standard input and the console all left unread.</summary>
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
      Assert.Equal(0, cli.KeysRead);
   }
}
