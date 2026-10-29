/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Service;
using Qqq.PrintAgent.Windows;

if(args.Length == 1 && string.Equals(args[0], "--printer-worker", StringComparison.Ordinal))
{
   return await PrinterWorkerHost.RunAsync().ConfigureAwait(false);
}

if(args.Length == 1 && string.Equals(args[0], "--remove-startup-tasks", StringComparison.Ordinal))
{
   try
   {
      await AgentStartup.RemoveInstalledTasksAsync().ConfigureAwait(false);
      return 0;
   }
   catch(Exception exception)
   {
      AgentActivityLog.Create(AgentStore.DefaultDirectory).Error("[Installer] Startup task cleanup failed.", exception);
      return 1;
   }
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "QqqPrintAgent");
builder.Services.AddHostedService<PrintService>();
await builder.Build().RunAsync();
return 0;

