/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Text.Json.Nodes;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>The realtime policy the server sends the agent must be complete and internally consistent: a missing field is an error, supported large values are accepted, and contradictory limits are rejected.</summary>
public sealed class RealtimeSettingsTests
{
   /// <summary>If the server's settings omit a required field (copy limit, submission timeout, download concurrency or heartbeat timeout), parsing fails instead of quietly using a built-in default that the server never chose.</summary>
   [Theory]
   [InlineData("maxCopies")]
   [InlineData("nativeSubmissionTimeoutMillis")]
   [InlineData("maxConcurrentDownloads")]
   [InlineData("heartbeatTimeoutSeconds")]
   public void MissingServerPolicyFieldCannotSilentlyUseCompiledDefault(string field)
   {
      JsonObject node = JsonSerializer.SerializeToNode(new RealtimeSettings(), PrintApiClient.JsonOptions)!.AsObject();
      Assert.True(node.Remove(field));
      Assert.Throws<InvalidDataException>(() => RealtimeSettings.Parse(JsonSerializer.SerializeToElement(node)));
   }



   /// <summary>A 16 GiB staging budget, 16 concurrent downloads and prints and a 32,767-copy limit are accepted and survive a serialize-and-parse round trip unchanged, so supported upper values are not clipped.</summary>
   [Fact]
   public void LargeStagingBudgetAndSupportedConcurrencyAreAccepted()
   {
      var settings = new RealtimeSettings(MaxStagedBytes: 16L * 1024 * 1024 * 1024, MaxConcurrentDownloads: 16, MaxConcurrentPrints: 16, MaxCopies: 32767);
      Assert.Equal(settings, RealtimeSettings.Parse(JsonSerializer.SerializeToElement(settings, PrintApiClient.JsonOptions)));
   }



   /// <summary>Settings that contradict themselves are rejected: a heartbeat timeout shorter than the interval, a staging budget too small to hold a document, and a native submission timeout too short to be usable.</summary>
   [Fact]
   public void InconsistentHeartbeatAndDocumentPolicyIsRejected()
   {
      Assert.Throws<InvalidDataException>(() => new RealtimeSettings(HeartbeatIntervalSeconds: 60, HeartbeatTimeoutSeconds: 30).Validate());
      Assert.Throws<InvalidDataException>(() => new RealtimeSettings(MaxStagedBytes: 10).Validate());
      Assert.Throws<InvalidDataException>(() => new RealtimeSettings(NativeSubmissionTimeoutMillis: 1).Validate());
   }
}
