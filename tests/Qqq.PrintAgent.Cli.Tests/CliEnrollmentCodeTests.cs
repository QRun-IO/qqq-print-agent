/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The one-time enrollment code is a secret. It comes from an environment variable or a prompt, never from the command line;
/// it goes to the service in the enrollment request and nowhere else; and nothing the tool prints ever contains it, whether
/// the enrollment works, is refused or cannot be sent.
/// </summary>
public sealed class CliEnrollmentCodeTests
{
   private const string Code = "synthetic-code-4471";
   private const string Server = "https://qqq.example.test";



   /// <summary>With the variable set, the code is taken from QQQ_PRINT_ENROLLMENT_CODE and no prompt is shown and nothing is read from standard input, so an unattended script never blocks.</summary>
   [Fact]
   public async Task CodeInTheEnvironment_IsUsedWithoutPrompting()
   {
      using CliFixture cli = new() { Reply = "Enrolled." };
      cli.Variables["QQQ_PRINT_ENROLLMENT_CODE"] = Code;

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal("QQQ_PRINT_ENROLLMENT_CODE", Assert.Single(cli.VariablesRead));
      Assert.Equal(0, cli.LinesRead);
      Assert.Equal(Code, Assert.Single(cli.Sent).EnrollmentCode);
      Assert.Equal("Enrolled.\n", cli.Output);
   }



   /// <summary>With the variable missing or blank, the tool prompts on standard output and uses the line the operator types, exactly as typed.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("   ")]
   public async Task NoCodeInTheEnvironment_PromptsAndUsesTheTypedLine(string? variable)
   {
      using CliFixture cli = new() { Reply = "Enrolled." };
      cli.Variables["QQQ_PRINT_ENROLLMENT_CODE"] = variable;
      cli.TypedLines.Enqueue(Code);

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(1, cli.LinesRead);
      Assert.Equal(new PipeRequest("enroll", Server, Code), Assert.Single(cli.Sent));
      Assert.Equal("Enrollment code: Enrolled.\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>When no code is available (standard input is closed, or the operator enters a blank line), enrollment fails with one line on standard error and exit code 1, and no request reaches the service.</summary>
   [Theory]
   [InlineData(false, null)]
   [InlineData(true, "")]
   [InlineData(true, "  ")]
   public async Task NoCodeAtAll_FailsWithoutContactingTheService(bool typed, string? line)
   {
      using CliFixture cli = new();
      if(typed)
      {
         cli.TypedLines.Enqueue(line);
      }

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(1, exitCode);
      Assert.Empty(cli.Sent);
      Assert.Equal("Enrollment code: ", cli.Output);
      Assert.Equal("Enrollment code is required.\n", cli.Error);
   }



   /// <summary>The code is never printed, whichever way it arrived and however the enrollment ends: accepted, refused by the service, timed out, or failed to send.</summary>
   [Theory]
   [InlineData(true, "accepted")]
   [InlineData(false, "accepted")]
   [InlineData(true, "refused")]
   [InlineData(false, "refused")]
   [InlineData(true, "timeout")]
   [InlineData(false, "timeout")]
   [InlineData(true, "failure")]
   [InlineData(false, "failure")]
   public async Task Code_NeverAppearsInOutput(bool fromEnvironment, string outcome)
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
      if(fromEnvironment)
      {
         cli.Variables["QQQ_PRINT_ENROLLMENT_CODE"] = Code;
      }
      else
      {
         cli.TypedLines.Enqueue(Code);
      }

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(outcome is "accepted" ? 0 : 1, exitCode);
      Assert.Equal(Code, Assert.Single(cli.Sent).EnrollmentCode);
      Assert.DoesNotContain(Code, cli.Everything, StringComparison.Ordinal);
   }



   /// <summary>A code put on the command line is refused, not used: nothing is sent, and the refusal does not repeat the code, so a mistake does not also copy the secret into captured error output.</summary>
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
   }
}
