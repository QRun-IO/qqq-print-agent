/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Service;

/// <summary>Hosts the same connector runtime used for local diagnostics under Windows Service Control Manager.</summary>
internal sealed partial class PrintService(ILogger<PrintService> logger) : BackgroundService
{
   /// <summary>Keep the connector alive until Windows requests service shutdown.</summary>
   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
      using AgentRuntime runtime = new(AgentStore.DefaultDirectory, message => LogAgentStatus(logger, message));
      await runtime.RunAsync(stoppingToken).ConfigureAwait(false);
   }



   /// <summary>Write connector observations without allocating a logging argument array.</summary>
   [LoggerMessage(Level = LogLevel.Information, Message = "{Message}")]
   private static partial void LogAgentStatus(ILogger logger, string message);
}

