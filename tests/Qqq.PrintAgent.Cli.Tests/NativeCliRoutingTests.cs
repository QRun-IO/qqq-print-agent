/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>With the signed-in service installed, the command-line tool sends its commands to the service over the management pipe instead of using a private device token, and refuses foreground options unless legacy mode is explicitly requested.</summary>
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



   /// <summary>Foreground-only options such as running once are refused before any service call unless legacy compatibility is named, and legacy mode cannot be combined with the service flag. The compatibility check itself is true only when the legacy flag is present.</summary>
   [Fact]
   public async Task ForegroundOptionsRequireExplicitCompatibilityBeforeAnyServiceCall()
   {
      await Assert.ThrowsAsync<ArgumentException>(() => Program.DispatchAsync(["run", "--once"], _ => throw new InvalidOperationException("Must not contact service.")));
      Assert.Throws<ArgumentException>(() => Program.UseLegacyCompatibility(["run", "--legacy-compat", "--service"]));
      Assert.True(Program.UseLegacyCompatibility(["run", "--legacy-compat"]));
      Assert.False(Program.UseLegacyCompatibility(["run"]));
   }
}
