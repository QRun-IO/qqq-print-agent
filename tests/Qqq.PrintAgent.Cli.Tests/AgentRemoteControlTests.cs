/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRemoteControlTests
{
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
