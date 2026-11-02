/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Adds presentation metadata without changing queue identity or the printing target.</summary>
internal sealed class ProfiledPrinterBackend(IPrinterBackend inner, AgentStore store) : IFilePrinterBackend
{
   /// <summary>Returns the inner backend's printers with the operator's display name, description and location added to each; queue identity and the printing target are never changed.</summary>
   public IReadOnlyList<PrinterInfo> Discover() => inner.Discover().Select(printer =>
   {
      PrinterProfile profile = store.GetPrinterProfile(printer.PrinterId);
      return printer with { DisplayName = profile.DisplayName, Description = profile.Description, Location = profile.Location };
   }).ToArray();



   /// <summary>Passes the document straight to the inner backend, adding nothing, since profiles are presentation only.</summary>
   public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken token)
      => inner.PrintAsync(job, content, token);



   /// <summary>Passes a staged file to the inner backend, or throws when that backend cannot take files, so a missing capability is an error and not a silent fallback.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken token)
      => inner is IFilePrinterBackend files ? files.PrintFileAsync(job, path, token)
         : throw new NotSupportedException("The printer backend does not support file submissions.");



   /// <summary>Same pass-through with an explicit native submission time limit.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken token)
      => inner is IFilePrinterBackend files ? files.PrintFileAsync(job, path, submissionTimeout, token)
         : throw new NotSupportedException("The printer backend does not support file submissions.");
}
