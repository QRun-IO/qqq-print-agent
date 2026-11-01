/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Changes of state while the realtime channel is live: enrolling a new device resets what the old one negotiated, and an instruction that arrives before a state acknowledgement is not lost.</summary>
public sealed class AgentRealtimeTransitionTests
{
   /// <summary>After enrolling a new device while the service keeps running, the next realtime handshake reports settings revision 0 (the new pairing's own) instead of the old pairing's revision 5, and the stored settings and device id belong to the replacement device, so the new device is not judged against policy negotiated by the old one.</summary>
   [Fact]
   public async Task NewPairingStartsWithItsOwnSettingsRevisionWithoutServiceRestart()
   {
      await using var fixture = new RealtimeRuntimeFixture(settings: new RealtimeSettings(Revision: 5));
      _ = await fixture.ConnectAsync();
      fixture.Settings = fixture.Settings with { Revision = 0 };
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      _ = await fixture.Runtime.EnrollWithGatesAsync(new PipeRequest("enroll", fixture.Peer.Origin.AbsoluteUri, "synthetic-code"), timeout.Token);
      _ = await fixture.ConnectAsync();
      Assert.Equal(0, fixture.LastHelloSettingsRevision);
      Assert.True(fixture.Runtime.GetSnapshot().Authentication!.Authorized);
      using var store = new AgentStore(fixture.DirectoryPath);
      Assert.Equal("replacement-device", store.LoadConfiguration()!.DeviceId);
      Assert.Equal(0, store.LoadRealtimeSettings().Revision);
   }



   /// <summary>A command that arrives while the server is still holding the acknowledgement of a state change is kept, and runs (a receipt is sent) as soon as that acknowledgement is released, with no HTTP polling needed to retrigger it.</summary>
   [Fact]
   public async Task CommandWaitingForStateAcknowledgementRunsAsSoonAsNegotiationCompletes()
   {
      await using var fixture = new RealtimeRuntimeFixture();
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.StateAppliedRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, true, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Messages.Any(message => message.Type is "STATE_APPLIED" && message.Payload.GetProperty("lastAppliedRevision").GetInt64() == 2));
      await connection.SendAsync("COMMAND", new DeviceControlCommand("refresh-waiting", "request", "REFRESH_INVENTORY", RealtimeRuntimeFixture.Now(), DateTimeOffset.UtcNow.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
      await Task.Delay(50);
      fixture.StateAppliedRelease.TrySetResult();
      await fixture.WaitAsync(() => fixture.Messages.Any(message => message.Type is "COMMAND_RECEIPT"));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }
}
