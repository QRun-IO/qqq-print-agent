/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>A printer is shared with the server only when the operator says so. These tests cover that choice being explicit, persisted, re-checked at print time, and visible in the status snapshot without leaking credentials.</summary>
public sealed class PrinterExposureTests
{
   /// <summary>Nothing is shared by default. Sharing a printer that does not exist, or an interactive "print to file" queue, is refused; a real one is shared and remembered across a restart, and can be hidden again.</summary>
   [Fact]
   public void Exposure_IsExplicitPersistedAndValidatedAgainstDiscovery()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-exposure-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(JournalStore store = TestState.OpenStore(directory))
         {
            Assert.Empty(store.GetExposedPrinterIds());
            Assert.Throws<InvalidOperationException>(() => store.SetExposure("missing", true, []));
            Assert.Throws<InvalidOperationException>(() => store.SetExposure("interactive", true, [new PrinterInfo("interactive", "Print to File", "Driver", "FILE:", "UNKNOWN", [])]));
            store.SetExposure("p1", true, new FakePrinter().Discover());
         }
         using(JournalStore restored = TestState.OpenStore(directory))
         {
            Assert.Contains("p1", restored.GetExposedPrinterIds());
            restored.SetExposure("p1", false, []);
            Assert.Empty(restored.GetExposedPrinterIds());
         }
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>The sharing filter is consulted on every call, not cached: once a printer is unshared it disappears from discovery and a job for it is refused before reaching the real printer, even if the job was claimed earlier.</summary>
   [Fact]
   public async Task Backend_RechecksExposureBeforeSubmitting()
   {
      FakePrinter printer = new();
      HashSet<string> selected = new(StringComparer.Ordinal) { "p1" };
      ExposedPrinterBackend backend = new(printer, () => selected);
      Assert.Single(backend.Discover());
      selected.Clear();
      Assert.Empty(backend.Discover());
      PrintJob job = new("j1", "p1", "Test", "PDF", "", 1, new PrintOptions(), "", "CLAIMED");
      await Assert.ThrowsAsync<PrinterNotExposedException>(() => backend.PrintFileAsync(job, Path.Combine(Path.GetTempPath(), "unused-exposure.document"), TimeSpan.FromSeconds(1), CancellationToken.None));
      Assert.False(printer.Printed);
   }



   /// <summary>The status snapshot shows the enrolled state, the shared printer and the recorded job (and reports an unconnected agent as RED), but the device token never appears anywhere in its serialized form.</summary>
   [Fact]
   public void Snapshot_IncludesSelectedPrintersAndJobsButNeverCredential()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-snapshot-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(JournalStore store = TestState.OpenStore(directory))
         {
            store.SaveConfiguration(new AgentConfiguration("https://example.com", "d1", "secret-never-in-snapshot"));
            store.SetExposure("p1", true, new FakePrinter().Discover());
            store.Record("j1", new PrintEvent("e1", "SPOOLED", SpoolerJobId: "123"));
         }
         using SingleServerHarness runtime = new(directory, _ => { }, new FakePrinter());
         AgentSnapshot snapshot = runtime.GetSnapshot();
         Assert.True(snapshot.Enrolled);
         Assert.False(snapshot.Connected);
         Assert.Equal("RED", snapshot.Health);
         Assert.True(Assert.Single(snapshot.Printers).Exposed);
         Assert.Equal("SPOOLED", Assert.Single(snapshot.Jobs).Status);
         Assert.DoesNotContain("secret-never-in-snapshot", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>A ready PDF queue that records whether anything was sent to it, so a test can prove a refused job never reached the printer.</summary>
   private sealed class FakePrinter : IPrinterBackend
   {
      public bool Printed { get; private set; }



      /// <summary>Reports the single synthetic queue "p1".</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new("p1", "Test", "Driver", "Port", "READY", ["PDF"])];



      /// <summary>Reports the same queue; it has no media to leave out.</summary>
      public IReadOnlyList<PrinterInfo> ListWithoutMedia() => Discover();



      /// <summary>Returns the queue when it is the one asked for.</summary>
      public PrinterInfo? FindWithMedia(string printerId) => Discover().SingleOrDefault(printer => string.Equals(printer.PrinterId, printerId, StringComparison.Ordinal));



      /// <summary>Marks that a print arrived and returns a fixed receipt.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      {
         Printed = true;
         return Task.FromResult("123");
      }
   }
}
