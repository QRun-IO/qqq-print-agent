/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Jobs;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Jobs;

/// <summary>What a job row shows, when its actions are offered, what their confirmations say, and what the details views contain.</summary>
public sealed class JobRowTests
{
   /// <summary>A fully recorded job shows its title, printer name, status in words (with the reviewed mark), format and local creation time, and keeps the raw timestamp as help text.</summary>
   [Fact]
   public void RecordedJob_ShowsItsFacts()
   {
      var row = JobRow.From(TestData.Job(status: "FAILED", title: "Invoice 42", printerName: "Front Desk Laser", printerId: "printer-1", format: "PDF", reviewed: true, createdAt: "2026-11-10T10:00:30Z"), TestData.Time);

      Assert.Equal("Invoice 42|Front Desk Laser|Failed · Reviewed|PDF|11/10/2026 12:00:30", string.Join('|', row.Cells));
      Assert.Equal("2026-11-10T10:00:30Z", row.CreatedHelpText);
      Assert.Equal("Invoice 42", row.Subject);
   }



   /// <summary>An older record without a title, printer name, format or creation time shows its job identifier, the printer identifier or "Not recorded", a dash and "Not recorded" rather than invented values.</summary>
   [Fact]
   public void SparseJob_ShowsFallbacksInsteadOfInventedValues()
   {
      var withPrinterId = JobRow.From(TestData.Job(id: "job-77", printerId: "printer-9"), TestData.Time);
      var bare = JobRow.From(TestData.Job(id: "job-78"), TestData.Time);

      Assert.Equal("job-77|printer-9|Queued|—|Not recorded", string.Join('|', withPrinterId.Cells));
      Assert.Equal("job-78|Not recorded|Queued|—|Not recorded", string.Join('|', bare.Cells));
      Assert.Equal(string.Empty, bare.CreatedHelpText);
      Assert.Equal("job-78", bare.Subject);
   }



   /// <summary>Another copy is offered for failed, uncertain and already-spooled jobs only, and never while a request runs.</summary>
   [Theory]
   [InlineData("FAILED", true)]
   [InlineData("OUTCOME_UNKNOWN", true)]
   [InlineData("SPOOLED", true)]
   [InlineData("SPOOLER_COMPLETED", true)]
   [InlineData("QUEUED", false)]
   [InlineData("SUBMITTING", false)]
   public void Retry_IsOfferedOnlyAfterAnAttempt(string status, bool expected)
   {
      Assert.Equal(expected, JobActions.CanRetry(TestData.Job(status: status), busy: false));
      Assert.False(JobActions.CanRetry(TestData.Job(status: status), busy: true));
   }



   /// <summary>Cancelling is offered for a queued job, and for a spooled one only when its Windows job number is known; never while a request runs.</summary>
   [Theory]
   [InlineData("QUEUED", null, true)]
   [InlineData("SPOOLED", "42", true)]
   [InlineData("SPOOLED", null, false)]
   [InlineData("FAILED", "42", false)]
   public void Cancel_NeedsAQueuedJobOrAKnownWindowsJob(string status, string? spoolerJobId, bool expected)
   {
      Assert.Equal(expected, JobActions.CanCancel(TestData.Job(status: status, spoolerJobId: spoolerJobId), busy: false));
      Assert.False(JobActions.CanCancel(TestData.Job(status: status, spoolerJobId: spoolerJobId), busy: true));
   }



   /// <summary>Marking reviewed is offered once, for a failed or uncertain job that has not been reviewed.</summary>
   [Theory]
   [InlineData("FAILED", false, true)]
   [InlineData("OUTCOME_UNKNOWN", false, true)]
   [InlineData("FAILED", true, false)]
   [InlineData("SPOOLED", false, false)]
   public void Review_IsOfferedOnceForFailedOrUncertainJobs(string status, bool reviewed, bool expected)
   {
      Assert.Equal(expected, JobActions.CanReview(TestData.Job(status: status, reviewed: reviewed), busy: false));
      Assert.False(JobActions.CanReview(TestData.Job(status: status, reviewed: reviewed), busy: true));
   }



