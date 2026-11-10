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
   /// <summary>The shared claim reply, read the way the API client reads a claim, yields a job with a ZPL format, one copy and a 64-character content hash.</summary>
   [Fact]
   public void SharedClaimFixture_Deserializes()
   {
      PrintJob? job = PrintJobContract.ReadClaim(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "claim.response.json")));
      Assert.NotNull(job);
      Assert.Equal("ZPL", job.Format);
      Assert.Equal(1, job.Options.Copies);
      Assert.Equal(64, job.ContentSha256.Length);
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



   /// <summary>The shared PRINTERS frame (printing extension 2.4, printer-approval-v1) parses into the per-printer approval states the runtime applies: one approved, one known but not approved.</summary>
   [Fact]
   public void SharedPrintersFrameFixture_Deserializes()
   {
      string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "realtime-printers.message.json"));
      RealtimeEnvelope envelope = JsonSerializer.Deserialize<RealtimeEnvelope>(json, PrintApiClient.JsonOptions)!;
      Assert.Equal("PRINTERS", envelope.Type);
      PrinterApprovalState[] printers = envelope.Payload.GetProperty("printers").Deserialize<PrinterApprovalState[]>(PrintApiClient.JsonOptions)!;
      Assert.Equal(2, printers.Length);
      Assert.Contains(printers, printer => string.Equals(printer.PrinterId, "laser-example", StringComparison.Ordinal) && printer.Enabled);
      Assert.Contains(printers, printer => string.Equals(printer.PrinterId, "label-example", StringComparison.Ordinal) && !printer.Enabled);
   }



   /// <summary>An event with no message or spooler id serializes without any null members, since the server treats an explicit null differently from an absent field.</summary>
   [Fact]
   public void OptionalEventFields_AreOmitted()
   {
      string json = JsonSerializer.Serialize(new PrintEvent("e1", "RECEIVED"), PrintApiClient.JsonOptions);
      Assert.DoesNotContain("null", json, StringComparison.Ordinal);
   }
}

