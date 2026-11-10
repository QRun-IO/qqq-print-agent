/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The service's connection loop and management pipe in one running runtime against a scripted server: it recovers from a
/// network timeout as soon as it is asked to reconnect, serves job actions over the pipe, and keeps serving after a client misbehaves.
/// </summary>
public sealed class AgentRuntimeTests
{
   /// <summary>
   /// The first server request times out, which leaves the connection loop waiting out a 60-second backoff. A reconnect request
   /// over the pipe wakes the loop at once, so the service connects long before that backoff (which is longer than the
   /// fixture's whole time limit) could have elapsed. The pipe then handles the job actions below, and a client that
   /// disconnects halfway through a command does not stop the service or its next command.
   /// </summary>
   [Fact]
   public async Task Reconnect_WakesTheConnectionLoopAfterHttpTimeout_AndRetainsPipeAfterClientDisconnect()
   {
      int requests = 0;
      var settings = new RealtimeSettings(Revision: 1, ReconnectMinDelayMillis: 60000, ReconnectMaxDelayMillis: 60000, ReconnectJitterPercent: 0);
      await using RealtimeRuntimeFixture fixture = new(settings: settings, cacheSettings: true,
         seed: store => store.SetExposure("other", false, new RealtimeRuntimeFixture.SyntheticFilePrinter().Discover()),
         httpFault: _ => Interlocked.Increment(ref requests) == 1 ? new TaskCanceledException("simulated timeout") : null);
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal) && line.Contains("simulated timeout", StringComparison.Ordinal)));
      Assert.False(fixture.Runtime.GetSnapshot().Connected);
      string result = await fixture.PipeAsync(new PipeRequest("reconnect"));
      Assert.StartsWith("Reconnect requested", result, StringComparison.Ordinal);
      _ = await fixture.ConnectAsync();
      Assert.True(fixture.Runtime.GetSnapshot().Connected);
      await VerifyJobActionsAsync(fixture);
      await DisconnectMidCommandAsync(fixture.PipeName);
      string status = await fixture.PipeAsync(new PipeRequest("status"));
      Assert.Contains("Connected", status, StringComparison.Ordinal);
      Assert.False(fixture.Running.IsCompleted);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// A test print is refused until the printer is shared. A failed job turns health red and its details show the failure text;
   /// acknowledging it returns health to green without changing the delivery record; a shared printer's test print is queued on the server.
   /// </summary>
   private static async Task VerifyJobActionsAsync(RealtimeRuntimeFixture fixture)
   {
      string denied = await fixture.PipeAsync(new PipeRequest("test-print", PrinterId: "other", Format: "PDF"));
      Assert.StartsWith("Error: Share the printer", denied, StringComparison.Ordinal);
      Assert.Empty(fixture.OperatorActions);
      using(AgentStore store = new(fixture.DirectoryPath))
      {
         store.SetExposure("other", true, fixture.Printer.Discover());
         store.Record("failed-job", new PrintEvent("test-failed", "FAILED", "Synthetic failure", null));
      }
      Assert.Equal("RED", fixture.Runtime.GetSnapshot().Health);
      string details = await fixture.PipeAsync(new PipeRequest("job-details", JobId: "failed-job"));
      Assert.Contains("Synthetic failure", details, StringComparison.Ordinal);
      string acknowledged = await fixture.PipeAsync(new PipeRequest("acknowledge-job", JobId: "failed-job"));
      Assert.StartsWith("Job acknowledged", acknowledged, StringComparison.Ordinal);
      Assert.Equal("GREEN", fixture.Runtime.GetSnapshot().Health);
      Assert.Equal("FAILED", fixture.State("failed-job"));
      string queued = await fixture.PipeAsync(new PipeRequest("test-print", PrinterId: "other", Format: "PDF"));
      Assert.StartsWith("Queued on the server", queued, StringComparison.Ordinal);
      Assert.EndsWith("/device/test-print", Assert.Single(fixture.OperatorActions), StringComparison.Ordinal);
   }



   /// <summary>Writes half a command (invalid JSON, no newline) and drops the connection, to prove an abandoned pipe client cannot wedge the service.</summary>
   private static async Task DisconnectMidCommandAsync(string name)
   {
      using CancellationTokenSource guard = new(TimeSpan.FromSeconds(30));
      await using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(guard.Token);
      await pipe.WriteAsync(Encoding.UTF8.GetBytes("{invalid"), guard.Token);
      await pipe.FlushAsync(guard.Token);
   }
}
