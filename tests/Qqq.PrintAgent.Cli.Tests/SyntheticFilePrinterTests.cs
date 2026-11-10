/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Tests of the synthetic printer itself, without a service: the fake is what tests of queues and claiming stand on, so what it
/// holds and what it lets through is shown here once, directly.
/// </summary>
public sealed class SyntheticFilePrinterTests
{
   /// <summary>
   /// With one queue held, a submission to that queue enters the printer and stays there, while a submission made afterwards
   /// to the other queue finishes at once. The held one is counted as a submission but not as a print until it is released,
   /// and the order in which the jobs finished is the other queue's job first, then the held one.
   /// </summary>
   [Fact]
   public async Task BlockPrinter_HoldsOneQueueWhileTheOtherPrints()
   {
      string path = Path.Combine(Path.GetTempPath(), "qqq-block-printer-" + Guid.NewGuid().ToString("N"));
      try
      {
         await File.WriteAllBytesAsync(path, [1, 2, 3]);
         var printer = new RealtimeRuntimeFixture.SyntheticFilePrinter { BlockPrinter = "printer" };
         Task<string> held = printer.PrintFileAsync(Job("a", "printer"), path, TimeSpan.FromSeconds(30), CancellationToken.None);
         Assert.Equal("123", await printer.PrintFileAsync(Job("b", "other"), path, TimeSpan.FromSeconds(30), CancellationToken.None));
         Assert.False(held.IsCompleted);
         Assert.True(printer.Entered.Task.IsCompleted);
         Assert.Equal((2, 1), (printer.Submissions, printer.Prints));
         Assert.Equal("b", Assert.Single(printer.Order));
         printer.Release.TrySetResult();
         Assert.Equal("123", await held);
         Assert.Equal((2, 2), (printer.Submissions, printer.Prints));
         Assert.Equal(["b", "a"], printer.Order.ToArray());
      }
      finally
      {
         File.Delete(path);
      }
   }



   /// <summary>A three-byte job for one of the fake's two queues; the length is what the printer checks the staged file against.</summary>
   private static PrintJob Job(string id, string printerId) => new(id, printerId, "Synthetic " + id, "PDF", "unused", 3, new PrintOptions(), RealtimeRuntimeFixture.Now(), "CLAIMED");
}
