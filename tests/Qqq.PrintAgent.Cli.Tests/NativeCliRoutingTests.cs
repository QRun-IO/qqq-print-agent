/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The command-line tool sends its commands to the installed service over the management pipe, and refuses the options of the removed legacy mode instead of running a script written for it against the service.</summary>
public sealed class NativeCliRoutingTests
{
   /// <summary>The printers, run and sign-out commands are each translated into the matching service request (printers, reconnect, sign-out) and succeed with exit code 0, so normal use goes through the service that owns the signed-in session.</summary>
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



   /// <summary>The legacy flag and each option that existed only for it (a separate data directory, run once, the service selector) are refused before any service call, with whatever command they accompany.</summary>
   [Theory]
   [InlineData("run", "--legacy-compat")]
   [InlineData("run", "--once")]
   [InlineData("run", "--legacy-compat", "--once", "--data-dir", "C:\\synthetic")]
   [InlineData("printers", "--data-dir", "C:\\synthetic")]
   [InlineData("enroll", "--service", "--server", "https://qqq.example.test")]
   [InlineData("service-status", "--service")]
   [InlineData("--legacy-compat")]
   public async Task RemovedLegacyOptionsAreRefusedBeforeAnyServiceCall(params string[] args)
   {
      ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => Program.DispatchAsync(args, _ => throw new InvalidOperationException("Must not contact service.")));
      Assert.Contains("removed in 2.0", refusal.Message, StringComparison.Ordinal);
   }
}
