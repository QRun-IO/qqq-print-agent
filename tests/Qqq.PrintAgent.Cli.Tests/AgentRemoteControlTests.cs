/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The server can pause the device, disconnect it, ask it to refresh its printer list, or cancel a job already handed to the
/// Windows spooler. These tests protect the rules that make that safe: a stale or incomplete instruction never resumes a paused
/// device, a cancel is performed at most once however many times it is delivered, and an uncertain result is reported as
/// unknown rather than invented.
/// </summary>
public sealed class AgentRemoteControlTests
{
   /// <summary>A desired-state reply missing its dispatch flag or its revision is a malformed instruction. It is rejected and the device stays paused at its previous revision, so a truncated reply can never un-pause printing.</summary>
   [Theory]
   [InlineData("{\"revision\":6,\"connectionState\":\"CONNECTED\"}")]
   [InlineData("{\"dispatchPaused\":false,\"connectionState\":\"CONNECTED\"}")]
   public async Task MissingRequiredStateCannotResumeDispatch(string state)
   {
      using ControlFixture fixture = new();
      fixture.Desired = new DeviceDesiredState(5, true, "CONNECTED");
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      fixture.PollBody = "{\"controlVersion\":1,\"serverTime\":\"2026-10-31T00:00:00Z\",\"pollIntervalSeconds\":5,\"commands\":[],\"desiredState\":" + state + "}";
      await Assert.ThrowsAsync<JsonException>(() => runtime.PollControlsOnceAsync(CancellationToken.None));
      Assert.True(runtime.GetSnapshot().Control!.DesiredState.DispatchPaused);
      Assert.Equal(5, runtime.GetSnapshot().Control!.DesiredState.Revision);
   }



