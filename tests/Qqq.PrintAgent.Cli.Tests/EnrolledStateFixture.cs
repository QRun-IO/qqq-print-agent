/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Security.Cryptography;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The local state of an enrolled computer in a temporary folder: a saved pairing, one shared synthetic queue and, when a test
/// asks for it, a job the Windows spooler already accepted. It holds no server and starts no runtime; tests that need one
/// build it on this state, so what survives sign-in, sign-out, enrollment and damage to the protected files can be inspected.
/// </summary>
internal sealed class EnrolledStateFixture : IDisposable
{
   /// <summary>The temporary folder that holds the enrolled state, removed when the fixture is disposed.</summary>
   internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qqq-enrolled-" + Guid.NewGuid().ToString("N"));



   /// <summary>The one shared queue; a runtime built on this state is given it as its printer backend.</summary>
   internal SyntheticPrinter Printer { get; } = new();



   /// <summary>Saves an enrolled configuration and shares the synthetic queue.</summary>
   internal EnrolledStateFixture()
   {
      using JournalStore store = TestState.OpenStore(DirectoryPath);
      store.SaveConfiguration(new AgentConfiguration("https://synthetic.invalid", "device", "synthetic-token"));
      store.SetExposure("printer", true, Printer.Discover());
   }



   /// <summary>Records a job that has already been handed to the Windows spooler (spooler id 123) with its receipt acknowledged, so a test can show the journal was kept.</summary>
   internal void SeedSpooledJob()
   {
      using JournalStore store = TestState.OpenStore(DirectoryPath);
      store.TrackJob(new PrintJob("job", "printer", "Synthetic", "PDF", Convert.ToHexString(SHA256.HashData([1, 2, 3])), 3,
         new PrintOptions(), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), "CLAIMED"), "Synthetic printer");
      PrintEvent receipt = new("spooled", "SPOOLED", SpoolerJobId: "123");
      store.Record("job", receipt);
      store.Acknowledge(receipt.EventId);
   }



   /// <summary>Clears SQLite's pool so the files unlock, and deletes the temporary state folder.</summary>
   public void Dispose()
   {
      JournalFiles.Delete(DirectoryPath);
   }



   /// <summary>One ready PDF queue that refuses to print, because the tests built on this state never dispatch a job.</summary>
   internal sealed class SyntheticPrinter : IPrinterBackend
   {
      /// <summary>Reports the single ready PDF queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new("printer", "Synthetic printer", "Fixture", "None", "READY", ["PDF"])];



      /// <summary>Always throws: no test on this state may submit a document.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
         => throw new InvalidOperationException("Tests on enrolled local state must never print.");
   }
}
