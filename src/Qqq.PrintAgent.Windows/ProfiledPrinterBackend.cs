/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Adds presentation metadata without changing queue identity or the printing target.</summary>
internal sealed class ProfiledPrinterBackend(IPrinterBackend inner, AgentStore store) : IFilePrinterBackend
{
   public IReadOnlyList<PrinterInfo> Discover() => inner.Discover().Select(printer =>
   {
      PrinterProfile profile = store.GetPrinterProfile(printer.PrinterId);
      return printer with { DisplayName = profile.DisplayName, Description = profile.Description, Location = profile.Location };
   }).ToArray();

   public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken token)
      => inner.PrintAsync(job, content, token);

   public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken token)
      => inner is IFilePrinterBackend files ? files.PrintFileAsync(job, path, token)
         : throw new NotSupportedException("The printer backend does not support file submissions.");

   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken token)
      => inner is IFilePrinterBackend files ? files.PrintFileAsync(job, path, submissionTimeout, token)
         : throw new NotSupportedException("The printer backend does not support file submissions.");
}