   /// <summary>Every command receipt sent to the server, whether produced by a normal run or recovered after an interrupted one, carries a UTC completion time ending in Z and short enough for the server's field, because the server rejects any other form.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task GeneratedAndRecoveredReceiptsUseRequiredUtcWireTimestamp(bool interrupted)
   {
      using ControlFixture fixture = new();
      fixture.SeedSpooledJob();
      fixture.Commands = [ControlFixture.Command()];
      if(interrupted)
      {
         using AgentStore store = new(fixture.DirectoryPath);
         Assert.True(store.BeginControlCommand(fixture.Commands[0]));
      }
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.NotEmpty(fixture.Receipts);
      foreach(string body in fixture.Receipts)
      {
         using var json = JsonDocument.Parse(body);
         string timestamp = json.RootElement.GetProperty("completedAt").GetString()!;
         Assert.EndsWith("Z", timestamp, StringComparison.Ordinal);
         Assert.True(timestamp.Length <= 40);
         Assert.True(DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _));
      }
   }



   /// <summary>If the cancel was performed but the worker's answer was lost, the outcome is recorded as unknown and later polls do not repeat the cancel (one call in total); retrying could cancel something it should not.</summary>
   [Fact]
   public async Task LostNativeCancellationResultStaysUnknownAndNeverRepeats()
   {
      using ControlFixture fixture = new();
      fixture.SeedSpooledJob();
      fixture.Commands = [ControlFixture.Command()];
      fixture.Printer.LoseCancellationResult = true;
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      using AgentStore store = new(fixture.DirectoryPath);
      Assert.Equal("OUTCOME_UNKNOWN", store.GetControlReceipt("command")!.Outcome);
      Assert.Equal(1, fixture.Printer.Cancellations);
   }



   /// <summary>
   /// When the server has forgotten an old command (404 on its receipt), a command nine days old is retired locally without
   /// touching the spooler, and its saved receipt is kept. The seven-day window is what separates a pruned record from a receipt
   /// the server simply has not seen yet, and normal command handling continues afterwards.
   /// </summary>
   [Fact]
   public async Task PrunedServerReceiptRetiresTransportOnlyAfterSevenDays()
   {
      using ControlFixture fixture = new();
      DeviceControlCommand old = ControlFixture.Command() with
      {
         CreatedAt = DateTimeOffset.UtcNow.AddDays(-9).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
         ExpiresAt = DateTimeOffset.UtcNow.AddDays(-9).AddMinutes(15).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
      };
      DeviceControlReceipt receipt = new("receipt", "CANCEL_REQUESTED", old.CreatedAt, "Synthetic committed result.");
      using(AgentStore store = new(fixture.DirectoryPath))
      {
         Assert.True(store.BeginControlCommand(old));
         store.SaveControlReceipt(old.CommandId, receipt);
      }
      fixture.ReceiptStatus = HttpStatusCode.NotFound;
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      using AgentStore inspected = new(fixture.DirectoryPath);
      Assert.Empty(inspected.PendingControlReceipts());
      Assert.Equal(receipt, inspected.GetControlReceipt(old.CommandId));
      Assert.Equal(0, fixture.Printer.Cancellations);
      fixture.Commands = [ControlFixture.Command("REFRESH_INVENTORY", "new-command")];
      fixture.ReceiptStatus = HttpStatusCode.OK;
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal(1, fixture.Inventories);
   }



   /// <summary>A 404 for a receipt of a current command is an error, not a delivery: the receipt stays pending to be retried (the cancel itself has already run once), so a server hiccup cannot make the agent forget what it did.</summary>
   [Fact]
   public async Task FutureReceipt404IsNotTreatedAsDeliveredOrRetired()
   {
      using ControlFixture fixture = new();
      fixture.SeedSpooledJob();
      fixture.Commands = [ControlFixture.Command()];
      fixture.ReceiptStatus = HttpStatusCode.NotFound;
      using AgentRuntime runtime = fixture.Runtime();
      await Assert.ThrowsAsync<HttpRequestException>(() => runtime.PollControlsOnceAsync(CancellationToken.None));
      using AgentStore store = new(fixture.DirectoryPath);
      Assert.Single(store.PendingControlReceipts());
      Assert.Equal(1, fixture.Printer.Cancellations);
   }



   /// <summary>After a pause at revision 5, an older revision (4) or a different instruction claiming the same revision (5, resumed) is refused as invalid data, so a delayed or conflicting reply cannot resume a device the operator paused.</summary>
   [Fact]
   public async Task OlderOrConflictingRevisionCannotResumePausedDevice()
   {
      using ControlFixture fixture = new();
      fixture.Desired = new DeviceDesiredState(5, true, "CONNECTED");
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      fixture.Desired = new DeviceDesiredState(4, false, "CONNECTED");
      await Assert.ThrowsAsync<InvalidDataException>(() => runtime.PollControlsOnceAsync(CancellationToken.None));
      fixture.Desired = new DeviceDesiredState(5, false, "CONNECTED");
      await Assert.ThrowsAsync<InvalidDataException>(() => runtime.PollControlsOnceAsync(CancellationToken.None));
      Assert.True(runtime.GetSnapshot().Control!.DesiredState.DispatchPaused);
   }



   /// <summary>
   /// The server never gets the first receipt, the agent restarts, and the same command is delivered again. The spooler cancel
   /// still happens exactly once and every resent receipt is identical, which is what makes redelivery safe.
   /// </summary>
   [Fact]
   public async Task LostReceiptAndDuplicateDeliveryNeverRepeatSpoolCancellation()
   {
      using ControlFixture fixture = new();
      fixture.SeedSpooledJob();
      fixture.Commands = [ControlFixture.Command()];
      fixture.ReceiptFailures = 1;
      using(AgentRuntime first = fixture.Runtime())
      {
         await Assert.ThrowsAsync<HttpRequestException>(() => first.PollControlsOnceAsync(CancellationToken.None));
      }
      using AgentRuntime restarted = fixture.Runtime();
      await restarted.PollControlsOnceAsync(CancellationToken.None);
      await restarted.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal(1, fixture.Printer.Cancellations);
      Assert.True(fixture.Receipts.Count >= 3);
      Assert.All(fixture.Receipts, value => Assert.Equal(fixture.Receipts[0], value));
      Assert.Equal("CANCEL_REQUESTED", JsonSerializer.Deserialize<DeviceControlReceipt>(fixture.Receipts[0], PrintApiClient.JsonOptions)!.Outcome);
   }



   /// <summary>A command that was marked as started but never finished (the agent stopped mid-run) is concluded as unknown on restart with no spooler call, and the job keeps its recorded state; the agent cannot tell whether the cancel happened, so it does not try again.</summary>
   [Fact]
   public async Task InterruptedExecutionMarkerBecomesUnknownWithoutNativeCall()
   {
      using ControlFixture fixture = new();
      fixture.SeedSpooledJob();
      fixture.Commands = [ControlFixture.Command()];
      using(AgentStore store = new(fixture.DirectoryPath))
      { Assert.True(store.BeginControlCommand(fixture.Commands[0])); }
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal(0, fixture.Printer.Cancellations);
      using AgentStore inspected = new(fixture.DirectoryPath);
      Assert.Equal("OUTCOME_UNKNOWN", inspected.GetControlReceipt("command")!.Outcome);
      Assert.Equal("SPOOLED", inspected.GetState("job"));
   }



   /// <summary>An expired command, or one for a job this device does not hold, is answered with EXPIRED or OWNERSHIP_MISMATCH and never reaches Windows, so a late or misdirected cancel cannot affect a printer.</summary>
   [Theory]
   [InlineData(true, "EXPIRED")]
   [InlineData(false, "OWNERSHIP_MISMATCH")]
   public async Task ExpiryAndMissingOwnershipNeverTouchWindows(bool expired, string expected)
   {
      using ControlFixture fixture = new();
      fixture.Commands = [ControlFixture.Command(expired: expired)];
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      using AgentStore store = new(fixture.DirectoryPath);
      Assert.Equal(expected, store.GetControlReceipt("command")!.Outcome);
      Assert.Equal(0, fixture.Printer.Cancellations);
   }



   /// <summary>When Windows verifiably refuses the cancel (the spooler job is already gone, or is not ours), the exact reason is reported as the outcome rather than claiming a cancellation that did not occur.</summary>
   [Theory]
   [InlineData("ALREADY_ABSENT")]
   [InlineData("OWNERSHIP_MISMATCH")]
   public async Task VerifiedNativeRefusalIsReportedWithoutInventingCancellation(string outcome)
   {
      using ControlFixture fixture = new();
      fixture.SeedSpooledJob();
      fixture.Commands = [ControlFixture.Command()];
      fixture.Printer.CancellationOutcome = outcome;
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      using AgentStore store = new(fixture.DirectoryPath);
      Assert.Equal(outcome, store.GetControlReceipt("command")!.Outcome);
   }



   /// <summary>
   /// While the server says disconnected, an inventory-refresh command is held, with no inventory sent and no receipt. The
   /// disconnected state survives a restart even when the next poll fails, and the held refresh runs once the server reconnects the device.
   /// </summary>
   [Fact]
   public async Task DisconnectedRefreshWaitsForReconnectAndRestrictionsSurviveRestart()
   {
      using ControlFixture fixture = new();
      fixture.Desired = new DeviceDesiredState(3, true, "DISCONNECTED");
      fixture.Commands = [ControlFixture.Command("REFRESH_INVENTORY")];
      using(AgentRuntime first = fixture.Runtime())
      { await first.PollControlsOnceAsync(CancellationToken.None); }
      Assert.Equal(0, fixture.Inventories);
      Assert.Empty(fixture.Receipts);
      using AgentRuntime restarted = fixture.Runtime();
      fixture.PollStatus = HttpStatusCode.NotFound;
      await Assert.ThrowsAsync<InvalidDataException>(() => restarted.PollControlsOnceAsync(CancellationToken.None));
      Assert.Equal("DISCONNECTED", restarted.GetSnapshot().Control!.DesiredState.ConnectionState);
      fixture.PollStatus = HttpStatusCode.OK;
      fixture.Desired = new DeviceDesiredState(4, false, "CONNECTED");
      await restarted.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal(1, fixture.Inventories);
      Assert.Equal("INVENTORY_REFRESHED", restarted.GetSnapshot().Control!.LastOutcome);
   }



   /// <summary>
   /// A 401 on control polling marks the device as needing re-authorization, persists across a restart without further polling,
   /// and keeps the job journal. Enrolling under a new device identity clears the denial and starts a fresh journal scope, after
   /// which control is supported again.
   /// </summary>
   [Fact]
   public async Task AuthDenialPersistsUntilNewEnrollmentScopeWithoutErasingJournal()
   {
      using ControlFixture fixture = new();
      fixture.SeedSpooledJob();
      fixture.PollStatus = HttpStatusCode.Unauthorized;
      using(AgentRuntime first = fixture.Runtime())
      { await first.PollControlsOnceAsync(CancellationToken.None); }
      using(AgentRuntime restarted = fixture.Runtime())
      {
         await restarted.PollControlsOnceAsync(CancellationToken.None);
         Assert.True(restarted.GetSnapshot().Control!.AuthorizationRequired);
         Assert.Equal(1, fixture.Polls);
      }
      using AgentStore store = new(fixture.DirectoryPath);
      Assert.Equal("SPOOLED", store.GetState("job"));
      store.SaveConfiguration(new AgentConfiguration("https://synthetic.invalid", "new-device", "new-synthetic-token"));
      Assert.False(store.LoadControlState().AuthorizationRequired);
      Assert.Null(store.GetState("job"));
      fixture.PollStatus = HttpStatusCode.OK;
      using AgentRuntime reenrolled = fixture.Runtime();
      await reenrolled.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal("SUPPORTED", reenrolled.GetSnapshot().Control!.Support);
   }



   /// <summary>A 404 on the control endpoint means the server predates remote control, so the device records LEGACY support and keeps printing normally.</summary>
   [Fact]
   public async Task Legacy404ExplicitlyAllowsNormalPrinting()
   {
      using ControlFixture fixture = new();
      fixture.PollStatus = HttpStatusCode.NotFound;
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal("LEGACY", runtime.GetSnapshot().Control!.Support);
   }
}
