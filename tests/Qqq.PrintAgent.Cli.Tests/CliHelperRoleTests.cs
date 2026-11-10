/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The three roles other parts of the product start this executable in, each with fixed arguments: the isolated printer
/// worker, the MSI update helper and the browser sign-in callback forwarder. These tests pin which arguments select each
/// role, what is passed on, and how a failure is reported, with fakes standing in for the worker, the installer and the
/// companion. CliProcessTests starts the real executable to prove the same routing in the shipped entry point.
/// </summary>
public sealed class CliHelperRoleTests
{
   private const string Callback = "io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-code-91&state=synthetic-state-17";



   /// <summary>Exactly --printer-worker runs the printer worker once and returns its exit code unchanged. The command line itself writes nothing, because the worker's standard output is a one-line protocol its parent parses.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(1)]
   public async Task PrinterWorker_RunsTheWorkerAndReturnsItsExitCode(int workerExitCode)
   {
      using CliFixture cli = new() { HelperExitCode = workerExitCode };

      int exitCode = await cli.RunAsync("--printer-worker");

      Assert.Equal(workerExitCode, exitCode);
      Assert.Equal(1, cli.WorkerRuns);
      Assert.Equal("", cli.Everything);
      Assert.Empty(cli.Sent);
   }



   /// <summary>A failure inside the printer worker is not caught or printed by the command line: the worker reports its own failures to its parent, and anything else must surface as a crash, not as a line the parent would try to parse.</summary>
   [Fact]
   public async Task PrinterWorker_FailureIsNotCaughtOrPrinted()
   {
      using CliFixture cli = new() { HelperFailure = new InvalidOperationException("Synthetic worker failure.") };

      InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => cli.RunAsync("--printer-worker"));

      Assert.Equal("Synthetic worker failure.", failure.Message);
      Assert.Equal("", cli.Everything);
   }



   /// <summary>The update helper receives exactly the request path it was started with and its exit code is returned unchanged; the command line prints nothing of its own.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(1)]
   [InlineData(1603)]
   public async Task ApplyUpdate_PassesTheRequestPathAndReturnsTheExitCode(int helperExitCode)
   {
      using CliFixture cli = new() { HelperExitCode = helperExitCode };

      int exitCode = await cli.RunAsync("--apply-update", "C:\\synthetic\\updates\\request.json");

      Assert.Equal(helperExitCode, exitCode);
      Assert.Equal("C:\\synthetic\\updates\\request.json", Assert.Single(cli.UpdateRequests));
      Assert.Equal("", cli.Everything);
      Assert.Equal(0, cli.WorkerRuns);
      Assert.Empty(cli.Sent);
   }



   /// <summary>An update helper that throws is reported as one line on standard error and exit code 1.</summary>
   [Fact]
   public async Task ApplyUpdate_FailureIsOneLineOnStandardErrorAndFails()
   {
      using CliFixture cli = new() { HelperFailure = new UnauthorizedAccessException("Synthetic update refusal.") };

      int exitCode = await cli.RunAsync("--apply-update", "C:\\synthetic\\updates\\request.json");

      Assert.Equal(1, exitCode);
      Assert.Equal("", cli.Output);
      Assert.Equal("Synthetic update refusal.\n", cli.Error);
   }



   /// <summary>The sign-in callback address is forwarded exactly as the browser supplied it, without a cancellation deadline of the command line's own, and a delivered callback exits 0 with nothing printed.</summary>
   [Fact]
   public async Task AuthCallback_ForwardsTheAddressUnchangedAndSucceeds()
   {
      using CliFixture cli = new();

      int exitCode = await cli.RunAsync("--auth-callback", Callback);

      Assert.Equal(0, exitCode);
      (string forwarded, CancellationToken token) = Assert.Single(cli.Callbacks);
      Assert.Equal(Callback, forwarded);
      Assert.False(token.CanBeCanceled);
      Assert.Equal("", cli.Everything);
      Assert.Empty(cli.Sent);
   }



   /// <summary>A callback that cannot be delivered exits 1 with the forwarder's reason on standard error, and the address, which carries a one-time authorization code, is not printed.</summary>
   [Fact]
   public async Task AuthCallback_FailureIsReportedWithoutTheAddress()
   {
      using CliFixture cli = new() { HelperFailure = new UnauthorizedAccessException("Browser callbacks require the installed CLI location.") };

      int exitCode = await cli.RunAsync("--auth-callback", Callback);

      Assert.Equal(1, exitCode);
      Assert.Equal("", cli.Output);
      Assert.Equal("Browser callbacks require the installed CLI location.\n", cli.Error);
      Assert.DoesNotContain("synthetic-code-91", cli.Everything, StringComparison.Ordinal);
   }



   /// <summary>A callback hand-off that is canceled or times out exits 1 with the fixed timeout sentence.</summary>
   [Fact]
   public async Task AuthCallback_TimeoutFailsWithTheFixedMessage()
   {
      using CliFixture cli = new() { HelperFailure = new OperationCanceledException("Synthetic hand-off timeout.") };

      int exitCode = await cli.RunAsync("--auth-callback", Callback);

      Assert.Equal(1, exitCode);
      Assert.Equal("The operation timed out or was canceled before completion.\n", cli.Error);
   }



   /// <summary>The role arguments are exact. One argument too many or too few does not start a helper: the command line is refused as an unknown command, and what was typed, which may be a callback address, is not repeated.</summary>
   [Theory]
   [InlineData("--printer-worker", "extra")]
   [InlineData("--printer-worker", "--printer-worker")]
   [InlineData("--Printer-Worker")]
   [InlineData("--apply-update")]
   [InlineData("--apply-update", "C:\\synthetic\\updates\\request.json", "extra")]
   [InlineData("--auth-callback")]
   [InlineData("--auth-callback", Callback, "extra")]
   [InlineData("run", "--printer-worker")]
   public async Task InexactRoleArguments_StartNoHelperAndAreRefused(params string[] args)
   {
      using CliFixture cli = new();

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(1, exitCode);
      Assert.Equal(0, cli.WorkerRuns);
      Assert.Empty(cli.UpdateRequests);
      Assert.Empty(cli.Callbacks);
      Assert.Empty(cli.Sent);
      Assert.Equal("", cli.Output);
      Assert.EndsWith(CommandLine.Usage.ReplaceLineEndings("\n") + "\n", cli.Error.ReplaceLineEndings("\n"), StringComparison.Ordinal);
      Assert.DoesNotContain("synthetic-code-91", cli.Error, StringComparison.Ordinal);
   }
}
