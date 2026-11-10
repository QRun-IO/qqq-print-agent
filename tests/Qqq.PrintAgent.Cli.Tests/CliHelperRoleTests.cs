/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The two roles other parts of the product start this executable in, each with fixed arguments: the MSI update helper
/// and the browser sign-in callback forwarder. These tests pin which arguments select each role, what is passed on, and
/// how a failure is reported, with fakes standing in for the installer and the companion. The executable is not a printer
/// worker; the service starts its own executable for that, and CliUsageTests holds --printer-worker to an unknown command.
/// </summary>
public sealed class CliHelperRoleTests
{
   private const string Callback = "io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-code-91&state=synthetic-state-17";



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
   [InlineData("--apply-update")]
   [InlineData("--apply-update", "C:\\synthetic\\updates\\request.json", "extra")]
   [InlineData("--Apply-Update", "C:\\synthetic\\updates\\request.json")]
   [InlineData("--auth-callback")]
   [InlineData("--auth-callback", Callback, "extra")]
   [InlineData("run", "--apply-update", "C:\\synthetic\\updates\\request.json")]
   public async Task InexactRoleArguments_StartNoHelperAndAreRefused(params string[] args)
   {
      using CliFixture cli = new();

      int exitCode = await cli.RunAsync(args);

      Assert.Equal(1, exitCode);
      Assert.Empty(cli.UpdateRequests);
      Assert.Empty(cli.Callbacks);
      Assert.Empty(cli.Sent);
      Assert.Equal("", cli.Output);
      Assert.EndsWith(CommandLine.Usage.ReplaceLineEndings("\n") + "\n", cli.Error.ReplaceLineEndings("\n"), StringComparison.Ordinal);
      Assert.DoesNotContain("synthetic-code-91", cli.Error, StringComparison.Ordinal);
   }
}
