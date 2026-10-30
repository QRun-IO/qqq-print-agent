/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public static class AgentPipeClient
{
   public static async Task<string> SendAsync(PipeRequest request)
   {
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(string.Equals(request.Operation, "enroll", StringComparison.Ordinal) ? 150 : 35));
      await using NamedPipeClientStream pipe = new(".", "QqqPrintAgent", PipeDirection.InOut, PipeOptions.Asynchronous);
      using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
      connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
      await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
      await AgentPipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(request, PrintApiClient.JsonOptions), AgentPipeFraming.MaximumCommandCharacters, timeout.Token).ConfigureAwait(false);
      return await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumResponseCharacters, timeout.Token).ConfigureAwait(false);
   }
}



