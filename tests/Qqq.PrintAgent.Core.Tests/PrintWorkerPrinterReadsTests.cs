/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// Which printers one pass of the worker asks about, and when. A pass lists the printers before it claims, with the
/// read that asks no printer for its settings, and after the download it looks up the claimed job's own printer, once,
/// before SUBMITTING is recorded. It never runs the full discovery, which reads every printer's settings and so fails
/// when any one of them cannot be read. The printing platform is a hand-written fake that counts each read separately.
/// </summary>
public sealed class PrintWorkerPrinterReadsTests
{
   /// <summary>The worker's refusal of a job whose printer is gone, is not shared any more or no longer offers the job's format.</summary>
   private const string Unavailable = "Assigned printer or document format is unavailable.";



   /// <summary>A synthetic document.</summary>
   private static readonly byte[] Document = [1, 2, 3, 4, 5];



   /// <summary>
   /// The case the three reads exist for. The station has three shared printers, and the settings of one that the job is
   /// not for cannot be read: the full discovery fails, and so does a lookup of that printer. The job for another
   /// printer is claimed, rechecked and spooled all the same, in the order the reliability rule fixes: the list, the
   /// claim, RECEIVED, the download, the lookup of the job's own printer, SUBMITTING, the print, SPOOLED. The full
   /// discovery is never run, and the only printer looked up is the job's.
   /// </summary>
   [Fact]
   public async Task JobIsClaimedRecheckedAndSpooledWhileAnotherPrintersSettingsCannotBeRead()
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      printer.Printers.Insert(0, new PrinterInfo("unreadable", "Queue zero", "Stuck driver", "USB000", "READY", ["PDF", "ZPL"]));
      printer.Printers.Add(new PrinterInfo("printer2", "Queue two", "Synthetic driver", "USB002", "READY", ["PDF"]));
      harness.Exposed.UnionWith(["unreadable", "printer2"]);
      List<string> steps = [];
      printer.Discovering = () => throw new TimeoutException("Synthetic: one printer's driver never answers.");
      printer.Listing = () => steps.Add("list");
      printer.Finding = id =>
      {
         steps.Add("look up " + id);
         if(string.Equals(id, "unreadable", StringComparison.Ordinal))
         { throw new TimeoutException("Synthetic: one printer's driver never answers."); }
      };
      printer.Printing = (_, _, _) =>
      {
         steps.Add("print");
         return Task.FromResult("spool-1");
      };
      server.Arriving = (kind, _) =>
      {
         if(kind is "claim" or "content")
         { steps.Add(kind); }
         return Task.CompletedTask;
      };
      journal.Recorded = (_, recorded) => steps.Add(recorded.Status);
      server.Offer(FakePrintServer.Job(Document), Document);

      Assert.True(await harness.RunOnceAsync());

      Assert.Equal(["list", "claim", "RECEIVED", "content", "look up printer1", "SUBMITTING", "print", "SPOOLED"], steps, StringComparer.Ordinal);
      Assert.Equal((0, 1), (printer.Discoveries, printer.Listings));
      Assert.Equal(["printer1"], printer.Lookups, StringComparer.Ordinal);
      Assert.Equal(["RECEIVED", "SUBMITTING", "SPOOLED"], server.States, StringComparer.Ordinal);
      Assert.Equal("spool-1", journal.Last("job-1").SpoolerJobId);
      Assert.Equal("Queue one", journal.Jobs["job-1"].PrinterName);
   }



   /// <summary>A pass that finds nothing to claim lists the printers once and asks nothing else: no full discovery and no lookup.</summary>
   [Fact]
   public async Task IdlePassListsOnceAndAsksNothingElse()
   {
      await using var harness = new WorkerHarness();
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(1, harness.Server.Claims);
      Assert.Equal((0, 1), (harness.Printer.Discoveries, harness.Printer.Listings));
      Assert.Empty(harness.Printer.Lookups);
   }



   /// <summary>
   /// A platform that cannot list its printers at all stops the pass before it claims: the failure comes out of the pass,
   /// no claim is made, nothing is recorded and nothing is sent, so the job stays with the server for a later pass.
   /// </summary>
   [Fact]
   public async Task PlatformThatCannotListItsPrintersStopsThePassBeforeTheClaim()
   {
      await using var harness = new WorkerHarness();
      harness.Server.Offer(FakePrintServer.Job(Document), Document);
      harness.Printer.Listing = () => throw new IOException("Synthetic: the spooler cannot list its queues.");

      IOException failure = await Assert.ThrowsAsync<IOException>(() => harness.RunOnceAsync());

      Assert.Equal("Synthetic: the spooler cannot list its queues.", failure.Message);
      Assert.Equal(0, harness.Server.Claims);
      Assert.Empty(harness.Server.Events);
      Assert.Empty(harness.Journal.Jobs);
      Assert.Empty(harness.Printer.Lookups);
      Assert.Equal(0, harness.Printer.Submissions);
   }



   /// <summary>
   /// What the lookup after the download can find wrong with the job's own printer, each refused before SUBMITTING is
   /// recorded, as FAILED with its reason, with the document downloaded once and the printer never handed it: the
   /// printer no longer offers the job's format; the printer was unshared while the document streamed, in which case it
   /// is not even asked about; and the printer's own settings cannot be read, which fails the job with that reason.
   /// </summary>
   [Theory]
   [InlineData("format-withdrawn", Unavailable, 1)]
   [InlineData("unshared", Unavailable, 0)]
   [InlineData("own-settings-unreadable", "Synthetic: this printer's driver never answers.", 1)]
   public async Task WhatTheLookupFindsWrongWithTheJobsPrinterIsRefusedBeforeSubmitting(string fault, string reason, int lookups)
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(FakePrintServer.Job(Document), Document);
      switch(fault)
      {
         case "format-withdrawn":
            printer.Finding = _ => printer.Printers[0] = printer.Printers[0] with { Formats = ["PDF"] };
            break;
         case "unshared":
            server.Arriving = (kind, _) =>
            {
               if(string.Equals(kind, "content", StringComparison.Ordinal))
               { harness.Exposed.Clear(); }
               return Task.CompletedTask;
            };
            break;
         default:
            printer.Finding = _ => throw new TimeoutException(reason);
            break;
      }

      Assert.True(await harness.RunOnceAsync());

      Assert.Equal(["RECEIVED", "FAILED"], journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(reason, journal.Last("job-1").Message);
      Assert.Equal(journal.StatesOf("job-1"), server.States, StringComparer.Ordinal);
      Assert.Equal(1, server.Downloads);
      Assert.Equal(lookups, printer.Lookups.Count);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal(0, printer.Discoveries);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
   }
}
