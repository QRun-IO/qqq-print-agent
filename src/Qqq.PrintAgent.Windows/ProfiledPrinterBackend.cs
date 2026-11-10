/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Adds presentation metadata without changing queue identity or the printing target.</summary>
internal sealed class ProfiledPrinterBackend(IPrinterBackend inner, AgentStore store) : IPrinterBackend
{
   /// <summary>Returns the inner backend's printers with the operator's display name, description and location added to each; queue identity and the printing target are never changed.</summary>
   public IReadOnlyList<PrinterInfo> Discover() => inner.Discover().Select(printer =>
   {
      PrinterProfile profile = store.GetPrinterProfile(printer.PrinterId);
      return printer with { DisplayName = profile.DisplayName, Description = profile.Description, Location = profile.Location };
   }).ToArray();



   /// <summary>Passes the staged file and its submission limit straight to the inner backend, adding nothing, since profiles are presentation only.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken token)
      => inner.PrintFileAsync(job, path, submissionTimeout, token);
}
