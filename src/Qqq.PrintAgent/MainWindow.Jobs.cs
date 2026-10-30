/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private readonly Dictionary<string, JobRowView> _jobRows = new(StringComparer.Ordinal);
   private int _jobQueryVersion;
   private int _jobOffset;
   private int _jobTotal;
   private const int JobPageSize = 50;

   private async void JobFilters_Changed(object sender, RoutedEventArgs e)
   {
      if(_initializing)
      {
         return;
      }
      _jobOffset = 0;
      int version = ++_jobQueryVersion;
      await Task.Delay(300);
      if(version == _jobQueryVersion && !_updatesClosed)
      {
         await RefreshJobPageAsync();
      }
   }

   private async Task RefreshJobPageAsync()
   {
      if(_busy || _updatesClosed || JobsPage.Visibility != Visibility.Visible)
      {
         return;
      }
      int version = ++_jobQueryVersion;
      string filter = (JobStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "ALL";
      int mutationVersion = _mutationVersion;
      var query = new JobQuery(Search: JobSearch.Text.Trim(), Status: filter is "ALL" or "ATTENTION" ? null : filter,
         NeedsAttentionOnly: string.Equals(filter, "ATTENTION", StringComparison.Ordinal), Offset: _jobOffset, PageSize: JobPageSize);
      JobsPrevious.IsEnabled = false;
      JobsNext.IsEnabled = false;
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("query-jobs", JobQuery: query));
         if(version != _jobQueryVersion || _updatesClosed || _busy || mutationVersion != _mutationVersion)
         {
            return;
         }
         if(IsActionError(response))
         {
            JobsSummary.Text = "History unavailable; previously shown rows may be stale";
            return;
         }
         JobPage page = JsonSerializer.Deserialize<JobPage>(response, PrintApiClient.JsonOptions) ?? throw new JsonException("Missing job page.");
         _jobOffset = page.Offset;
         _jobTotal = page.TotalCount;
         RenderJobPage(page);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         if(version == _jobQueryVersion && !_updatesClosed)
         {
            JobsSummary.Text = "History unavailable; check the service and refresh";
         }
      }
      finally
      {
         if(version == _jobQueryVersion && !_updatesClosed)
         {
            JobsPrevious.IsEnabled = _jobOffset > 0;
            JobsNext.IsEnabled = _jobOffset + JobPageSize < _jobTotal;
         }
      }
   }

   private void RenderJobPage(JobPage page)
   {
      string? selected = (JobsList.SelectedItem as JobRowView)?.Snapshot.JobId;
      var present = page.Jobs.Select(job => job.JobId).ToHashSet(StringComparer.Ordinal);
      foreach(string id in _jobRows.Keys.Where(id => !present.Contains(id)).ToArray())
      {
         JobsList.Items.Remove(_jobRows[id]);
         _jobRows.Remove(id);
      }
      for(int index = 0; index < page.Jobs.Count; index++)
      {
         JobSnapshot job = page.Jobs[index];
         if(!_jobRows.TryGetValue(job.JobId, out JobRowView? row))
         {
            row = new JobRowView(job);
            _jobRows.Add(job.JobId, row);
            JobsList.Items.Insert(index, row);
         }
         row.Update(job, FriendlyState(job.Status), FormatTime(job.UpdatedAt));
         if(JobsList.Items.IndexOf(row) != index)
         {
            JobsList.Items.Remove(row);
            JobsList.Items.Insert(index, row);
         }
      }
      JobsList.SelectedItem = selected is not null && _jobRows.TryGetValue(selected, out JobRowView? selectedRow) ? selectedRow : null;
      JobsSummary.Text = $"{page.Jobs.Count} of {page.TotalCount} matching jobs · page {page.Offset / JobPageSize + 1}";
      JobsEmpty.Visibility = page.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      JobsEmptyTitle.Text = "No matching jobs";
      JobsEmptyMessage.Text = "Try another prefix search or choose All states.";
      JobsNotice.Text = (page.ServerQueueLimited ? "Cloud queue preview is limited to 50; local history remains searchable. " : "")
         + "Sent to printer means Windows accepted the job. Check the printer to confirm physical output.";
      UpdateSelectedJob();
   }

   private async void PreviousJobs_Click(object sender, RoutedEventArgs e)
   {
      _jobOffset = Math.Max(0, _jobOffset - JobPageSize);
      await RefreshJobPageAsync();
   }

   private async void NextJobs_Click(object sender, RoutedEventArgs e)
   {
      if(_jobOffset + JobPageSize < _jobTotal)
      {
         _jobOffset += JobPageSize;
         await RefreshJobPageAsync();
      }
   }

   private void JobSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelectedJob();

   private void UpdateSelectedJob()
   {
      JobSnapshot? job = (JobsList.SelectedItem as JobRowView)?.Snapshot;
      JobDetailsButton.IsEnabled = job is not null && !_busy;
      JobRetryButton.IsEnabled = !_busy && job?.Status is "FAILED" or "OUTCOME_UNKNOWN" or "SPOOLED" or "SPOOLER_COMPLETED";
      JobCancelButton.IsEnabled = !_busy && job is not null && (job.Status is "QUEUED" || job.Status is "SPOOLED" && job.SpoolerJobId is not null);
      JobReviewButton.IsEnabled = !_busy && job is { FailureAcknowledged: false, Status: "FAILED" or "OUTCOME_UNKNOWN" };
      SelectedJobText.Text = job is null ? "Select a job for details and actions." : job.Title ?? job.JobId;
      ToolTipService.SetToolTip(SelectedJobText, SelectedJobText.Text);
   }

   private async void JobDetails_Click(object sender, RoutedEventArgs e)
   {
      if((JobsList.SelectedItem as JobRowView)?.Snapshot is JobSnapshot job)
      {
         await JobDetailsAsync(job);
      }
   }

   private async void JobRetry_Click(object sender, RoutedEventArgs e)
   {
      if((JobsList.SelectedItem as JobRowView)?.Snapshot is JobSnapshot job)
      {
         await RetryJobAsync(job);
      }
   }

   private async void JobCancel_Click(object sender, RoutedEventArgs e)
   {
      if((JobsList.SelectedItem as JobRowView)?.Snapshot is JobSnapshot job)
      {
         await CancelJobAsync(job);
      }
   }

   private async void JobReview_Click(object sender, RoutedEventArgs e)
   {
      if((JobsList.SelectedItem as JobRowView)?.Snapshot is JobSnapshot job)
      {
         await AcknowledgeJobAsync(job);
      }
   }

   private async Task JobDetailsAsync(JobSnapshot job)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      JobDetailsSnapshot? details = null;
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("job-details", JobId: job.JobId));
         if(IsActionError(response))
         {
            return;
         }
         details = JsonSerializer.Deserialize<JobDetailsSnapshot>(response, PrintApiClient.JsonOptions);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         Feedback("Job details unavailable", "The service could not return this job. Refresh and try again.", InfoBarSeverity.Error);
      }
      finally
      {
         SetBusy(false);
      }
      if(details is not null)
      {
         await ShowJobDetailsAsync(details);
      }
   }

   private async Task ShowJobDetailsAsync(JobDetailsSnapshot details)
   {
      JobSnapshot job = details.Job;
      string history = string.Join("\n\n", details.Events.Select(entry => $"{FormatTime(entry.RecordedAt)} · {FriendlyState(entry.Status)}\n{entry.Message}\n{(entry.Delivered ? "Reported to the server" : "Waiting to report to the server")}"));
      string text = $"Document: {job.Title ?? "Not recorded"}\nPrinter: {job.PrinterName ?? job.PrinterId ?? "Not recorded"}\nFormat: {job.Format ?? "Not recorded"}\nJob ID: {job.JobId}\nWindows job: {job.SpoolerJobId ?? "Not assigned"}\n\nHistory\n{history}";
      if(details.HistoryPruned)
      {
         text += "\n\nOlder history has been removed by the retention policy.";
      }
      var dialog = new ContentDialog { Title = "Print job details", CloseButtonText = "Close", XamlRoot = Content.XamlRoot, Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } } };
      await KofTwentyTwo.AppKit.WinUI.DialogGuard.ShowAsync(dialog);
   }

   private async Task RetryJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync("Print another copy?", $"{job.Title ?? job.JobId}\n{job.PrinterName ?? job.PrinterId}\n\nThis creates a new job. Check the printer first: the previous attempt may already have produced output.", "Print again"))
      {
         await RunActionAsync(new PipeRequest("retry-job", JobId: job.JobId, ConfirmDuplicateRisk: job.Status is "SPOOLED" or "OUTCOME_UNKNOWN" or "SPOOLER_COMPLETED"));
      }
   }

   private async Task CancelJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync("Cancel this job?", "Cancellation is best effort. Pages or labels already sent to the printer may still print.", "Cancel job"))
      {
         await RunActionAsync(new PipeRequest("cancel-job", JobId: job.JobId));
      }
   }

   private async Task AcknowledgeJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync("Mark this job reviewed?", "This clears its attention indicator. It does not print, cancel or change the recorded outcome.", "Mark reviewed"))
      {
         await RunActionAsync(new PipeRequest("acknowledge-job", JobId: job.JobId));
      }
   }
}
