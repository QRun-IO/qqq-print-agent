/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The rules of cancelling one spooler job, run against a spooler that exists only in memory: a job is deleted only when
/// its document name is exactly "QQQ" plus the job's identifier, a job that left the queue is reported as absent, every
/// Windows failure keeps its error code, and the queue handle is closed on every path. Each test also asserts the exact
/// calls sent to the spooler and their order, which is the evidence that the rules reach Windows the way they did before
/// the calls were moved behind <see cref="ISpoolerJobs"/>.
/// </summary>
public sealed class WindowsSpoolerJobsTests
{
   private const string Queue = "Synthetic label queue";
   private const string JobId = "job-7f3a";
   private const string OwnDocument = "QQQ job-7f3a";



   /// <summary>A job whose document name is exactly ours is read back, deleted with command 5 and the queue closed, in that order; nothing else in the queue is touched.</summary>
   [Fact]
   public void OwnedJob_IsReadBackThenDeleted()
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs().WithJob(Queue, 42, OwnDocument).WithJob(Queue, 43, "Someone else's invoice");
      WindowsSpoolerJobs.Cancel(spooler, Queue, JobId, "42");
      Assert.Equal(
      [
         $"OpenPrinter({Queue})",
         "GetJobSize(0x101, 42)",
         $"GetJobDocument(0x101, 42, {FakeSpoolerJobs.DetailsSize(OwnDocument)})",
         "SetJob(0x101, 42, 5)",
         "ClosePrinter(0x101)",
      ], spooler.Calls);
      Assert.Equal([43u], spooler.Queues[Queue].Keys);
      Assert.Equal(0, spooler.OpenHandles);
   }



   /// <summary>
   /// A spooler number that now belongs to another document is refused as an ownership mismatch and nothing is deleted. The
   /// comparison is exact: another job's identifier, a longer identifier that starts with ours, different letter case,
   /// surrounding spaces, the bare identifier and a missing name are all somebody else's document.
   /// </summary>
   [Theory]
   [InlineData("Quarterly report.pdf")]
   [InlineData("QQQ job-other")]
   [InlineData("QQQ job-7f3a0")]
   [InlineData("QQQ job-7f3")]
   [InlineData("qqq job-7f3a")]
   [InlineData("QQQ JOB-7F3A")]
   [InlineData("QQQ job-7f3a ")]
   [InlineData(" QQQ job-7f3a")]
   [InlineData("QQQ  job-7f3a")]
   [InlineData("job-7f3a")]
   [InlineData("")]
   [InlineData(null)]
   public void ReusedSpoolerNumber_IsRefusedAndNothingIsDeleted(string? document)
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs().WithJob(Queue, 42, document);
      SpoolCancellationException refusal = Assert.Throws<SpoolCancellationException>(() => WindowsSpoolerJobs.Cancel(spooler, Queue, JobId, "42"));
      Assert.Equal("OWNERSHIP_MISMATCH", refusal.Outcome);
      Assert.Equal("The spooler number now belongs to another document; cancellation was refused.", refusal.Message);
      Assert.Equal(
      [
         $"OpenPrinter({Queue})",
         "GetJobSize(0x101, 42)",
         $"GetJobDocument(0x101, 42, {FakeSpoolerJobs.DetailsSize(document)})",
         "ClosePrinter(0x101)",
      ], spooler.Calls);
      Assert.True(spooler.Queues[Queue].ContainsKey(42));
      Assert.Equal(0, spooler.OpenHandles);
   }



   /// <summary>A job that is no longer in the queue (Windows answers the sizing call with no size and error 87 or 1168) is reported as already absent, without a read or a delete.</summary>
   [Theory]
   [InlineData(87)]
   [InlineData(1168)]
   public void JobThatLeftTheQueue_IsReportedAsAlreadyAbsent(int error)
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs { MissingJobError = error }.WithJob(Queue, 43, OwnDocument);
      SpoolCancellationException refusal = Assert.Throws<SpoolCancellationException>(() => WindowsSpoolerJobs.Cancel(spooler, Queue, JobId, "42"));
      Assert.Equal("ALREADY_ABSENT", refusal.Outcome);
      Assert.Equal("The job is no longer in the Windows queue. Pages already printed cannot be recalled.", refusal.Message);
      Assert.Equal([$"OpenPrinter({Queue})", "GetJobSize(0x101, 42)", "ClosePrinter(0x101)"], spooler.Calls);
      Assert.True(spooler.Queues[Queue].ContainsKey(43));
      Assert.Equal(0, spooler.OpenHandles);
   }



   /// <summary>A sizing call that fails for any other reason (access denied, spooler unreachable) is a Windows failure with its own error code, never "already absent".</summary>
   [Theory]
   [InlineData(5)]
   [InlineData(1722)]
   public void UnreadableJob_IsAWindowsFailureAndNotAnAbsentJob(int error)
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs { MissingJobError = error }.WithJob(Queue, 43, OwnDocument);
      Win32Exception failure = Assert.Throws<Win32Exception>(() => WindowsSpoolerJobs.Cancel(spooler, Queue, JobId, "42"));
      Assert.Equal(error, failure.NativeErrorCode);
      Assert.Equal([$"OpenPrinter({Queue})", "GetJobSize(0x101, 42)", "ClosePrinter(0x101)"], spooler.Calls);
      Assert.Equal(0, spooler.OpenHandles);
   }



   /// <summary>A queue that cannot be opened fails with the Windows error and no further call is made; there is no handle to close.</summary>
   [Fact]
   public void QueueThatCannotBeOpened_FailsBeforeAnyJobIsRead()
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs { UnknownQueueError = 1801 }.WithJob(Queue, 42, OwnDocument);
      Win32Exception failure = Assert.Throws<Win32Exception>(() => WindowsSpoolerJobs.Cancel(spooler, "A queue that was removed", JobId, "42"));
      Assert.Equal(1801, failure.NativeErrorCode);
      Assert.Equal(["OpenPrinter(A queue that was removed)"], spooler.Calls);
      Assert.True(spooler.Queues[Queue].ContainsKey(42));
   }



   /// <summary>When the job's details cannot be read, the failure keeps its Windows error, nothing is deleted and the queue is closed.</summary>
   [Fact]
   public void FailedRead_DeletesNothingAndClosesTheQueue()
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs { ReadError = 5 }.WithJob(Queue, 42, OwnDocument);
      Win32Exception failure = Assert.Throws<Win32Exception>(() => WindowsSpoolerJobs.Cancel(spooler, Queue, JobId, "42"));
      Assert.Equal(5, failure.NativeErrorCode);
      Assert.Equal(
      [
         $"OpenPrinter({Queue})",
         "GetJobSize(0x101, 42)",
         $"GetJobDocument(0x101, 42, {FakeSpoolerJobs.DetailsSize(OwnDocument)})",
         "ClosePrinter(0x101)",
      ], spooler.Calls);
      Assert.True(spooler.Queues[Queue].ContainsKey(42));
      Assert.Equal(0, spooler.OpenHandles);
   }



   /// <summary>When Windows refuses the delete of a job that is ours, the failure keeps its Windows error, the job stays and the queue is closed.</summary>
   [Fact]
   public void RefusedDelete_KeepsItsWindowsErrorAndClosesTheQueue()
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs { CommandError = 5 }.WithJob(Queue, 42, OwnDocument);
      Win32Exception failure = Assert.Throws<Win32Exception>(() => WindowsSpoolerJobs.Cancel(spooler, Queue, JobId, "42"));
      Assert.Equal(5, failure.NativeErrorCode);
      Assert.Equal("SetJob(0x101, 42, 5)", spooler.Calls[^2]);
      Assert.Equal("ClosePrinter(0x101)", spooler.Calls[^1]);
      Assert.True(spooler.Queues[Queue].ContainsKey(42));
      Assert.Equal(0, spooler.OpenHandles);
   }



   /// <summary>An answer larger than 1 MiB is refused before any buffer of that size is asked for; an answer of exactly 1 MiB is still read.</summary>
   [Fact]
   public void SpoolerAnswerIsBoundedBeforeItIsRead()
   {
      FakeSpoolerJobs oversized = new FakeSpoolerJobs { ReportedSize = 1_048_577 }.WithJob(Queue, 42, OwnDocument);
      InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => WindowsSpoolerJobs.Cancel(oversized, Queue, JobId, "42"));
      Assert.Equal("The Windows spooler response exceeded its bound.", refusal.Message);
      Assert.Equal([$"OpenPrinter({Queue})", "GetJobSize(0x101, 42)", "ClosePrinter(0x101)"], oversized.Calls);
      Assert.True(oversized.Queues[Queue].ContainsKey(42));

      FakeSpoolerJobs atTheBound = new FakeSpoolerJobs { ReportedSize = 1_048_576 }.WithJob(Queue, 42, OwnDocument);
      WindowsSpoolerJobs.Cancel(atTheBound, Queue, JobId, "42");
      Assert.Equal("GetJobDocument(0x101, 42, 1048576)", atTheBound.Calls[2]);
      Assert.Empty(atTheBound.Queues[Queue]);
   }



   /// <summary>A receipt that is not a plain unsigned number (empty, signed, spaced, fractional, hexadecimal or too large) is refused before the spooler is asked anything.</summary>
   [Theory]
   [InlineData("")]
   [InlineData("abc")]
   [InlineData("-1")]
   [InlineData("+42")]
   [InlineData(" 42")]
   [InlineData("42 ")]
   [InlineData("4.2")]
   [InlineData("0x2a")]
   [InlineData("4294967296")]
   public void InvalidReceipt_IsRefusedBeforeTheSpoolerIsAsked(string receipt)
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs().WithJob(Queue, 42, OwnDocument);
      InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => WindowsSpoolerJobs.Cancel(spooler, Queue, JobId, receipt));
      Assert.Equal("This job has no valid Windows spooler receipt.", refusal.Message);
      Assert.IsNotType<SpoolCancellationException>(refusal);
      Assert.Empty(spooler.Calls);
   }
}
