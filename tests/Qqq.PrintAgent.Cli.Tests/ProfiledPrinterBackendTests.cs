/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The wrapper that adds the operator's display name, description and location to what the printers report. The service
/// puts it between the exposure filter and the printers, so each of the three reads passes through it, and the print
/// worker names a job's printer in the journal from what comes out. The store is a temporary one and the printers are a
/// fake that has no queue behind it.
/// </summary>
public sealed class ProfiledPrinterBackendTests
{
   /// <summary>The media profile the first of the two fake queues reports.</summary>
   private static readonly PrinterMediaProfile Media = new("default-id", new string('a', 64), "Default", 101600, 152400, 300, 300);



   /// <summary>
   /// Each of the three reads carries the stored profile of each printer it reports, and changes nothing else about it:
   /// the full discovery and the lookup keep the media profiles the printers reported, and the list without media stays
   /// without them. A printer with no stored profile is reported with none. The inner printers are asked by the read that
   /// was asked for and by no other.
   /// </summary>
   [Fact]
   public void EveryReadCarriesTheOperatorsProfileAndChangesNothingElse()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-profiled-" + Guid.NewGuid().ToString("N"));
      try
      {
         using JournalStore store = TestState.OpenStore(directory);
         store.SavePrinterProfile("described", new PrinterProfile("Shipping labels", "The label printer by the door", "Dock 2"));
         TwoPrinters inner = new();
         ProfiledPrinterBackend backend = new(inner, store);

         IReadOnlyList<PrinterInfo> discovered = backend.Discover();
         Assert.Equal((1, 0, 0), (inner.Discoveries, inner.Listings, inner.Lookups));
         IReadOnlyList<PrinterInfo> listed = backend.ListWithoutMedia();
         Assert.Equal((1, 1, 0), (inner.Discoveries, inner.Listings, inner.Lookups));
         PrinterInfo found = Assert.IsType<PrinterInfo>(backend.FindWithMedia("described"));
         PrinterInfo plain = Assert.IsType<PrinterInfo>(backend.FindWithMedia("plain"));
         Assert.Equal((1, 1, 2), (inner.Discoveries, inner.Listings, inner.Lookups));

         foreach(PrinterInfo described in new[] { discovered[0], listed[0], found })
         {
            Assert.Equal(("described", "Queue one", "Driver", "USB001", "READY"), (described.PrinterId, described.Name, described.DriverName, described.PortName, described.Status));
            Assert.Equal(["PDF"], described.Formats, StringComparer.Ordinal);
            Assert.Equal(("Shipping labels", "The label printer by the door", "Dock 2"), (described.DisplayName, described.Description, described.Location));
         }
         foreach(PrinterInfo undescribed in new[] { discovered[1], listed[1], plain })
         {
            Assert.Equal(("plain", "Queue two"), (undescribed.PrinterId, undescribed.Name));
            Assert.Equal((null, null, null), (undescribed.DisplayName, undescribed.Description, undescribed.Location));
         }
         Assert.Equal(Media, Assert.Single(discovered[0].MediaProfiles!));
         Assert.Equal(Media, Assert.Single(found.MediaProfiles!));
         Assert.Null(listed[0].MediaProfiles);
         Assert.Null(listed[1].MediaProfiles);
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>A printer the inner backend does not have is absent, whether or not a profile was once stored for its identifier.</summary>
   [Fact]
   public void PrinterTheInnerBackendDoesNotHaveIsAbsent()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-profiled-" + Guid.NewGuid().ToString("N"));
      try
      {
         using JournalStore store = TestState.OpenStore(directory);
         store.SavePrinterProfile("removed", new PrinterProfile("Old label printer"));
         ProfiledPrinterBackend backend = new(new TwoPrinters(), store);
         Assert.Null(backend.FindWithMedia("removed"));
         Assert.Null(backend.FindWithMedia("never-seen"));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>Two ready PDF queues, "described" (with one media profile) and "plain", that count how often each read was asked for and never print.</summary>
   private sealed class TwoPrinters : IPrinterBackend
   {
      /// <summary>How often the full discovery was asked for.</summary>
      internal int Discoveries { get; private set; }



      /// <summary>How often the list without media was asked for.</summary>
      internal int Listings { get; private set; }



      /// <summary>How often one queue was looked up.</summary>
      internal int Lookups { get; private set; }



      /// <summary>The two queues as a read that asked their drivers reports them.</summary>
      private static PrinterInfo[] Queues => [new("described", "Queue one", "Driver", "USB001", "READY", ["PDF"], MediaProfiles: [Media]), new("plain", "Queue two", "Driver", "USB002", "READY", ["PDF"], MediaProfiles: [])];



      /// <summary>Counts the call and reports both queues with their media.</summary>
      public IReadOnlyList<PrinterInfo> Discover()
      {
         Discoveries++;
         return Queues;
      }



      /// <summary>Counts the call and reports both queues without their media.</summary>
      public IReadOnlyList<PrinterInfo> ListWithoutMedia()
      {
         Listings++;
         return [.. Queues.Select(queue => queue with { MediaProfiles = null })];
      }



      /// <summary>Counts the call and returns the queue with that identifier, or null.</summary>
      public PrinterInfo? FindWithMedia(string printerId)
      {
         Lookups++;
         return Queues.SingleOrDefault(queue => string.Equals(queue.PrinterId, printerId, StringComparison.Ordinal));
      }



      /// <summary>Always throws: these tests never submit a document.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
         => throw new InvalidOperationException("These tests never print.");
   }
}
