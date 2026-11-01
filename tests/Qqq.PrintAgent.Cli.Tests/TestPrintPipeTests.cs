/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class TestPrintPipeTests
{
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
