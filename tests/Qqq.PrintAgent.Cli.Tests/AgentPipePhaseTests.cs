/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the phase-reporting send says about a request that has left. A test pipe server accepts the connection, so the
/// service may have acted on the request; whatever goes wrong from the first byte on must never be reported as
/// "not sent", because the companion tells the operator nothing happened when it hears that. The check of the other end
/// is replaced, since the test server is not the installed service, and no case waits for real time to pass.
/// </summary>
public sealed class AgentPipePhaseTests
{
   private static readonly TimeSpan s_connectLimit = TimeSpan.FromSeconds(5);



   /// <summary>
   /// The server reads the request and then closes, or answers more than a reply may hold. Each failure reaches the
   /// caller as the exception it is, and neither as <see cref="AgentPipeNotSentException"/>.
   /// </summary>
   [Theory]
   [InlineData("close", typeof(IOException))]
   [InlineData("oversize", typeof(InvalidDataException))]
   public async Task FailureAfterTheRequestWasReadIsNeverReportedAsNotSent(string behavior, Type expected)
   {
      string name = "QqqPrintAgent-phase-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

      Task connected = server.WaitForConnectionAsync(timeout.Token);
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(new PipeRequest("enroll", "https://print.example.com", "synthetic-enrollment-code"), name, s_connectLimit, clock: null, _ => new Verified());
      await connected;
      string request = await AgentPipeFraming.ReadAsync(server, AgentPipeFraming.MaximumCommandCharacters, timeout.Token);
      if(string.Equals(behavior, "close", StringComparison.Ordinal))
      {
         server.Disconnect();
      }
      else
      {
         _ = server.WriteAsync(Encoding.UTF8.GetBytes(new string('x', AgentPipeFraming.MaximumResponseCharacters + 1)), timeout.Token).AsTask();
      }
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => reply);

      Assert.Contains("synthetic-enrollment-code", request, StringComparison.Ordinal);
      Assert.IsNotType<AgentPipeNotSentException>(failure);
      Assert.IsAssignableFrom(expected, failure);
   }



   /// <summary>
   /// The server reads the request and then says nothing. The request limit is measured on a manual clock, so the test
   /// does not depend on how fast the machine is: one second before the limit the client is still waiting, and when the
   /// limit runs out the wait ends as a cancellation that is not reported as "not sent". An enrollment gets 150 seconds
   /// because the service calls the server for it; a status request gets 35.
   /// </summary>
   [Theory]
   [InlineData("enroll", 150)]
   [InlineData("snapshot", 35)]
   public async Task ReplyLimitAfterTheRequestWasReadIsNeverReportedAsNotSent(string operation, int limitSeconds)
   {
      string name = "QqqPrintAgent-phase-stall-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
      ManualTimeProvider clock = new();

      Task connected = server.WaitForConnectionAsync(timeout.Token);
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(new PipeRequest(operation, "https://print.example.com", "synthetic-enrollment-code"), name, s_connectLimit, clock, _ => new Verified());
      await connected;
      _ = await AgentPipeFraming.ReadAsync(server, AgentPipeFraming.MaximumCommandCharacters, timeout.Token);
      clock.Advance(TimeSpan.FromSeconds(limitSeconds - 1));
      bool stillWaiting = !reply.IsCompleted;
      clock.Advance(TimeSpan.FromSeconds(1));
      Exception failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reply);

      Assert.True(stillWaiting);
      Assert.IsNotType<AgentPipeNotSentException>(failure);
   }



   /// <summary>
   /// The server accepts the connection and never reads, so the request stalls in the write itself; then the server goes
   /// away. Bytes may have left by then, so the failure of the write must not be reported as "not sent": the request counts
   /// as possibly sent from the moment before its first byte, not from the moment the write has finished. The request is
   /// large enough that the pipe cannot take it without a reader.
   /// </summary>
   [Fact]
   public async Task FailureDuringTheWriteIsNeverReportedAsNotSent()
   {
      string name = "QqqPrintAgent-phase-write-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
      TaskCompletionSource verified = new(TaskCreationOptions.RunContinuationsAsynchronously);
      PipeRequest request = new("enroll", "https://print.example.com", new string('x', 60_000));

      Task connected = server.WaitForConnectionAsync(timeout.Token);
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(request, name, s_connectLimit, clock: null, _ =>
      {
         verified.TrySetResult();
         return new Verified();
      });
      await connected;
      await verified.Task.WaitAsync(timeout.Token);
      server.Disconnect();
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => reply);

      Assert.IsNotType<AgentPipeNotSentException>(failure);
      Assert.IsType<IOException>(failure);
   }



   /// <summary>A reply that is not what the operation should answer is still a reply: it is handed back as text for the caller to judge, not turned into "not sent".</summary>
   [Fact]
   public async Task NonsenseReplyIsReturnedAsTheReply()
   {
      string name = "QqqPrintAgent-phase-nonsense-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

      Task connected = server.WaitForConnectionAsync(timeout.Token);
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(new PipeRequest("snapshot"), name, s_connectLimit, clock: null, _ => new Verified());
      await connected;
      _ = await AgentPipeFraming.ReadAsync(server, AgentPipeFraming.MaximumCommandCharacters, timeout.Token);
      await AgentPipeFraming.WriteAsync(server, "\u0001}{ not an answer", AgentPipeFraming.MaximumResponseCharacters, timeout.Token);

      Assert.Equal("\u0001}{ not an answer", await reply);
   }



   /// <summary>
   /// When the check of the other end fails, the connection exists but no request byte has been written: the failure is
   /// reported as "not sent" with its cause inside, and the server reads nothing at all.
   /// </summary>
   [Fact]
   public async Task FailureBeforeTheFirstByteIsReportedAsNotSent()
   {
      string name = "QqqPrintAgent-phase-unsent-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
      IOException refused = new("The check of the other end failed.");

      Task connected = server.WaitForConnectionAsync(timeout.Token);
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(new PipeRequest("enroll", "https://print.example.com", "synthetic-enrollment-code"), name, s_connectLimit, clock: null, _ => throw refused);
      await connected;
      AgentPipeNotSentException notSent = await Assert.ThrowsAsync<AgentPipeNotSentException>(() => reply);

      Assert.Same(refused, notSent.InnerException);
      Assert.Equal(0, await server.ReadAsync(new byte[1], timeout.Token));
   }



   /// <summary>Stands for a check of the other end that passed; it holds nothing, so there is nothing to release.</summary>
   private sealed class Verified : IDisposable
   {
      /// <summary>Nothing was acquired, so nothing is released.</summary>
      public void Dispose()
      {
      }
   }
}
