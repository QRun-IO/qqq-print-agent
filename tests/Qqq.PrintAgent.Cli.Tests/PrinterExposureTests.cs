using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class PrinterExposureTests
{
   [Fact]
   public void Exposure_IsExplicitPersistedAndValidatedAgainstDiscovery()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-exposure-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = new(directory))
         {
            Assert.Empty(store.GetExposedPrinterIds());
            Assert.Throws<InvalidOperationException>(() => store.SetExposure("missing", true, []));
            Assert.Throws<InvalidOperationException>(() => store.SetExposure("interactive", true, [new PrinterInfo("interactive", "Print to File", "Driver", "FILE:", "UNKNOWN", [])]));
            store.SetExposure("p1", true, new FakePrinter().Discover());
         }
         using(AgentStore restored = new(directory))
         {
            Assert.Contains("p1", restored.GetExposedPrinterIds());
            restored.SetExposure("p1", false, []);
            Assert.Empty(restored.GetExposedPrinterIds());
         }
      }
      finally
      {
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

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
      await Assert.ThrowsAsync<InvalidOperationException>(() => backend.PrintAsync(job, [1], CancellationToken.None));
      Assert.False(printer.Printed);
   }

   [Fact]
   public void Snapshot_IncludesSelectedPrintersAndJobsButNeverCredential()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-snapshot-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = new(directory))
         {
            store.SaveConfiguration(new AgentConfiguration("https://example.com", "d1", "secret-never-in-snapshot"));
            store.SetExposure("p1", true, new FakePrinter().Discover());
            store.Record("j1", new PrintEvent("e1", "SPOOLED", SpoolerJobId: "123"));
         }
         using AgentRuntime runtime = new(directory, _ => { }, new FakePrinter());
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
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

   private sealed class FakePrinter : IPrinterBackend
   {
      public bool Printed { get; private set; }
      public IReadOnlyList<PrinterInfo> Discover() => [new("p1", "Test", "Driver", "Port", "READY", ["PDF"])];
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         Printed = true;
         return Task.FromResult("123");
      }
   }
}
