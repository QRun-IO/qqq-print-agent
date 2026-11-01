/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Client side of the local administrator pipe: one command per connection to the installed service, after the service has proved who it is.</summary>
public static class AgentPipeClient
{
   /// <summary>Sends one command to the installed service's pipe and returns its reply.</summary>
   public static async Task<string> SendAsync(PipeRequest request)
      => await SendAsync(request, "QqqPrintAgent").ConfigureAwait(false);



   /// <summary>
   /// Sends one command to the named pipe. Nothing is written until the process at the other end has been verified as the installed
   /// service. Enrollment and sign-in get 150 seconds because they call QQQ; everything else gets 35, and connecting is limited to 3.
   /// </summary>
   internal static async Task<string> SendAsync(PipeRequest request, string pipeName)
   {
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(request.Operation is "enroll" or "sign-in" or "sign-out" ? 150 : 35));
      await using NamedPipeClientStream pipe = CreateClient(pipeName);
      using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
      connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
      await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
      using var peer = AgentPipePeer.Authenticate(pipe.SafePipeHandle);
      await AgentPipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(request, PrintApiClient.JsonOptions), AgentPipeFraming.MaximumCommandCharacters, timeout.Token).ConfigureAwait(false);
      return await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumResponseCharacters, timeout.Token).ConfigureAwait(false);
   }



   /// <summary>Opens the client end of the pipe with anonymous impersonation and no handle inheritance, so the service learns nothing about the caller beyond the request and a child process cannot inherit the connection.</summary>
   internal static NamedPipeClientStream CreateClient(string pipeName)
      => new(".", pipeName, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);
}



