/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>Client and server share sample JSON replies. Parsing the shared samples here proves the client reads exactly what the server project says it sends, and that optional event fields are left out of what the client sends.</summary>
public sealed class ProtocolFixtureTests
{
   /// <summary>The shared claim reply parses into a job with a ZPL format, one copy and a 64-character content hash.</summary>
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



   /// <summary>The shared enrollment reply parses with protocol version 1 and a non-empty device token.</summary>
   [Fact]
   public void SharedEnrollmentFixture_Deserializes()
   {
      string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "enroll.response.json"));
      EnrollmentResponse response = JsonSerializer.Deserialize<EnrollmentResponse>(json, PrintApiClient.JsonOptions)!;
      Assert.Equal(1, response.ProtocolVersion);
      Assert.NotEmpty(response.DeviceToken);
   }



   /// <summary>An event with no message or spooler id serializes without any null members, since the server treats an explicit null differently from an absent field.</summary>
   [Fact]
   public void OptionalEventFields_AreOmitted()
   {
      string json = JsonSerializer.Serialize(new PrintEvent("e1", "RECEIVED"), PrintApiClient.JsonOptions);
      Assert.DoesNotContain("null", json, StringComparison.Ordinal);
   }
}

