/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class ProtocolFixtureTests
{
   [Fact]
   public void SharedClaimFixture_Deserializes()
   {
      string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "claim.response.json"));
      JobClaim claim = JsonSerializer.Deserialize<JobClaim>(json, PrintApiClient.JsonOptions)!;
      Assert.NotNull(claim.Job);
      Assert.Equal("ZPL", claim.Job.Format);
      Assert.Equal(1, claim.Job.Options.Copies);
      Assert.Equal(64, claim.Job.ContentSha256.Length);
   }

   [Fact]
   public void SharedEnrollmentFixture_Deserializes()
   {
      string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "enroll.response.json"));
      EnrollmentResponse response = JsonSerializer.Deserialize<EnrollmentResponse>(json, PrintApiClient.JsonOptions)!;
      Assert.Equal(1, response.ProtocolVersion);
      Assert.NotEmpty(response.DeviceToken);
   }

   [Fact]
   public void OptionalEventFields_AreOmitted()
   {
      string json = JsonSerializer.Serialize(new PrintEvent("e1", "RECEIVED"), PrintApiClient.JsonOptions);
      Assert.DoesNotContain("null", json, StringComparison.Ordinal);
   }
}

