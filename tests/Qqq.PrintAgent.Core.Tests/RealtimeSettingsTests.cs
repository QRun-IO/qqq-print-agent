/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Text.Json.Nodes;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class RealtimeSettingsTests
{
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

   [Fact]
   public void LargeStagingBudgetAndSupportedConcurrencyAreAccepted()
   {
      var settings = new RealtimeSettings(MaxStagedBytes: 16L * 1024 * 1024 * 1024, MaxConcurrentDownloads: 16, MaxConcurrentPrints: 16, MaxCopies: 32767);
      Assert.Equal(settings, RealtimeSettings.Parse(JsonSerializer.SerializeToElement(settings, PrintApiClient.JsonOptions)));
   }

   [Fact]
   public void InconsistentHeartbeatAndDocumentPolicyIsRejected()
   {
      Assert.Throws<InvalidDataException>(() => new RealtimeSettings(HeartbeatIntervalSeconds: 60, HeartbeatTimeoutSeconds: 30).Validate());
      Assert.Throws<InvalidDataException>(() => new RealtimeSettings(MaxStagedBytes: 10).Validate());
      Assert.Throws<InvalidDataException>(() => new RealtimeSettings(NativeSubmissionTimeoutMillis: 1).Validate());
   }
}
