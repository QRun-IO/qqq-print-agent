/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Adds presentation metadata without changing queue identity or the printing target. The operator's display name, description
/// and location come from the store, not from Windows, so they are added to whatever the inner backend read, in each of its three
/// reads alike, and cost no native call.
/// </summary>
internal sealed class ProfiledPrinterBackend(IPrinterBackend inner, AgentStore store) : IPrinterBackend
{
   /// <summary>Returns the inner backend's printers with the operator's display name, description and location added to each; queue identity and the printing target are never changed.</summary>
   public IReadOnlyList<PrinterInfo> Discover() => inner.Discover().Select(WithProfile).ToArray();



   /// <summary>
   /// Returns the inner backend's list without media, with the same display name, description and location added to each. The
   /// worker names a job's printer in the journal from this list, so the operator's name for it has to be on it.
   /// </summary>
   public IReadOnlyList<PrinterInfo> ListWithoutMedia() => inner.ListWithoutMedia().Select(WithProfile).ToArray();



   /// <summary>Returns the inner backend's one printer with the same display name, description and location added, or null when the inner backend has no such printer.</summary>
   public PrinterInfo? FindWithMedia(string printerId) => inner.FindWithMedia(printerId) is { } printer ? WithProfile(printer) : null;



   /// <summary>Passes the staged file and its submission limit straight to the inner backend, adding nothing, since profiles are presentation only.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken token)
      => inner.PrintFileAsync(job, path, submissionTimeout, token);



   /// <summary>The one mapping behind all three reads: the printer as it was read, with what the operator stored for its identifier.</summary>
   private PrinterInfo WithProfile(PrinterInfo printer)
   {
      PrinterProfile profile = store.GetPrinterProfile(printer.PrinterId);
      return printer with { DisplayName = profile.DisplayName, Description = profile.Description, Location = profile.Location };
   }
}
