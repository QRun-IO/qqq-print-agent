/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the phase-reporting send says about a request that has left. A test pipe server accepts the connection and reads
/// the whole request, so the service may have acted on it; whatever goes wrong afterwards must never be reported as
/// "not sent", because the companion tells the operator nothing happened when it hears that. The check of the other end
/// is replaced, since the test server is not the installed service.
/// </summary>
public sealed class AgentPipePhaseTests
{
   private static readonly TimeSpan s_connectLimit = TimeSpan.FromSeconds(5);



   /// <summary>
   /// The server reads the request and then closes, stalls past the request limit, or answers more than a reply may hold.
   /// Each failure reaches the caller as the exception it is, and none of them as <see cref="AgentPipeNotSentException"/>.
   /// </summary>
   [Theory]
   [InlineData("close", typeof(IOException))]
   [InlineData("stall", typeof(OperationCanceledException))]
   [InlineData("oversize", typeof(InvalidDataException))]
   public async Task FailureAfterTheRequestWasReadIsNeverReportedAsNotSent(string behavior, Type expected)
   {
      string name = "QqqPrintAgent-phase-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
      TimeSpan? requestLimit = string.Equals(behavior, "stall", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(250) : null;

      Task connected = server.WaitForConnectionAsync(timeout.Token);
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(new PipeRequest("enroll", "https://print.example.com", "synthetic-enrollment-code"), name, s_connectLimit, requestLimit, _ => new Verified());
      await connected;
      string request = await AgentPipeFraming.ReadAsync(server, AgentPipeFraming.MaximumCommandCharacters, timeout.Token);
      if(string.Equals(behavior, "close", StringComparison.Ordinal))
      {
         server.Disconnect();
      }
      else if(string.Equals(behavior, "oversize", StringComparison.Ordinal))
      {
         _ = server.WriteAsync(Encoding.UTF8.GetBytes(new string('x', AgentPipeFraming.MaximumResponseCharacters + 1)), timeout.Token).AsTask();
      }
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => reply);

      Assert.Contains("synthetic-enrollment-code", request, StringComparison.Ordinal);
      Assert.IsNotType<AgentPipeNotSentException>(failure);
      Assert.IsAssignableFrom(expected, failure);
   }



   /// <summary>A reply that is not what the operation should answer is still a reply: it is handed back as text for the caller to judge, not turned into "not sent".</summary>
   [Fact]
   public async Task NonsenseReplyIsReturnedAsTheReply()
   {
      string name = "QqqPrintAgent-phase-nonsense-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

      Task connected = server.WaitForConnectionAsync(timeout.Token);
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(new PipeRequest("snapshot"), name, s_connectLimit, requestLimit: null, _ => new Verified());
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
      Task<string> reply = AgentPipeClient.SendReportingPhaseAsync(new PipeRequest("enroll", "https://print.example.com", "synthetic-enrollment-code"), name, s_connectLimit, requestLimit: null, _ => throw refused);
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
