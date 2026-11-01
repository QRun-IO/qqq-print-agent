/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Checks the real session/realtime handshake against controlled discovery so additive media support cannot disconnect older servers.</summary>
public sealed class AgentRealtimeMediaCapabilitiesTests
{
   /// <summary>Reads actual HELLO frames before and after reconnect, proving each socket refreshes discovery once without HTTP polling.</summary>
   [Theory]
   [InlineData("2.0.0", false)]
   [InlineData("2.1.9", false)]
   [InlineData("2.2.0", true)]
   [InlineData("2.3.0", true)]
   public async Task HelloAdvertisesMediaOnlyAfterStandardDiscoveryConfirmsSupport(string version, bool media)
   {
      await using var fixture = new RealtimeRuntimeFixture(printingVersion: version);
      RealtimeRuntimeFixture.Connection first = await fixture.ConnectAsync();
      string[] expected = media ? ["realtime-v1", "REFRESH_INVENTORY", "CANCEL_SPOOL", "printer-media-v1"]
         : ["realtime-v1", "REFRESH_INVENTORY", "CANCEL_SPOOL"];
      Assert.Equal(expected, fixture.LastHelloCapabilities);
      // One initial authentication discovery, then one fresh discovery per socket.
      Assert.Equal(2, fixture.DiscoveryRequests);
      first.Abort();
      _ = await fixture.ConnectAsync();
      Assert.Equal(expected, fixture.LastHelloCapabilities);
      Assert.Equal(3, fixture.DiscoveryRequests);
      Assert.Equal(0, fixture.ForbiddenPolls);
      Assert.Equal(0, fixture.Printer.Prints);
   }



   /// <summary>Changes a running server's version and proves capabilities change only on the next socket, with the existing session retained.</summary>
   [Theory]
   [InlineData("2.0.0", "2.2.0", true)]
   [InlineData("2.2.0", "2.1.0", false)]
   public async Task SameSessionReconnectTracksServerUpgradeAndDowngrade(string initial, string replacement, bool mediaAfter)
   {
      await using var fixture = new RealtimeRuntimeFixture(printingVersion: initial);
      RealtimeRuntimeFixture.Connection first = await fixture.ConnectAsync();
      Assert.Equal(!mediaAfter, fixture.LastHelloCapabilities.Contains("printer-media-v1", StringComparer.Ordinal));
      Assert.Equal(2, fixture.DiscoveryRequests);
      fixture.PrintingVersion = replacement;
      await first.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, true, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Control!.DesiredState.Revision == 2);
      Assert.Equal(2, fixture.DiscoveryRequests);
      first.Abort();
      _ = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(2, true, "CONNECTED"));
      Assert.Equal(mediaAfter, fixture.LastHelloCapabilities.Contains("printer-media-v1", StringComparer.Ordinal));
      Assert.Equal(3, fixture.DiscoveryRequests);
      Assert.Equal(0, fixture.ForbiddenPolls);
      Assert.Equal(0, fixture.Printer.Prints);
   }



   /// <summary>Rejects ambiguous or nonstable discovery claims even when a test transport bypasses the production discovery validator.</summary>
   [Theory]
   [InlineData("2.2.0-rc.1")]
   [InlineData("2.2")]
   [InlineData("2.2.0.1")]
   [InlineData("02.2.0")]
   [InlineData("3.0.0")]
   public void UnverifiedSemanticVersionsCannotClaimMedia(string version)
   {
      var discovery = new QqqApiDiscovery { Extensions = [new("printing", version, "/qbit/printing")] };
      Assert.False(discovery.SupportsPrinterMedia());
      Assert.False(new QqqApiDiscovery().SupportsPrinterMedia());
      Assert.False(new QqqApiDiscovery { Extensions = [new("printing", "2.2.0", "/wrong")] }.SupportsPrinterMedia());
   }
}
