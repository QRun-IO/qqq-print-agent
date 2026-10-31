/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public static class AgentPipeClient
{
   public static async Task<string> SendAsync(PipeRequest request)
      => await SendAsync(request, "QqqPrintAgent").ConfigureAwait(false);

   internal static async Task<string> SendAsync(PipeRequest request, string pipeName)
   {
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(string.Equals(request.Operation, "enroll", StringComparison.Ordinal) ? 150 : 35));
      await using NamedPipeClientStream pipe = CreateClient(pipeName);
      using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
      connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
      await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
      using var peer = AgentPipePeer.Authenticate(pipe.SafePipeHandle);
      await AgentPipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(request, PrintApiClient.JsonOptions), AgentPipeFraming.MaximumCommandCharacters, timeout.Token).ConfigureAwait(false);
      return await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumResponseCharacters, timeout.Token).ConfigureAwait(false);
   }

   internal static NamedPipeClientStream CreateClient(string pipeName)
      => new(".", pipeName, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);
}



