/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Covers jobs the server has queued for this device but that have produced no local receipt yet.</summary>
public sealed class AgentRuntimeQueuedJobsTests
{
   /// <summary>
   /// While the administrator has paused dispatch, a job the server announces is known only from the server's queue. It must
   /// show in the snapshot, in a name-sorted page and in job details (with no events), yet leave nothing in the local journal.
   /// Cancelling it asks the server, removes it from the view, and must never claim it or reach the printer.
   /// </summary>
   [Fact]
   public async Task QueuedJobAppearsAndCanBeCanceledBeforeAnyLocalReceipt()
   {
      await using RealtimeRuntimeFixture fixture = new(seed: store => store.SavePrinterProfile("printer", new PrinterProfile("Dispatch labels")));
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(1, true, "CONNECTED"));
      fixture.AddJob("queued-job");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.Count > 0);
      JobSnapshot queued = Assert.Single(fixture.Runtime.GetSnapshot().Jobs);
      Assert.Equal("queued-job", queued.JobId);
      Assert.Equal("QUEUED", queued.Status);
      Assert.Equal("Synthetic queued-job", queued.Title);
      Assert.Equal("Dispatch labels", queued.PrinterName);
      string pageText = await fixture.PipeAsync(new PipeRequest("query-jobs", JobQuery: new JobQuery(SortBy: "printerName", Descending: false)));
      JobPage page = JsonSerializer.Deserialize<JobPage>(pageText, PrintApiClient.JsonOptions)!;
      Assert.Equal("Dispatch labels", Assert.Single(page.Jobs).PrinterName);
      Assert.Null(Details(fixture, queued.JobId));
      string detailsText = await fixture.PipeAsync(new PipeRequest("job-details", JobId: queued.JobId));
      JobDetailsSnapshot details = JsonSerializer.Deserialize<JobDetailsSnapshot>(detailsText, PrintApiClient.JsonOptions)!;
      Assert.Equal("QUEUED", details.Job.Status);
      Assert.Empty(details.Events);
      string result = await fixture.PipeAsync(new PipeRequest("cancel-job", JobId: queued.JobId));
      Assert.StartsWith("The server accepted the cancellation before dispatch.", result, StringComparison.Ordinal);
      Assert.Equal(1, fixture.OperatorCancellations);
      Assert.Empty(fixture.Runtime.GetSnapshot().Jobs);
      Assert.Null(Details(fixture, queued.JobId));
      Assert.Equal(0, fixture.Claims);
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>What the local journal holds for a job; null means the job was never received by this computer.</summary>
   private static JobDetailsSnapshot? Details(RealtimeRuntimeFixture fixture, string jobId)
   {
      using AgentStore store = new(fixture.DirectoryPath);
      return store.GetJobDetails(jobId);
   }
}
