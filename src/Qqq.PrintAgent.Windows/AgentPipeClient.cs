/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Client side of the local administrator pipe: one command per connection to the installed service, after the service has proved who it is.</summary>
public static class AgentPipeClient
{
   private const string PipeName = "QqqPrintAgent";
   private static readonly TimeSpan s_connectLimit = TimeSpan.FromSeconds(3);



   /// <summary>Sends one command to the installed service's pipe and returns its reply.</summary>
   public static async Task<string> SendAsync(PipeRequest request)
      => await SendAsync(request, PipeName).ConfigureAwait(false);



   /// <summary>
   /// Sends one command like <see cref="SendAsync(PipeRequest)"/> and also tells the caller whether the request ever left: a
   /// failure before any request byte was written is thrown as <see cref="AgentPipeNotSentException"/>, and every other
   /// failure is thrown as it is. The companion uses this to say "the service is not reachable" only when nothing was sent,
   /// and "the result could not be confirmed" when the service may have acted.
   /// </summary>
   public static async Task<string> SendReportingPhaseAsync(PipeRequest request)
      => await SendReportingPhaseAsync(request, PipeName, s_connectLimit).ConfigureAwait(false);



   /// <summary>
   /// The phase-reporting send against a named pipe. A test supplies its own clock for the request limit and its own check
   /// of the other end, so it can play a service that accepts the connection and then never reads, or reads the request and
   /// then closes, stalls or answers nonsense, and prove that none of those is reported as "not sent".
   /// </summary>
   internal static async Task<string> SendReportingPhaseAsync(PipeRequest request, string pipeName, TimeSpan connectLimit, TimeProvider? clock = null, Func<SafePipeHandle, IDisposable>? verify = null)
   {
      bool writing = false;
      try
      {
         return await SendAsync(request, pipeName, connectLimit, () => writing = true, clock, verify).ConfigureAwait(false);
      }
      catch(Exception exception) when(!writing && exception is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
      {
         throw new AgentPipeNotSentException(exception);
      }
   }



   /// <summary>
   /// Sends one command to the named pipe. Nothing is written until the process at the other end has been verified as the installed
   /// service. Enrollment, sign-in and sign-out get 150 seconds because they call QQQ; everything else gets 35, and connecting is
   /// limited to 3 unless another limit is given. A request too large for the pipe is refused before a connection is even opened,
   /// and <paramref name="writing"/> is called at the last moment before the first byte is written, which is how the
   /// phase-reporting send knows the request may have left. For tests only, <paramref name="clock"/> measures the request limit
   /// (so a test lets it run out exactly when it means to) and <paramref name="verify"/> replaces the check of the other end.
   /// </summary>
   internal static async Task<string> SendAsync(PipeRequest request, string pipeName, TimeSpan? connectLimit = null, Action? writing = null, TimeProvider? clock = null, Func<SafePipeHandle, IDisposable>? verify = null)
   {
      string command = JsonSerializer.Serialize(request, PrintApiClient.JsonOptions);
      if(command.Length > AgentPipeFraming.MaximumCommandCharacters)
      {
         throw new InvalidDataException("Local pipe message exceeds the permitted size.");
      }
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(request.Operation is "enroll" or "sign-in" or "sign-out" ? 150 : 35), clock ?? TimeProvider.System);
      await using NamedPipeClientStream pipe = CreateClient(pipeName);
      using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
      connectTimeout.CancelAfter(connectLimit ?? s_connectLimit);
      await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
      using IDisposable peer = (verify ?? AgentPipePeer.Installed.Authenticate)(pipe.SafePipeHandle);
      writing?.Invoke();
      await AgentPipeFraming.WriteAsync(pipe, command, AgentPipeFraming.MaximumCommandCharacters, timeout.Token).ConfigureAwait(false);
      return await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumResponseCharacters, timeout.Token).ConfigureAwait(false);
   }



   /// <summary>Opens the client end of the pipe with anonymous impersonation and no handle inheritance, so the service learns nothing about the caller beyond the request and a child process cannot inherit the connection.</summary>
   internal static NamedPipeClientStream CreateClient(string pipeName)
      => new(".", pipeName, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);
}
