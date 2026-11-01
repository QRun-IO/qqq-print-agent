namespace Qqq.PrintAgent.UiTests;

public sealed class TransportFormattingTests
{
   [Theory]
   [InlineData(double.NaN)]
   [InlineData(double.PositiveInfinity)]
   [InlineData(-1d)]
   public void InvalidMeasurements_DoNotAppearAsZero(double value)
   {
      Assert.Equal("Unavailable", TransportFormatting.Bytes(value));
      Assert.Equal("Unavailable", TransportFormatting.Rate(value));
   }

   [Fact]
   public void ByteAndRateUnits_AreDistinctAndSupportLargeTransfers()
   {
      Assert.Equal("0 B/s", TransportFormatting.Rate(0));
      Assert.Equal("1 MiB", TransportFormatting.Bytes(1024 * 1024));
      Assert.Equal("1 GiB/s", TransportFormatting.Rate(1024d * 1024 * 1024));
      Assert.EndsWith(" EiB", TransportFormatting.Bytes(long.MaxValue), StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("http://localhost:18081/path?token=private", "localhost:18081")]
   [InlineData("https://print.example.com/", "print.example.com")]
   [InlineData("https://user:password@print.example.com/", "Connection settings")]
   [InlineData("not an address", "Connection settings")]
   public void ServerLabel_ShowsOnlySafeAuthority(string address, string expected)
      => Assert.Equal(expected, TransportFormatting.Server(address));

   [Fact]
   public void MissingOrLegacyTransport_DoesNotPretendWebSocketIsConnected()
   {
      Assert.Equal("WebSocket unavailable", TransportFormatting.State(null, "CONNECTED"));
      Assert.Equal("WebSocket unavailable", TransportFormatting.State("HTTP", "CONNECTED"));
      Assert.Equal("WebSocket reconnecting (WSS)", TransportFormatting.State("WSS", "RECONNECTING"));
      Assert.Equal("WebSocket sign-in required (WS)", TransportFormatting.State("WS", "AUTHORIZATION_REQUIRED"));
   }
}
