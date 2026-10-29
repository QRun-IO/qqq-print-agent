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
      try
      {
         IsolatedPrinterBackend printers = new(Environment.ProcessPath ?? throw new InvalidOperationException("Service executable path is unavailable."));
         using AgentRuntime runtime = new(AgentStore.DefaultDirectory, message => LogAgentStatus(logger, message), printers);
         await runtime.RunAsync(stoppingToken).ConfigureAwait(false);
         if(!stoppingToken.IsCancellationRequested)
         {
            throw new InvalidOperationException("The print service runtime stopped unexpectedly.");
         }
      }
      catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
      {
         // An administrator stop, upgrade or Windows shutdown is a normal service exit.
      }
      catch(Exception exception)
      {
         AgentActivityLog.Create(AgentStore.DefaultDirectory).Error("[Service] Fatal runtime failure; Windows recovery may restart the service.", exception);
         // BackgroundService normally stops the host cleanly, which does not trigger SCM recovery.
         Environment.Exit(1);
      }
   }



   /// <summary>Write connector observations without allocating a logging argument array.</summary>
   [LoggerMessage(Level = LogLevel.Information, Message = "{Message}")]
   private static partial void LogAgentStatus(ILogger logger, string message);
}

