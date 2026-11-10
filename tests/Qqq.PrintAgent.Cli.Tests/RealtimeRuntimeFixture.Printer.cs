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
   /// A printer that accepts only the file-based submission the realtime path uses, counts and orders the jobs it receives, and
   /// remembers the submission timeout it was given. The in-memory print method throws, so a regression to the older path
   /// fails. A test can hold a submission open inside the printer, and can script how a spooler cancellation ends.
   /// </summary>
   internal sealed class SyntheticFilePrinter : IFilePrinterBackend, IPrinterJobControl
   {
      internal int Prints;
      internal int Cancellations;

      /// <summary>How many submissions reached the printer, including one that is held inside it or was interrupted there; <see cref="Prints"/> counts only those that finished.</summary>
      internal int Submissions;
      internal TimeSpan LastSubmissionTimeout;
      internal ConcurrentQueue<string> Order { get; } = new();



      /// <summary>When true, a submission signals <see cref="Entered"/> and then stays inside the printer until <see cref="Release"/> completes.</summary>
      internal bool BlockPrint { get; set; }



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



      /// <summary>Reports two ready queues, "printer" and "other", each accepting PDF and ZPL.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new("printer", "Synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"]), new("other", "Other synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"])];



      /// <summary>Always throws: the native realtime path must submit a file, not a byte array.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken) => throw new InvalidOperationException("The native realtime path must use a file.");



      /// <summary>Checks the staged file's size matches the declared content length, optionally holds the submission, records the job in order, and returns spooler id 123.</summary>
      public async Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken)
      {
         Assert.Equal(job.ContentLength, new FileInfo(path).Length);
         Interlocked.Increment(ref Submissions);
         Entered.TrySetResult();
         if(BlockPrint)
         { await Release.Task.WaitAsync(cancellationToken); }
         Interlocked.Increment(ref Prints);
         Order.Enqueue(job.JobId);
         return "123";
      }



      /// <summary>Captures the submission timeout the agent chose so tests can assert it, then submits normally.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      {
         LastSubmissionTimeout = submissionTimeout;
         return PrintFileAsync(job, path, cancellationToken);
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
