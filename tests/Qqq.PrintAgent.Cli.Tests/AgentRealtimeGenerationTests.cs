/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Reconnection behavior of the realtime channel: how long the agent waits between attempts, and that a late failure from an old connection or session cannot disturb a newer one.</summary>
public sealed class AgentRealtimeGenerationTests
{
   /// <summary>The reconnect delay starts at one second (up to 20 percent more with jitter) and is capped by the configured maximum (24 to 30 seconds at the twelfth attempt), so reconnects back off but never wait longer than the server allows.</summary>
   [Theory]
   [InlineData(0, 0, 1000)]
   [InlineData(0, 1, 1200)]
   [InlineData(12, 0, 24000)]
   [InlineData(12, 1, 30000)]
   public void ReconnectJitterRespectsConfiguredHardDelayBounds(int attempt, double sample, int expected)
      => Assert.Equal(TimeSpan.FromMilliseconds(expected), AgentRuntime.ReconnectDelay(new RealtimeSettings(), attempt, sample));



   /// <summary>
   /// A handshake on the old session is held open while the user signs in again. When the old handshake is later refused as
   /// unauthenticated, that late failure must not sign out or block the replacement: the replacement session still opens its own
   /// realtime connection and the agent stays authorized.
   /// </summary>
   [Fact]
   public async Task OldHandshakeDenialCannotBlockReplacementSignedInSession()
   {
      using var fixture = new SessionFixture();
      fixture.SaveSession();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.Session.ConnectRealtime = async _ =>
      {
         entered.TrySetResult();
         await release.Task.WaitAsync(timeout.Token);
         throw new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "Synthetic delayed old handshake rejection.");
      };
      var replacement = new SessionFixture.FakeSession
      {
         ConnectRealtime = async token =>
         {
            replacementEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new OperationCanceledException(token);
         },
      };
      int sessions = 0;
      string pipe = "qqq-generation-" + Guid.NewGuid().ToString("N");
      using var runtime = new AgentRuntime(fixture.Local.DirectoryPath, _ => { }, fixture.Local.Printer, pipeName: pipe,
         pipeFactory: () => new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
         sessionFactory: _ => Interlocked.Increment(ref sessions) == 1 ? fixture.Session : replacement);
      Task running = runtime.RunAsync(timeout.Token);
      try
      {
         await entered.Task.WaitAsync(timeout.Token);
         _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: replacement.ExportCredential()), timeout.Token);
         release.TrySetResult();
         await replacementEntered.Task.WaitAsync(timeout.Token);
         Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      }
      finally
      {
         release.TrySetResult();
         await timeout.CancelAsync();
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
      }
   }
}
