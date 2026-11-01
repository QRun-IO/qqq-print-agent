/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRealtimeGenerationTests
{
   [Theory]
   [InlineData(0, 0, 1000)]
   [InlineData(0, 1, 1200)]
   [InlineData(12, 0, 24000)]
   [InlineData(12, 1, 30000)]
   public void ReconnectJitterRespectsConfiguredHardDelayBounds(int attempt, double sample, int expected)
      => Assert.Equal(TimeSpan.FromMilliseconds(expected), AgentRuntime.ReconnectDelay(new RealtimeSettings(), attempt, sample));

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