   /// <summary>Full details need no running request, and refreshing the inline details also needs no detail load already on its way.</summary>
   [Fact]
   public void DetailActions_DoNotOverlapOtherRequests()
   {
      Assert.True(JobActions.CanOpenDetails(busy: false));
      Assert.False(JobActions.CanOpenDetails(busy: true));
      Assert.True(JobActions.CanRefreshDetails(busy: false, loading: false));
      Assert.False(JobActions.CanRefreshDetails(busy: false, loading: true));
      Assert.False(JobActions.CanRefreshDetails(busy: true, loading: false));
   }



   /// <summary>An expanded row says it is loading, and when the details did not load it points to the refresh button after a refusal and to the service when no answer arrived.</summary>
   [Fact]
   public void InlineDetails_SayWhatIsHappeningAndWhatToTry()
   {
      Assert.Equal("Loading job details…", JobActions.InlineDetailsLoading);
      Assert.Equal("Details unavailable. Use Refresh details to try again.", JobActions.InlineDetailsUnavailable(rejected: true));
      Assert.Equal("Details unavailable. Check the service, then refresh details.", JobActions.InlineDetailsUnavailable(rejected: false));
   }



   /// <summary>The confirmations name the job, warn about duplicate output, say cancellation is best effort, and say that reviewing changes no outcome; the details failure asks for a refresh.</summary>
   [Fact]
   public void Confirmations_SayWhatTheActionWillAndWillNotDo()
   {
      Assert.Equal(new ConfirmationPrompt("Print another copy?", "Invoice 42\nFront Desk Laser\n\nThis creates a new job. Check the printer first: the previous attempt may already have produced output.", "Print again"),
         JobActions.RetryPrompt(TestData.Job(title: "Invoice 42", printerName: "Front Desk Laser")));
      Assert.Equal(new ConfirmationPrompt("Print another copy?", "job-5\nprinter-9\n\nThis creates a new job. Check the printer first: the previous attempt may already have produced output.", "Print again"),
         JobActions.RetryPrompt(TestData.Job(id: "job-5", printerId: "printer-9")));
      Assert.Equal(new ConfirmationPrompt("Cancel this job?", "Cancellation is best effort. Pages or labels already sent to the printer may still print.", "Cancel job"), JobActions.CancelPrompt);
      Assert.Equal(new ConfirmationPrompt("Mark this job reviewed?", "This clears its attention indicator. It does not print, cancel or change the recorded outcome.", "Mark reviewed"), JobActions.ReviewPrompt);
      Assert.Equal(new FeedbackMessage("Job details unavailable", "The service could not return this job. Refresh and try again.", FeedbackSeverity.Error), JobActions.DetailsUnavailable);
   }



   /// <summary>The full details list every summary field unshortened, up to 200 events oldest first with delivery and Windows job, and note when retention removed older events.</summary>
   [Fact]
   public void FullDetails_ListSummaryAndEventHistory()
   {
      JobSnapshot job = TestData.Job(id: "job-9", status: "SPOOLED", title: "Invoice 42", printerName: "Front Desk Laser", format: "PDF", createdAt: "2026-11-10T10:00:00Z", spoolerJobId: "42", message: "Accepted by Windows.");
      JobEventSnapshot[] events =
      [
         new("event-1", "RECEIVED", "2026-11-10T10:01:00Z", null, null, Delivered: true),
         new("event-2", "SPOOLED", "2026-11-10T10:02:00Z", "Accepted by Windows.", "42", Delivered: false),
      ];

      var content = JobDetailsContent.From(new JobDetailsSnapshot(job, events, HistoryPruned: true), TestData.Time, compact: false);

      Assert.Equal("Document=Invoice 42;Printer=Front Desk Laser;Status=Sent to printer;Created=11/10/2026 12:00;Last update=11/10/2026 12:05;Outcome=Accepted by Windows.;Format=PDF;Job ID=job-9;Windows job=42",
         string.Join(';', content.Fields.Select(field => field.Label + "=" + field.Value)));
      Assert.DoesNotContain(content.Fields, field => field.Shortened);
      Assert.Equal("Event history", content.EventsHeading);
      Assert.Equal("The service returns up to 200 retained events. Sent to printer means Windows accepted the job, not confirmed physical output.", content.EventsNote);
      Assert.Equal(new JobEventRow("Time", "Status", "Message and delivery"), content.EventColumns);
      Assert.Equal(new JobEventRow("11/10/2026 12:01", "Received", "No message\nReported to server\nEvent: event-1"), content.Events[0]);
      Assert.Equal(new JobEventRow("11/10/2026 12:02", "Sent to printer", "Accepted by Windows.\nWaiting to report\nEvent: event-2\nWindows job: 42"), content.Events[1]);
      Assert.Null(content.NoEventsNote);
      Assert.Equal("Older event detail has been removed by the retention policy. The job summary remains available.", content.PrunedNote);
   }



