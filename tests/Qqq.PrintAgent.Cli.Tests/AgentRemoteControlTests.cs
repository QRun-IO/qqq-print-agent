/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The server can pause the device, disconnect it, ask it to refresh its printer list, or cancel a job already handed to the
/// Windows spooler. These tests protect the rules that make that safe, on the realtime channel the service uses: a stale or
/// incomplete instruction never resumes a paused device, a cancel is performed at most once however many times it is
/// delivered, and an uncertain result is reported as unknown rather than invented.
/// </summary>
public sealed class AgentRemoteControlTests
{
   private const string Stopped = "Realtime connection stopped";



   /// <summary>
   /// A desired-state frame missing its dispatch flag or its revision is a malformed instruction. It ends the connection as a
   /// protocol violation and the device stays paused at its previous revision, in memory and on disk, so a truncated frame can
   /// never un-pause printing.
   /// </summary>
   [Theory]
   [InlineData("{\"revision\":6,\"connectionState\":\"CONNECTED\"}")]
   [InlineData("{\"dispatchPaused\":false,\"connectionState\":\"CONNECTED\"}")]
   public async Task MissingRequiredStateCannotResumeDispatch(string state)
   {
      await using RealtimeRuntimeFixture fixture = new();
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(5, true, "CONNECTED"));
      using var malformed = JsonDocument.Parse(state);
      await connection.SendAsync("DESIRED_STATE", malformed.RootElement);
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains(Stopped, StringComparison.Ordinal)));
      Assert.Contains(fixture.Logs, line => line.Contains(Stopped + ": JsonException", StringComparison.Ordinal));
      AssertPausedAtRevisionFive(fixture);
   }



   /// <summary>Every command receipt sent to the server, whether produced by a normal run or recovered after an interrupted one, carries a UTC completion time ending in Z and short enough for the server's field, because the server rejects any other form.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task GeneratedAndRecoveredReceiptsUseRequiredUtcWireTimestamp(bool interrupted)
   {
      DeviceControlCommand command = RealtimeRuntimeFixture.Command();
      await using RealtimeRuntimeFixture fixture = new(seed: store =>
      {
         RealtimeRuntimeFixture.SeedSpooledJob(store);
         if(interrupted)
         { Assert.True(store.BeginControlCommand(command)); }
      });
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      if(!interrupted)
      { await connection.SendAsync("COMMAND", command); }
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") > 0);
      foreach(JsonElement receipt in Receipts(fixture))
      {
         string timestamp = receipt.GetProperty("completedAt").GetString()!;
         Assert.EndsWith("Z", timestamp, StringComparison.Ordinal);
         Assert.True(timestamp.Length <= 40);
         Assert.True(DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
      }
   }



   /// <summary>If the cancel was performed but the worker's answer was lost, the outcome is recorded as unknown and a second delivery of the command does not repeat the cancel (one call in total); retrying could cancel something it should not.</summary>
   [Fact]
   public async Task LostNativeCancellationResultStaysUnknownAndNeverRepeats()
   {
      DeviceControlCommand command = RealtimeRuntimeFixture.Command();
      await using RealtimeRuntimeFixture fixture = new(seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      fixture.Printer.LoseCancellationResult = true;
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await connection.SendAsync("COMMAND", command);
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") == 1);
      await connection.SendAsync("COMMAND", command);
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") == 2);
      using AgentStore store = TestState.OpenStore(fixture.DirectoryPath);
      Assert.Equal("OUTCOME_UNKNOWN", store.GetControlReceipt("command")!.Outcome);
      Assert.All(Receipts(fixture), receipt => Assert.Equal("OUTCOME_UNKNOWN", receipt.GetProperty("outcome").GetString()));
      Assert.Equal(1, fixture.Printer.Cancellations);
   }



   /// <summary>
   /// When the server has forgotten an old command (NOT_FOUND for its receipt), a command nine days old is retired locally
   /// without touching the spooler, and its saved receipt is kept. The seven-day window is what separates a pruned record from
   /// a receipt the server simply has not seen yet, and normal command handling continues afterwards on the same connection.
   /// </summary>
   [Fact]
   public async Task PrunedServerReceiptRetiresTransportOnlyAfterSevenDays()
   {
      DeviceControlCommand old = RealtimeRuntimeFixture.Command() with
      {
         CreatedAt = DateTimeOffset.UtcNow.AddDays(-9).ToString("O", CultureInfo.InvariantCulture),
         ExpiresAt = DateTimeOffset.UtcNow.AddDays(-9).AddMinutes(15).ToString("O", CultureInfo.InvariantCulture),
      };
      DeviceControlReceipt receipt = new("receipt", "CANCEL_REQUESTED", old.CreatedAt, "Synthetic committed result.");
      await using RealtimeRuntimeFixture fixture = new(seed: store =>
      {
         Assert.True(store.BeginControlCommand(old));
         store.SaveControlReceipt(old.CommandId, receipt);
      });
      fixture.RefuseWith = message => message.Type is "COMMAND_RECEIPT" ? "NOT_FOUND" : null;
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => PendingReceipts(fixture) == 0);
      using(AgentStore inspected = TestState.OpenStore(fixture.DirectoryPath))
      { Assert.Equal(receipt, inspected.GetControlReceipt(old.CommandId)); }
      Assert.Equal(0, fixture.Printer.Cancellations);
      fixture.RefuseWith = null;
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") == 1);
      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command("REFRESH_INVENTORY", "new-command"));
      await fixture.WaitAsync(() => HasReceipt(fixture, "new-command", "INVENTORY_REFRESHED"));
      Assert.Equal(2, fixture.Count("INVENTORY"));
      Assert.True(fixture.Runtime.GetSnapshot().Connected);
   }



   /// <summary>
   /// NOT_FOUND for the receipt of a current command is an error, not a delivery: the connection ends, the receipt stays
   /// pending, and the cancel itself has run once. On the next connection the same receipt is delivered again and accepted,
   /// still without a second cancel, so a server hiccup cannot make the agent forget what it did or do it twice.
   /// </summary>
   [Fact]
   public async Task FutureReceipt404IsNotTreatedAsDeliveredOrRetired()
   {
      await using RealtimeRuntimeFixture fixture = new(seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      fixture.RefuseWith = message => message.Type is "COMMAND_RECEIPT" ? "NOT_FOUND" : null;
      ScriptedPrintServer.Connection first = await fixture.ConnectAsync();
      await first.SendAsync("COMMAND", RealtimeRuntimeFixture.Command());
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains(Stopped, StringComparison.Ordinal) && line.Contains("NOT_FOUND", StringComparison.Ordinal)));
      Assert.Equal(1, PendingReceipts(fixture));
      Assert.Equal(1, fixture.Printer.Cancellations);
      fixture.RefuseWith = null;
      _ = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => PendingReceipts(fixture) == 0);
      Assert.Equal(1, fixture.Printer.Cancellations);
      Assert.Single(ReceiptMessageIds(fixture).Distinct(StringComparer.Ordinal));
   }



   /// <summary>
   /// The server refuses a receipt and then closes the socket with a close frame. The connection's logged reason is the
   /// refusal, which is what an operator has to act on, and not "Server closed the realtime connection", which only followed
   /// from it. The receipt stays pending and the cancel has run once.
   /// </summary>
   [Fact]
   public async Task RefusedReceiptFollowedByServerCloseIsLoggedAsTheRefusal()
   {
      await using RealtimeRuntimeFixture fixture = new(seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      fixture.RefuseWith = message => message.Type is "COMMAND_RECEIPT" ? "CONFLICT" : null;
      fixture.CloseAfterRefusal = true;
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command());
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains(Stopped, StringComparison.Ordinal)));
      string stopped = fixture.Logs.First(line => line.Contains(Stopped, StringComparison.Ordinal));
      Assert.Contains("Realtime operation was rejected: CONFLICT", stopped, StringComparison.Ordinal);
      Assert.DoesNotContain("Server closed", stopped, StringComparison.Ordinal);
      Assert.Equal(1, PendingReceipts(fixture));
      Assert.Equal(1, fixture.Printer.Cancellations);
   }



   /// <summary>After a pause at revision 5, an older revision (4) or a different instruction claiming the same revision (5, resumed) is refused as a protocol violation that ends the connection, so a delayed or conflicting frame cannot resume a device the operator paused.</summary>
   [Fact]
   public async Task OlderOrConflictingRevisionCannotResumePausedDevice()
   {
      const string regressed = "Desired state revision regressed";
      await using RealtimeRuntimeFixture fixture = new();
      ScriptedPrintServer.Connection first = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(5, true, "CONNECTED"));
      await first.SendAsync("DESIRED_STATE", new DeviceDesiredState(4, false, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Logs.Count(line => line.Contains(regressed, StringComparison.Ordinal)) == 1);
      AssertPausedAtRevisionFive(fixture);
      ScriptedPrintServer.Connection second = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(5, true, "CONNECTED"));
      await second.SendAsync("DESIRED_STATE", new DeviceDesiredState(5, false, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Logs.Count(line => line.Contains(regressed, StringComparison.Ordinal)) == 2);
      AssertPausedAtRevisionFive(fixture);
   }



   /// <summary>
   /// The server never gets the first receipt, the service restarts, and the same command is delivered again. The spooler
   /// cancel still happens exactly once and every resent receipt is identical, down to its message id, which is what makes
   /// redelivery safe.
   /// </summary>
   [Fact]
   public async Task LostReceiptAndDuplicateDeliveryNeverRepeatSpoolCancellation()
   {
      DeviceControlCommand command = RealtimeRuntimeFixture.Command();
      await using RealtimeRuntimeFixture fixture = new(seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      fixture.LoseAcknowledgementOf = message => message.Type is "COMMAND_RECEIPT";
      ScriptedPrintServer.Connection first = await fixture.ConnectAsync();
      await first.SendAsync("COMMAND", command);
      await fixture.WaitAsync(first.LostAcknowledgement.Task);
      fixture.LoseAcknowledgementOf = null;
      await fixture.RestartAsync();
      ScriptedPrintServer.Connection second = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") >= 2 && PendingReceipts(fixture) == 0);
      await second.SendAsync("COMMAND", command);
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") >= 3);
      Assert.Equal(1, fixture.Printer.Cancellations);
      JsonElement[] receipts = Receipts(fixture);
      Assert.All(receipts, value => Assert.Equal(receipts[0].GetRawText(), value.GetRawText()));
      Assert.Single(ReceiptMessageIds(fixture).Distinct(StringComparer.Ordinal));
      Assert.Equal("CANCEL_REQUESTED", receipts[0].GetProperty("outcome").GetString());
   }



   /// <summary>A command that was marked as started but never finished (the service stopped mid-run) is concluded as unknown at the next start with no spooler call, and the job keeps its recorded state; redelivering the command only repeats that receipt. The agent cannot tell whether the cancel happened, so it does not try again.</summary>
   [Fact]
   public async Task InterruptedExecutionMarkerBecomesUnknownWithoutNativeCall()
   {
      DeviceControlCommand command = RealtimeRuntimeFixture.Command();
      await using RealtimeRuntimeFixture fixture = new(seed: store =>
      {
         RealtimeRuntimeFixture.SeedSpooledJob(store);
         Assert.True(store.BeginControlCommand(command));
      });
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => HasReceipt(fixture, "command", "OUTCOME_UNKNOWN"));
      await connection.SendAsync("COMMAND", command);
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") >= 2);
      Assert.Equal(0, fixture.Printer.Cancellations);
      Assert.Single(ReceiptMessageIds(fixture).Distinct(StringComparer.Ordinal));
      using AgentStore inspected = TestState.OpenStore(fixture.DirectoryPath);
      Assert.Equal("OUTCOME_UNKNOWN", inspected.GetControlReceipt("command")!.Outcome);
      Assert.Equal("SPOOLED", inspected.GetState("job"));
   }



   /// <summary>
   /// The spooler cancel fails in a way the command code has no outcome for. The failure ends the connection after the
   /// execution intent was recorded and before any receipt, and the service keeps running. When the server delivers the same
   /// command on the next connection it is not run a second time: the recorded intent without a receipt is answered once with
   /// OUTCOME_UNKNOWN, because the agent cannot tell what the first attempt did to the spooler.
   /// </summary>
   [Fact]
   public async Task CommandThatFailedUnexpectedlyIsAnsweredUnknownAndNeverRunAgainWhenRedelivered()
   {
      DeviceControlCommand command = RealtimeRuntimeFixture.Command();
      await using RealtimeRuntimeFixture fixture = new(seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      fixture.Printer.CancellationFailure = new NotSupportedException("Synthetic unexpected spooler failure.");
      ScriptedPrintServer.Connection first = await fixture.ConnectAsync();
      await first.SendAsync("COMMAND", command);
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains(Stopped, StringComparison.Ordinal) && line.Contains("Synthetic unexpected spooler failure", StringComparison.Ordinal)));
      Assert.Equal(1, fixture.Printer.Cancellations);
      Assert.Equal(0, fixture.Count("COMMAND_RECEIPT"));
      using(AgentStore interrupted = TestState.OpenStore(fixture.DirectoryPath))
      { Assert.Null(interrupted.GetControlReceipt("command")); }
      fixture.Printer.CancellationFailure = null;
      ScriptedPrintServer.Connection second = await fixture.ConnectAsync();
      await second.SendAsync("COMMAND", command);
      await fixture.WaitAsync(() => HasReceipt(fixture, "command", "OUTCOME_UNKNOWN") && PendingReceipts(fixture) == 0);
      Assert.Equal(1, fixture.Printer.Cancellations);
      Assert.Equal(1, fixture.Count("COMMAND_RECEIPT"));
      using AgentStore store = TestState.OpenStore(fixture.DirectoryPath);
      Assert.Contains("No command was automatically repeated", store.GetControlReceipt("command")!.Message, StringComparison.Ordinal);
      Assert.Equal("SPOOLED", store.GetState("job"));
      Assert.False(fixture.Running.IsCompleted);
   }



   /// <summary>An expired command, or one for a job this device does not hold, is answered with EXPIRED or OWNERSHIP_MISMATCH and never reaches Windows, so a late or misdirected cancel cannot affect a printer.</summary>
   [Theory]
   [InlineData(true, "EXPIRED")]
   [InlineData(false, "OWNERSHIP_MISMATCH")]
   public async Task ExpiryAndMissingOwnershipNeverTouchWindows(bool expired, string expected)
   {
      await using RealtimeRuntimeFixture fixture = new();
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command(expired: expired));
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") > 0);
      Assert.True(HasReceipt(fixture, "command", expected));
      using AgentStore store = TestState.OpenStore(fixture.DirectoryPath);
      Assert.Equal(expected, store.GetControlReceipt("command")!.Outcome);
      Assert.Equal(0, fixture.Printer.Cancellations);
   }



   /// <summary>When Windows verifiably refuses the cancel (the spooler job is already gone, or is not ours), the exact reason is reported as the outcome rather than claiming a cancellation that did not occur.</summary>
   [Theory]
   [InlineData("ALREADY_ABSENT")]
   [InlineData("OWNERSHIP_MISMATCH")]
   public async Task VerifiedNativeRefusalIsReportedWithoutInventingCancellation(string outcome)
   {
      await using RealtimeRuntimeFixture fixture = new(seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      fixture.Printer.CancellationOutcome = outcome;
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command());
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") > 0);
      Assert.True(HasReceipt(fixture, "command", outcome));
      Assert.Equal(1, fixture.Printer.Cancellations);
      using AgentStore store = TestState.OpenStore(fixture.DirectoryPath);
      Assert.Equal(outcome, store.GetControlReceipt("command")!.Outcome);
   }



   /// <summary>
   /// While the server says disconnected, an inventory-refresh command is held: no inventory is sent and it gets no receipt,
   /// although a later command on the same connection is answered. The disconnected state survives a restart even when the
   /// first connection attempt fails, and the refresh runs once the server reconnects the device and delivers it again.
   /// </summary>
   [Fact]
   public async Task DisconnectedRefreshWaitsForReconnectAndRestrictionsSurviveRestart()
   {
      DeviceControlCommand refresh = RealtimeRuntimeFixture.Command("REFRESH_INVENTORY");
      await using RealtimeRuntimeFixture fixture = new();
      ScriptedPrintServer.Connection first = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(3, true, "DISCONNECTED"));
      await first.SendAsync("COMMAND", refresh);
      // Commands are handled one at a time in arrival order, so the receipt of this later one proves the refresh was handled.
      await first.SendAsync("COMMAND", RealtimeRuntimeFixture.Command(id: "later", expired: true));
      await fixture.WaitAsync(() => HasReceipt(fixture, "later", "EXPIRED"));
      Assert.Equal(0, fixture.Count("INVENTORY"));
      Assert.Equal(1, fixture.Count("COMMAND_RECEIPT"));
      fixture.HttpFault = _ => new HttpRequestException("Synthetic outage.");
      await fixture.RestartAsync();
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains(Stopped, StringComparison.Ordinal) && line.Contains("Synthetic outage", StringComparison.Ordinal)));
      Assert.Equal("DISCONNECTED", fixture.Runtime.GetSnapshot().Control!.DesiredState.ConnectionState);
      fixture.HttpFault = null;
      ScriptedPrintServer.Connection second = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(4, false, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") == 1);
      await second.SendAsync("COMMAND", refresh);
      await fixture.WaitAsync(() => HasReceipt(fixture, "command", "INVENTORY_REFRESHED"));
      Assert.Equal(2, fixture.Count("INVENTORY"));
      Assert.Equal("INVENTORY_REFRESHED", fixture.Runtime.GetSnapshot().Control!.LastOutcome);
   }



   /// <summary>
   /// When the server withdraws authorization on the realtime channel, printing is blocked and the service stops trying to
   /// connect: the connection loop goes on making its passes as the backoff runs out on the manual clock, and across them it
   /// sends no HTTP request at all. An operator who asks to reconnect is told that a sign-in is required, because a reconnect
   /// cannot repair this, and the status says the same in red. The job journal is kept, and an explicit sign-in restores the
   /// connection. Dispatch is paused by the server throughout, so no claim is in flight to blur the request count.
   /// </summary>
   [Fact]
   public async Task AuthorizationWithdrawnOverRealtimeBlocksPrintingUntilSignInWithoutErasingJournal()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock, seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(1, true, "CONNECTED"));
      await connection.SendAsync("ERROR", new { code = "AUTHENTICATION_REQUIRED", message = "Synthetic session ended." });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains(Stopped, StringComparison.Ordinal)));
      await fixture.BackoffPassAsync(clock);
      int requests = fixture.Requests;
      await fixture.BackoffPassAsync(clock);
      await fixture.BackoffPassAsync(clock);
      Assert.Equal(requests, fixture.Requests);
      Assert.Equal("AUTHORIZATION_REQUIRED", fixture.Runtime.GetTransportSnapshot().State);
      AgentSnapshot blocked = fixture.Runtime.GetSnapshot();
      Assert.False(blocked.Connected);
      Assert.Equal("RED", blocked.Health);
      Assert.Contains("authorization is required", blocked.Status, StringComparison.Ordinal);
      Assert.False(blocked.Authentication!.Authorized);
      Assert.Contains("authorization is required", blocked.Authentication.Message, StringComparison.Ordinal);
      Assert.False(await fixture.Runtime.EnsureAuthorizationAsync(CancellationToken.None));
      Assert.StartsWith("Sign-in is required", await fixture.PipeAsync(new PipeRequest("reconnect")), StringComparison.Ordinal);
      Assert.DoesNotContain("Connection retry requested.", fixture.Logs, StringComparer.Ordinal);
      Assert.False(await fixture.Runtime.EnsureAuthorizationAsync(CancellationToken.None));
      Assert.Equal("AUTHORIZATION_REQUIRED", fixture.Runtime.GetTransportSnapshot().State);
      Assert.Equal("SPOOLED", fixture.State("job"));
      _ = await fixture.Runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: new QqqSessionCredential(fixture.Peer.Origin, "synthetic-session")), CancellationToken.None);
      _ = await fixture.ConnectAsync();
      Assert.True(fixture.Runtime.GetSnapshot().Authentication!.Authorized);
      Assert.Equal("SPOOLED", fixture.State("job"));
   }



   /// <summary>
   /// A control state written by the polling prerelease may say that authorization is required and that the server has no
   /// remote control. 2.0 reads such a file without failing and honors neither: the saved demand does not block traffic, the
   /// support is shown as still being checked, and the first realtime WELCOME puts the current state in its place.
   /// </summary>
   [Fact]
   public async Task ControlStateSavedByThePollingPrereleaseIsReadButNotHonored()
   {
      await using RealtimeRuntimeFixture fixture = new();
      using(AgentStore store = TestState.OpenStore(fixture.DirectoryPath))
      { store.SaveControlState(AgentControlState.Initial); }
      using(var journal = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DirectoryPath, "journal.db") }.ToString()))
      {
         journal.Open();
         using Microsoft.Data.Sqlite.SqliteCommand overwrite = journal.CreateCommand();
         overwrite.CommandText = """UPDATE control_state SET json='{"support":"LEGACY","desiredState":{"revision":0,"dispatchPaused":false,"connectionState":"CONNECTED"},"authorizationRequired":true}'""";
         Assert.Equal(1, overwrite.ExecuteNonQuery());
      }
      await fixture.RestartAsync();
      AgentControlState loaded = fixture.Runtime.GetSnapshot().Control!;
      Assert.DoesNotContain("authorizationRequired", JsonSerializer.Serialize(loaded, PrintApiClient.JsonOptions), StringComparison.OrdinalIgnoreCase);
      Assert.Equal("checking support", AgentControlLabels.Support(loaded.Support));
      _ = await fixture.ConnectAsync();
      AgentSnapshot connected = fixture.Runtime.GetSnapshot();
      Assert.True(connected.Connected);
      Assert.Equal("SUPPORTED", connected.Control!.Support);
      Assert.Contains("Remote controls available.", connected.Status, StringComparison.Ordinal);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>The device must still be paused at revision 5, both in the running service and in what it saved.</summary>
   private static void AssertPausedAtRevisionFive(RealtimeRuntimeFixture fixture)
   {
      DeviceDesiredState live = fixture.Runtime.GetSnapshot().Control!.DesiredState;
      Assert.True(live.DispatchPaused);
      Assert.Equal(5, live.Revision);
      using AgentStore store = TestState.OpenStore(fixture.DirectoryPath);
      DeviceDesiredState saved = store.LoadControlState().DesiredState;
      Assert.True(saved.DispatchPaused);
      Assert.Equal(5, saved.Revision);
   }



   /// <summary>The receipt objects of every COMMAND_RECEIPT message the agent sent, in order.</summary>
   private static JsonElement[] Receipts(RealtimeRuntimeFixture fixture)
      => fixture.Messages.Where(message => message.Type is "COMMAND_RECEIPT").Select(message => message.Payload.GetProperty("receipt")).ToArray();



   /// <summary>The message ids of every COMMAND_RECEIPT message; a replayed receipt must reuse its id so the server recognises it.</summary>
   private static string[] ReceiptMessageIds(RealtimeRuntimeFixture fixture)
      => fixture.Messages.Where(message => message.Type is "COMMAND_RECEIPT").Select(message => message.MessageId).ToArray();



   /// <summary>True when the agent sent a receipt for the command with the given outcome.</summary>
   private static bool HasReceipt(RealtimeRuntimeFixture fixture, string commandId, string outcome)
      => fixture.Messages.Any(message => message.Type is "COMMAND_RECEIPT"
         && string.Equals(message.Payload.GetProperty("commandId").GetString(), commandId, StringComparison.Ordinal)
         && string.Equals(message.Payload.GetProperty("receipt").GetProperty("outcome").GetString(), outcome, StringComparison.Ordinal));



   /// <summary>How many receipts the store still holds for delivery.</summary>
   private static int PendingReceipts(RealtimeRuntimeFixture fixture)
   {
      using AgentStore store = TestState.OpenStore(fixture.DirectoryPath);
      return store.PendingControlReceipts().Count;
   }
}
