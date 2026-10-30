/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Adds presentation metadata without changing queue identity or the printing target.</summary>
internal sealed class ProfiledPrinterBackend(IPrinterBackend inner, AgentStore store) : IPrinterBackend
{
   public IReadOnlyList<PrinterInfo> Discover() => inner.Discover().Select(printer =>
   {
      PrinterProfile profile = store.GetPrinterProfile(printer.PrinterId);
      return printer with { DisplayName = profile.DisplayName, Description = profile.Description, Location = profile.Location };
   }).ToArray();

   public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken token)
      => inner.PrintAsync(job, content, token);
}
