/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The companion asks the service to print a test through the local pipe; this checks the request format carries the chosen media without disturbing older callers.</summary>
public sealed class TestPrintPipeTests
{
   /// <summary>An old test-print request with no media profile reads back with none and serializes without a testProfile member. A request with a ZPL format and a label profile survives a serialize-and-parse round trip unchanged, so new media choices cross the pipe and old messages keep their original shape.</summary>
   [Fact]
   public void TestMedia_RoundTripsThroughLocalPipeRequestWithoutChangingLegacyDefaults()
   {
      const string legacy = "{\"operation\":\"test-print\",\"printerId\":\"queue\",\"format\":\"PDF\"}";
      PipeRequest previous = JsonSerializer.Deserialize<PipeRequest>(legacy, PrintApiClient.JsonOptions)!;
      Assert.Null(previous.TestProfile);
      using var oldBody = JsonDocument.Parse(JsonSerializer.Serialize(previous, PrintApiClient.JsonOptions));
      Assert.False(oldBody.RootElement.TryGetProperty("testProfile", out _));
      PipeRequest selected = previous with { Format = "ZPL", TestProfile = "LABEL_4X6_300" };
      PipeRequest restored = JsonSerializer.Deserialize<PipeRequest>(JsonSerializer.Serialize(selected, PrintApiClient.JsonOptions), PrintApiClient.JsonOptions)!;
      Assert.Equal(selected, restored);
   }
}