   /// <summary>The compact details shorten the three free-text fields, show only the latest five events, say "Not recorded" and "Not assigned" for missing facts, and point to Full details.</summary>
   [Fact]
   public void CompactDetails_ShortenTextAndShowTheLatestFiveEvents()
   {
      JobEventSnapshot[] events = Enumerable.Range(1, 7).Select(number => new JobEventSnapshot("event-" + number, "RECEIVED", "2026-11-10T10:00:00Z", null, null, Delivered: true)).ToArray();

      var content = JobDetailsContent.From(new JobDetailsSnapshot(TestData.Job(id: "job-9", reviewed: true), events, HistoryPruned: false), TestData.Time, compact: true);

      Assert.Equal("Document=Not recorded;Printer=Not recorded;Status=Queued · Reviewed;Created=Not recorded;Last update=11/10/2026 12:05;Outcome=No additional outcome message recorded.;Format=Not recorded;Job ID=job-9;Windows job=Not assigned",
         string.Join(';', content.Fields.Select(field => field.Label + "=" + field.Value)));
      Assert.Equal("Document,Printer,Outcome", string.Join(',', content.Fields.Where(field => field.Shortened).Select(field => field.Label)));
      Assert.Equal("Recent events (up to 5)", content.EventsHeading);
      Assert.Equal("Use Full details for the retained event history. Sent to printer is spool acceptance, not confirmed paper output.", content.EventsNote);
      Assert.Equal(5, content.Events.Count);
      Assert.EndsWith("Event: event-3", content.Events[0].Message, StringComparison.Ordinal);
      Assert.EndsWith("Event: event-7", content.Events[4].Message, StringComparison.Ordinal);
      Assert.Null(content.PrunedNote);
   }



   /// <summary>A job without retained events says so instead of showing an empty table, and the full form never shows more than 200.</summary>
   [Fact]
   public void EventCount_IsBoundedAndAnEmptyHistoryIsSaid()
   {
      JobEventSnapshot[] many = Enumerable.Range(1, 205).Select(number => new JobEventSnapshot("event-" + number, "RECEIVED", "2026-11-10T10:00:00Z", null, null, Delivered: true)).ToArray();

      var empty = JobDetailsContent.From(new JobDetailsSnapshot(TestData.Job(), [], HistoryPruned: false), TestData.Time, compact: false);
      var full = JobDetailsContent.From(new JobDetailsSnapshot(TestData.Job(printerId: "printer-9"), many, HistoryPruned: false), TestData.Time, compact: false);

      Assert.Empty(empty.Events);
      Assert.Equal("No retained local events are available for this job.", empty.NoEventsNote);
      Assert.Equal(200, full.Events.Count);
      Assert.EndsWith("Event: event-200", full.Events[199].Message, StringComparison.Ordinal);
      Assert.Equal("printer-9", full.Fields[1].Value);
      Assert.Equal(5, JobDetailsContent.CompactEvents);
      Assert.Equal(200, JobDetailsContent.FullEvents);
   }
}
