/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The printer side of the fixture: a file-based fake that no physical device stands behind.</summary>
internal sealed partial class RealtimeRuntimeFixture
{
   /// <summary>
   /// A printer that takes the staged file the service submits, counts and orders the jobs it receives, and remembers the
   /// submission timeout it was given. A test can hold a submission open inside the printer, and can script how a spooler
   /// cancellation ends. It counts each of the three reads separately, and a test can make the full discovery fail the way
   /// it does when one queue's driver does not answer, while the other two reads, which do not ask that driver, go on working.
   /// </summary>
   internal sealed class SyntheticFilePrinter : IPrinterBackend, IPrinterJobControl
   {
      internal int Prints;
      internal int Cancellations;

      /// <summary>How many submissions reached the printer, including one that is held inside it or was interrupted there; <see cref="Prints"/> counts only those that finished.</summary>
      internal int Submissions;
      internal TimeSpan LastSubmissionTimeout;
      internal ConcurrentQueue<string> Order { get; } = new();



      /// <summary>How often the full discovery was asked for, the read that asks every queue's driver.</summary>
      internal int Discoveries;



      /// <summary>How often the list without media was asked for, the read that asks no driver.</summary>
      internal int Listings;



      /// <summary>How often one queue was looked up with its media.</summary>
      internal int Lookups;



      /// <summary>When true, a submission signals <see cref="Entered"/> and then stays inside the printer until <see cref="Release"/> completes.</summary>
      internal bool BlockPrint { get; set; }



      /// <summary>
      /// When set to a printer identifier, a submission to that queue stays inside the printer until <see cref="Release"/>
      /// completes, as one busy device does, while submissions to the other queue pass straight through; <see cref="BlockPrint"/>
      /// holds both. A held submission is counted in <see cref="Submissions"/> at once and in <see cref="Prints"/> only when it
      /// is let go. When this is not set, no job is held on its account, a job that names no printer included.
      /// </summary>
      internal string? BlockPrinter { get; set; }



      /// <summary>Completed when a submission has reached the printer.</summary>
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Lets a held submission finish.</summary>
      internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>When set, cancellation is refused with this verified outcome instead of succeeding.</summary>
      internal string? CancellationOutcome { get; set; }



      /// <summary>When true, cancellation happens and then its reply is lost, as when the isolated worker dies after acting.</summary>
      internal bool LoseCancellationResult { get; set; }



      /// <summary>When set, cancellation is attempted and then fails with this exception, for failures the command code does not expect.</summary>
      internal Exception? CancellationFailure { get; set; }



      /// <summary>When set, the full discovery throws what this makes, each time it is asked; the list without media and the lookup of one queue are not affected.</summary>
      internal Func<Exception>? DiscoveryFailure { get; set; }



      /// <summary>The two ready queues, "printer" and "other", each accepting PDF and ZPL; neither has media.</summary>
      private static PrinterInfo[] Queues => [new("printer", "Synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"]), new("other", "Other synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"])];



      /// <summary>Counts the call and reports the two queues, or fails as the test said.</summary>
      public IReadOnlyList<PrinterInfo> Discover()
      {
         Interlocked.Increment(ref Discoveries);
         return DiscoveryFailure is null ? Queues : throw DiscoveryFailure();
      }



      /// <summary>Counts the call and reports the same two queues.</summary>
      public IReadOnlyList<PrinterInfo> ListWithoutMedia()
      {
         Interlocked.Increment(ref Listings);
         return Queues;
      }



      /// <summary>Counts the call and returns the queue with that identifier, or null when it is neither of the two.</summary>
      public PrinterInfo? FindWithMedia(string printerId)
      {
         Interlocked.Increment(ref Lookups);
         return Queues.SingleOrDefault(queue => string.Equals(queue.PrinterId, printerId, StringComparison.Ordinal));
      }



      /// <summary>Captures the submission timeout the agent chose so tests can assert it, checks the staged file's size matches the declared content length, optionally holds the submission, records the job in order, and returns spooler id 123.</summary>
      public async Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      {
         LastSubmissionTimeout = submissionTimeout;
         Assert.Equal(job.ContentLength, new FileInfo(path).Length);
         Interlocked.Increment(ref Submissions);
         Entered.TrySetResult();
         if(BlockPrint || (BlockPrinter is string held && string.Equals(held, job.PrinterId, StringComparison.Ordinal)))
         { await Release.Task.WaitAsync(cancellationToken); }
         Interlocked.Increment(ref Prints);
         Order.Enqueue(job.JobId);
         return "123";
      }



      /// <summary>Checks it was asked to cancel exactly the recorded spooler job on its own queue, counts the call, then fails or refuses as scripted or succeeds.</summary>
      public Task CancelAsync(string printerName, string jobId, string spoolerJobId, CancellationToken cancellationToken = default)
      {
         Assert.Equal("Synthetic", printerName);
         Assert.Equal("123", spoolerJobId);
         Interlocked.Increment(ref Cancellations);
         if(CancellationFailure is not null)
         { throw CancellationFailure; }
         if(LoseCancellationResult)
         {
            throw new IOException("Synthetic isolated worker reply lost after action.");
         }
         if(CancellationOutcome is not null)
         { throw new SpoolCancellationException(CancellationOutcome, "Synthetic refusal."); }
         return Task.CompletedTask;
      }
   }
}
