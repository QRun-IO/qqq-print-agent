/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>The job state names are wire values: they must be exactly what the shared protocol fixtures carry, and the rule for which states end a job must hold for each of them.</summary>
public sealed class PrintJobStateTests
{
   /// <summary>The state names equal the status values in the shared event and claim fixtures, so a renamed constant cannot drift from the contract unnoticed.</summary>
   [Theory]
   [InlineData("claim.response.json", PrintJobState.Claimed)]
   [InlineData("received.event.json", PrintJobState.Received)]
   [InlineData("submitting.event.json", PrintJobState.Submitting)]
   [InlineData("spooled.event.json", PrintJobState.Spooled)]
   public void StateNamesAreTheWireValuesOfTheSharedFixtures(string fixture, string state)
   {
      using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)));
      JsonElement holder = document.RootElement.TryGetProperty("job", out JsonElement job) ? job : document.RootElement;
      Assert.Equal(state, holder.GetProperty("status").GetString());
   }



   /// <summary>A job is finished, and never worked on again, once it is spooled, completed, failed or of unknown outcome; while claimed, received or submitting it is not, and neither is a job the journal has never seen.</summary>
   [Theory]
   [InlineData(PrintJobState.Spooled, true)]
   [InlineData(PrintJobState.SpoolerCompleted, true)]
   [InlineData(PrintJobState.Failed, true)]
   [InlineData(PrintJobState.OutcomeUnknown, true)]
   [InlineData(PrintJobState.Claimed, false)]
   [InlineData(PrintJobState.Received, false)]
   [InlineData(PrintJobState.Submitting, false)]
   [InlineData("spooled", false)]
   [InlineData(null, false)]
   public void OnlyConcludedStatesAreFinal(string? state, bool final)
      => Assert.Equal(final, PrintJobState.IsFinal(state));



   /// <summary>The remaining names, which no shared fixture carries as an event, are spelled as the contract's status list spells them.</summary>
   [Fact]
   public void RemainingStateNamesMatchTheContract()
   {
      Assert.Equal("SPOOLER_COMPLETED", PrintJobState.SpoolerCompleted);
      Assert.Equal("FAILED", PrintJobState.Failed);
      Assert.Equal("OUTCOME_UNKNOWN", PrintJobState.OutcomeUnknown);
   }
}
