/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Operator commands. Each one becomes exactly one request to the installed service over its pipe; the reply is printed as
/// the service wrote it; and the exit code tells a script whether the service did what was asked. The pipe is a fake here,
/// so these tests need no installed service.
/// </summary>
public sealed class CliServiceCommandTests
{
   private const string SignOutNote = "The identity provider's browser session is not ended from the command line; use Sign out in the QQQ Print Agent app to end it too.";



   /// <summary>A command without options sends one request that carries only its operation name (run asks for a reconnect, service-status for the status), prints the reply on standard output and exits 0.</summary>
   [Theory]
   [InlineData("printers", "printers")]
   [InlineData("run", "reconnect")]
   [InlineData("service-status", "status")]
   [InlineData("snapshot", "snapshot")]
   public async Task CommandWithoutOptions_SendsItsOperationAndPrintsTheReply(string command, string operation)
   {
      using CliFixture cli = new() { Reply = "Synthetic service accepted the request." };

      int exitCode = await cli.RunAsync(command);

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest(operation), Assert.Single(cli.Sent));
      Assert.Equal("Synthetic service accepted the request.\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>Signing out sends the sign-out operation and, when the service accepts it, adds one line saying the identity provider's browser session is still open, because only the companion can end that.</summary>
   [Fact]
   public async Task SignOut_SendsTheOperationAndSaysTheProviderSessionRemains()
   {
      using CliFixture cli = new() { Reply = "Signed out." };

      int exitCode = await cli.RunAsync("sign-out");

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest("sign-out"), Assert.Single(cli.Sent));
      Assert.Equal("Signed out.\n" + SignOutNote + "\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>Sharing or hiding a printer sends the printer and the choice, and nothing else. The two options may come in either order, and true or false is accepted in any letter case (PowerShell writes $true as "True").</summary>
   [Theory]
   [InlineData(true, "expose", "--printer", "synthetic-queue-1", "--exposed", "true")]
   [InlineData(false, "expose", "--printer", "synthetic-queue-1", "--exposed", "false")]
   [InlineData(true, "expose", "--printer", "synthetic-queue-1", "--exposed", "True")]
   [InlineData(false, "expose", "--printer", "synthetic-queue-1", "--exposed", "FALSE")]
   [InlineData(true, "expose", "--exposed", "TRUE", "--printer", "synthetic-queue-1")]
   [InlineData(false, "expose", "--exposed", "False", "--printer", "synthetic-queue-1")]
   public async Task Expose_SendsThePrinterAndTheChoice(bool exposed, params string[] args)
   {
      using CliFixture cli = new() { Reply = "{\"status\":\"Connected\"}" };

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest("set-exposure", PrinterId: "synthetic-queue-1", Exposed: exposed), Assert.Single(cli.Sent));
      Assert.Equal("{\"status\":\"Connected\"}\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>Enrolling sends the server address and the enrollment code together in one request, and nothing else.</summary>
   [Fact]
   public async Task Enroll_SendsTheServerAndTheCode()
   {
      using CliFixture cli = new() { Reply = "Enrolled." };
      cli.Variables[CommandLine.EnrollmentCodeVariable] = "synthetic-code-4471";

      int exitCode = await cli.RunAsync("enroll", "--server", "https://qqq.example.test");

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest("enroll", "https://qqq.example.test", "synthetic-code-4471"), Assert.Single(cli.Sent));
      Assert.Equal("Enrolled.\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>A reply that begins with "Error:" means the service refused or failed the request. It is still printed on standard output as the service wrote it, and every command then exits 1, so a script cannot take a refusal for success. Signing out adds no note when it did not happen.</summary>
   [Theory]
   [InlineData("printers")]
   [InlineData("run")]
   [InlineData("sign-out")]
   [InlineData("service-status")]
   [InlineData("snapshot")]
   [InlineData("expose", "--printer", "synthetic-queue-1", "--exposed", "true")]
   [InlineData("enroll", "--server", "https://qqq.example.test")]
   public async Task ErrorReply_IsPrintedAndFailsTheCommand(params string[] args)
   {
      using CliFixture cli = new() { Reply = "Error: The synthetic service refused." };
      cli.Variables[CommandLine.EnrollmentCodeVariable] = "synthetic-code-4471";

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(1, exitCode);
      Assert.Single(cli.Sent);
      Assert.Equal("Error: The synthetic service refused.\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>Only a reply that starts with "Error:" in exactly that form is a failure; the word appearing later in a reply, or in another letter case, is ordinary text.</summary>
   [Theory]
   [InlineData("Last error: none.")]
   [InlineData("error: lower case is not the service's refusal form")]
   [InlineData("")]
   public async Task ReplyThatOnlyMentionsAnError_IsNotAFailure(string reply)
   {
      using CliFixture cli = new() { Reply = reply };

      int exitCode = await cli.RunAsync("service-status");

      Assert.Equal(0, exitCode);
      Assert.Equal(reply + "\n", cli.Output);
   }



   /// <summary>When the service cannot be reached or is not the installed service, the reason is one line on standard error, standard output stays empty and the exit code is 1.</summary>
   [Fact]
   public async Task PipeFailure_IsOneLineOnStandardErrorAndFails()
   {
      using CliFixture cli = new() { SendFailure = new UnauthorizedAccessException("The local endpoint is not the installed Windows print service. No request was sent.") };

      int exitCode = await cli.RunAsync("snapshot");

      Assert.Equal(1, exitCode);
      Assert.Equal("", cli.Output);
      Assert.Equal("The local endpoint is not the installed Windows print service. No request was sent.\n", cli.Error);
   }



   /// <summary>A request that times out or is canceled exits 1 with a fixed sentence, never 0 and never an unhandled exception, so an operation that did not finish is not mistaken for one that succeeded.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task TimeoutOrCancellation_FailsWithAFixedMessage(bool taskCanceled)
   {
      using CliFixture cli = new() { SendFailure = taskCanceled ? new TaskCanceledException("Synthetic request timeout.") : new OperationCanceledException("Synthetic cancellation.") };

      int exitCode = await cli.RunAsync("run");

      Assert.Equal(1, exitCode);
      Assert.Equal("", cli.Output);
      Assert.Equal("The operation timed out or was canceled before completion.\n", cli.Error);
   }
}
