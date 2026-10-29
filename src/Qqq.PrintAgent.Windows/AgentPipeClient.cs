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
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
      await using NamedPipeClientStream pipe = new(".", "QqqPrintAgent", PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
      await using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
      using StreamReader reader = new(pipe, leaveOpen: true);
      await writer.WriteLineAsync(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions)).ConfigureAwait(false);
      return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? "Service closed the connection.";
   }
}



