/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.UiTests;

/// <summary>How the footer turns raw transport numbers and addresses into text: unusable values read "Unavailable", units are binary and distinct, the server label never exposes credentials, and a missing transport is never shown as a live WebSocket.</summary>
public sealed class TransportFormattingTests
{
   /// <summary>NaN, infinity and negative measurements display as "Unavailable" for both byte totals and rates, so a bad reading is never presented as a believable zero.</summary>
   [Theory]
   [InlineData(double.NaN)]
   [InlineData(double.PositiveInfinity)]
   [InlineData(-1d)]
   public void InvalidMeasurements_DoNotAppearAsZero(double value)
   {
      Assert.Equal("Unavailable", TransportFormatting.Bytes(value));
      Assert.Equal("Unavailable", TransportFormatting.Rate(value));
   }



   /// <summary>Totals use units like MiB and rates add "/s" (0 B/s, 1 GiB/s), and a value as large as a 64-bit maximum still formats in EiB, so very large transfers neither overflow nor lose their unit.</summary>
   [Fact]
   public void ByteAndRateUnits_AreDistinctAndSupportLargeTransfers()
   {
      Assert.Equal("0 B/s", TransportFormatting.Rate(0));
      Assert.Equal("1 MiB", TransportFormatting.Bytes(1024 * 1024));
      Assert.Equal("1 GiB/s", TransportFormatting.Rate(1024d * 1024 * 1024));
      Assert.EndsWith(" EiB", TransportFormatting.Bytes(long.MaxValue), StringComparison.Ordinal);
   }



   /// <summary>The footer shows only the host and port of the server address: paths and query strings (which could hold a token) are dropped, and an address with embedded credentials or that is not an address at all becomes the neutral "Connection settings".</summary>
   [Theory]
   [InlineData("http://localhost:18081/path?token=private", "localhost:18081")]
   [InlineData("https://print.example.com/", "print.example.com")]
   [InlineData("https://user:password@print.example.com/", "Connection settings")]
   [InlineData("not an address", "Connection settings")]
   public void ServerLabel_ShowsOnlySafeAuthority(string address, string expected)
      => Assert.Equal(expected, TransportFormatting.Server(address));



   /// <summary>No transport report, or an HTTP-only one, reads "WebSocket unavailable" even if the state says CONNECTED; real WebSocket states keep their secure/plain label and state wording, such as reconnecting or sign-in required.</summary>
   [Fact]
   public void MissingOrLegacyTransport_DoesNotPretendWebSocketIsConnected()
   {
      Assert.Equal("WebSocket unavailable", TransportFormatting.State(null, "CONNECTED"));
      Assert.Equal("WebSocket unavailable", TransportFormatting.State("HTTP", "CONNECTED"));
      Assert.Equal("WebSocket reconnecting (WSS)", TransportFormatting.State("WSS", "RECONNECTING"));
      Assert.Equal("WebSocket sign-in required (WS)", TransportFormatting.State("WS", "AUTHORIZATION_REQUIRED"));
   }
}
