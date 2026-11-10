/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The one-time enrollment code is a secret. It comes from an environment variable or from standard input (piped in, or
/// typed at a prompt without being shown), never from the command line; it goes to the service in the enrollment request
/// and nowhere else; and nothing the tool prints ever contains it, whether the enrollment works, is refused or cannot be sent.
/// </summary>
public sealed class CliEnrollmentCodeTests
{
   private const string Code = "synthetic-code-4471";
   private const string Server = "https://qqq.example.test";
   private const string Prompt = "Enrollment code: \n";



   /// <summary>With the variable set, the code is taken from QQQ_PRINT_ENROLLMENT_CODE: no prompt is written, no line and no key is read, so an unattended script never blocks.</summary>
   [Fact]
   public async Task CodeInTheEnvironment_IsUsedWithoutPrompting()
   {
      using CliFixture cli = new() { Reply = "Enrolled." };
      cli.Variables["QQQ_PRINT_ENROLLMENT_CODE"] = Code;

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal("QQQ_PRINT_ENROLLMENT_CODE", Assert.Single(cli.VariablesRead));
      Assert.Equal(0, cli.LinesRead);
      Assert.Equal(0, cli.KeysRead);
      Assert.Equal(Code, Assert.Single(cli.Sent).EnrollmentCode);
      Assert.Equal("Enrolled.\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>With the variable missing or blank and standard input redirected, the code is the line that is piped in. The prompt goes to standard error and its line is ended there, so standard output holds the service's reply and nothing else, and no key is read from a console that is not there.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("   ")]
   public async Task CodePipedIn_IsReadAsALineWithThePromptOnStandardError(string? variable)
   {
      using CliFixture cli = new() { Reply = "Enrolled." };
      cli.Variables["QQQ_PRINT_ENROLLMENT_CODE"] = variable;
      cli.TypedLines.Enqueue(Code);

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(1, cli.LinesRead);
      Assert.Equal(0, cli.KeysRead);
      Assert.Equal(new PipeRequest("enroll", Server, Code), Assert.Single(cli.Sent));
      Assert.Equal("Enrolled.\n", cli.Output);
      Assert.Equal(Prompt, cli.Error);
   }



   /// <summary>At a console the code is read key by key up to Enter, and every key is read without being shown. Standard input is not read as a line, and the prompt line is ended on standard error because the Enter was not shown either.</summary>
   [Fact]
   public async Task CodeTypedAtTheConsole_IsReadWithoutBeingShown()
   {
      using CliFixture cli = new() { Reply = "Enrolled.", InputRedirected = false };
      cli.Type(Code);
      cli.Press(ConsoleKey.Enter, '\r');

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(Code.Length + 1, cli.KeysRead);
      Assert.Equal(0, cli.KeysShown);
      Assert.Equal(0, cli.LinesRead);
      Assert.Equal(new PipeRequest("enroll", Server, Code), Assert.Single(cli.Sent));
      Assert.Equal("Enrolled.\n", cli.Output);
      Assert.Equal(Prompt, cli.Error);
   }



   /// <summary>While typing, Backspace removes the last character and does nothing on an empty line, and keys that produce no printable character (arrows, function keys, Tab, Ctrl combinations) are ignored, so the code sent is what the operator meant to type.</summary>
   [Fact]
   public async Task TypingAtTheConsole_HonorsBackspaceAndIgnoresKeysWithoutACharacter()
   {
      using CliFixture cli = new() { InputRedirected = false };
      cli.Press(ConsoleKey.Backspace, '\b');
      cli.Type("synthetic-cods");
      cli.Press(ConsoleKey.Backspace, '\b');
      cli.Press(ConsoleKey.Backspace, '\b');
      cli.Press(ConsoleKey.LeftArrow);
      cli.Press(ConsoleKey.F5);
      cli.Press(ConsoleKey.Tab, '\t');
      cli.Press(ConsoleKey.A, '\u0001');
      cli.Type("de-4471");
      cli.Press(ConsoleKey.Enter, '\r');
      cli.Type("typed-after-enter");

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(Code, Assert.Single(cli.Sent).EnrollmentCode);
      Assert.Equal(0, cli.KeysShown);
      Assert.Equal("typed-after-enter".Length, cli.Keys.Count);
   }



   /// <summary>White space around the code is dropped, whichever way it arrives, and white space inside it is kept. A code never begins or ends with white space, but a shell can add some: cmd's "echo CODE | ..." pipes a trailing space.</summary>
   [Theory]
   [InlineData("variable", "  synthetic-code-4471 ", "synthetic-code-4471")]
   [InlineData("piped", "synthetic-code-4471 ", "synthetic-code-4471")]
   [InlineData("piped", "\tsynthetic code 4471\t", "synthetic code 4471")]
   [InlineData("typed", " synthetic-code-4471  ", "synthetic-code-4471")]
   public async Task WhiteSpaceAroundTheCode_IsDropped(string source, string given, string sent)
   {
      using CliFixture cli = new();
      Supply(cli, source, given);

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest("enroll", Server, sent), Assert.Single(cli.Sent));
   }



   /// <summary>When no code is available (redirected input is at its end or holds a blank line, or the operator presses only Enter or spaces), enrollment fails: the prompt line is ended, then one line on standard error says why, the exit code is 1, standard output is empty and no request reaches the service.</summary>
   [Theory]
   [InlineData("piped", null)]
   [InlineData("piped", "")]
   [InlineData("piped", "  ")]
   [InlineData("typed", "")]
   [InlineData("typed", "   ")]
   public async Task NoCodeAtAll_FailsWithoutContactingTheService(string source, string? given)
   {
      using CliFixture cli = new();
      if(given is not null)
      {
         Supply(cli, source, given);
      }

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(1, exitCode);
      Assert.Empty(cli.Sent);
      Assert.Equal("", cli.Output);
      Assert.Equal(Prompt + "Enrollment code is required.\n", cli.Error);
   }



   /// <summary>
   /// The code is never printed, whichever way it arrived and however the enrollment ends: accepted, refused by the service,
   /// timed out, or failed to send. The fake service's replies do not contain the code, and the real service's cannot: its
   /// reply to an enrollment is the new device's identifier, or "Error:" followed by its own description of the failure,
   /// and it copies neither the request nor the server's response body into either. So the reply is printed as it comes,
   /// with no filtering to test.
   /// </summary>
   [Theory]
   [InlineData("variable", "accepted")]
   [InlineData("piped", "accepted")]
   [InlineData("typed", "accepted")]
   [InlineData("variable", "refused")]
   [InlineData("piped", "refused")]
   [InlineData("typed", "refused")]
   [InlineData("variable", "timeout")]
   [InlineData("piped", "timeout")]
   [InlineData("typed", "timeout")]
   [InlineData("variable", "failure")]
   [InlineData("piped", "failure")]
   [InlineData("typed", "failure")]
   public async Task Code_NeverAppearsInOutput(string source, string outcome)
   {
      using CliFixture cli = new()
      {
         Reply = outcome is "refused" ? "Error: The synthetic service refused the enrollment." : "Enrolled.",
         SendFailure = outcome switch
         {
            "timeout" => new TaskCanceledException("Synthetic request timeout."),
            "failure" => new IOException("Service connection closed before a complete response was received."),
            _ => null,
         },
      };
      Supply(cli, source, Code);

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(outcome is "accepted" ? 0 : 1, exitCode);
      Assert.Equal(Code, Assert.Single(cli.Sent).EnrollmentCode);
      Assert.DoesNotContain(Code, cli.Everything, StringComparison.Ordinal);
   }



   /// <summary>A code put on the command line is refused, not used: nothing is sent, nothing is read, and the refusal does not repeat the code, so a mistake does not also copy the secret into captured error output.</summary>
   [Theory]
   [InlineData("enroll", "--server", Server, Code)]
   [InlineData("enroll", "--server", Server, "--code", Code)]
   [InlineData("enroll", "--code", Code)]
   [InlineData("enroll", Code)]
   [InlineData("enrol", "--server", Server, "--code", Code)]
   public async Task CodeOnTheCommandLine_IsRefusedAndNotEchoed(params string[] args)
   {
      using CliFixture cli = new();

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(1, exitCode);
      Assert.Empty(cli.Sent);
      Assert.Equal("", cli.Output);
      Assert.DoesNotContain(Code, cli.Error, StringComparison.Ordinal);
      Assert.Equal(0, cli.LinesRead);
      Assert.Equal(0, cli.KeysRead);
   }



   /// <summary>Makes the given text arrive the named way: in the variable, as the line redirected standard input holds, or as keys typed at a console and ended with Enter.</summary>
   private static void Supply(CliFixture cli, string source, string text)
   {
      switch(source)
      {
         case "variable":
            cli.Variables["QQQ_PRINT_ENROLLMENT_CODE"] = text;
            break;
         case "piped":
            cli.TypedLines.Enqueue(text);
            break;
         default:
            cli.InputRedirected = false;
            cli.Type(text);
            cli.Press(ConsoleKey.Enter, '\r');
            break;
      }
   }
}
