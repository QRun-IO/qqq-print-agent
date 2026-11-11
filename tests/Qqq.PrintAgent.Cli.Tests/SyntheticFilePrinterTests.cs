/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Tests of the synthetic printer itself. They call the fake directly, with no service in between, so they show what the
/// printer holds and what it lets through and nothing else. A test that goes through the service has a second thing in the
/// way: the service submits one print at a time unless the server's settings allow more, so a job for the other queue
/// reaches the printer while one is held only when those settings allow two prints at once.
/// </summary>
public sealed class SyntheticFilePrinterTests
{
   /// <summary>
   /// With one queue held, a submission to that queue enters the printer and stays there: it is counted as a submission and
   /// not as a print. A submission made afterwards to the other queue finishes at once, while the first is still held. When
   /// the hold is released the held one finishes too. The printer's order is the order in which jobs finished, not the
   /// order in which they entered, so it reads the other queue's job first and the held one second.
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
         Assert.Equal((1, 0), (printer.Submissions, printer.Prints));
         Assert.Empty(printer.Order);
         Assert.Equal("123", await printer.PrintFileAsync(Job("b", "other"), path, TimeSpan.FromSeconds(30), CancellationToken.None));
         Assert.False(held.IsCompleted);
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



   /// <summary>
   /// With no queue named, nothing is held on that account: a job for either queue, and a job that names no printer at all,
   /// finish at once. The last case is the one a comparison of "no queue held" with "no printer named" would get wrong.
   /// </summary>
   [Fact]
   public async Task BlockPrinter_Unset_HoldsNothing_NotEvenAJobThatNamesNoPrinter()
   {
      string path = Path.Combine(Path.GetTempPath(), "qqq-block-printer-" + Guid.NewGuid().ToString("N"));
      try
      {
         await File.WriteAllBytesAsync(path, [1, 2, 3]);
         var printer = new RealtimeRuntimeFixture.SyntheticFilePrinter();
         Task<string>[] submissions =
         [
            printer.PrintFileAsync(Job("a", "printer"), path, TimeSpan.FromSeconds(30), CancellationToken.None),
            printer.PrintFileAsync(Job("b", "other"), path, TimeSpan.FromSeconds(30), CancellationToken.None),
            printer.PrintFileAsync(Job("c", null!), path, TimeSpan.FromSeconds(30), CancellationToken.None),
         ];
         Assert.All(submissions, submission => Assert.True(submission.IsCompleted));
         Assert.Equal((3, 3), (printer.Submissions, printer.Prints));
         Assert.Equal(["a", "b", "c"], printer.Order.ToArray());
      }
      finally
      {
         File.Delete(path);
      }
   }



   /// <summary>A three-byte job for the named queue; the length is what the printer checks the staged file against.</summary>
   private static PrintJob Job(string id, string printerId) => new(id, printerId, "Synthetic " + id, "PDF", "unused", 3, new PrintOptions(), RealtimeRuntimeFixture.Now(), "CLAIMED");
}
