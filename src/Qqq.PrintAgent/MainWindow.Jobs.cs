/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// Jobs page part of the main window: filtered, sorted, paged history fetched from the service and rendered as stable
/// rows. Queries are versioned so a slow reply for an older filter, page or sort can never overwrite a newer one.
/// </summary>
public sealed partial class MainWindow
{
   private readonly Dictionary<string, JobRowView> _jobRows = new(StringComparer.Ordinal);
   private int _jobQueryVersion;
   private int _jobOffset;
   private int _jobTotal;
   private const int JobPageSize = 50;



   /// <summary>
   /// Restarts the list at the first page when the search text or state filter changes, after a 300 ms pause that is
   /// abandoned if another change arrives, so typing a search does not query the service once per keystroke.
   /// </summary>
   private async void JobFilters_Changed(object sender, RoutedEventArgs e)
   {
      if(_initializing)
      {
         return;
      }
      InvalidateJobDetails();
      _jobOffset = 0;
      int version = ++_jobQueryVersion;
      await Task.Delay(300);
      if(version == _jobQueryVersion && !_updatesClosed)
      {
         await RefreshJobPageAsync();
      }
   }



   /// <summary>
   /// Asks the service for the current page and renders it. Every call takes a new query version, and a reply is used only if
   /// it is still the newest, no administration request started meanwhile and the page is still showing. Paging buttons are
   /// disabled during the query and restored from the real offset and total afterwards. A failure keeps the old rows but
   /// says they may be stale, and a successful refresh also reloads the expanded row's details.
   /// </summary>
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
         NeedsAttentionOnly: string.Equals(filter, "ATTENTION", StringComparison.Ordinal), Offset: _jobOffset, PageSize: JobPageSize, SortBy: _jobSortBy, Descending: _jobSortDescending);
      JobsPrevious.IsEnabled = false;
      JobsNext.IsEnabled = false;
      bool refreshDetails = false;
      try
      {
         string response = await _sendRequest(new PipeRequest("query-jobs", JobQuery: query));
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
         UpdateJobSortHeaders(query.SortBy, query.Descending);
         refreshDetails = true;
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
            JobsPrevious.IsEnabled = !_busy && _jobOffset > 0;
            JobsNext.IsEnabled = !_busy && _jobOffset + JobPageSize < _jobTotal;
         }
      }
      if(refreshDetails && version == _jobQueryVersion && !_updatesClosed)
      {
         await RefreshExpandedJobAsync();
      }
   }



   /// <summary>
   /// Renders one page: the rows, the "n of m" summary, the empty state, and a notice that "sent to printer" only means
   /// Windows accepted the job. The rendering flag brackets the row changes so selection events caused by the refresh itself are ignored.
   /// </summary>
   private void RenderJobPage(JobPage page)
   {
      _renderingJobPage = true;
      try
      {
         RenderJobRows(page);
      }
      finally
      {
         _renderingJobPage = false;
      }
      JobsSummary.Text = $"{page.Jobs.Count} of {page.TotalCount} matching jobs · page {page.Offset / JobPageSize + 1}";
      JobsEmpty.Visibility = page.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      JobsEmptyTitle.Text = "No matching jobs";
      JobsEmptyMessage.Text = "Try another prefix search or choose All states.";
      JobsNotice.Text = (page.ServerQueueLimited ? "Cloud queue preview is limited to 50; local history remains searchable. " : "")
         + "Sent to printer means Windows accepted the job. Check the printer to confirm physical output.";
      UpdateSelectedJob();
   }



   /// <summary>
   /// Updates the list in place instead of rebuilding it: rows for jobs no longer on the page are removed, new jobs get a
   /// row, and every row is moved to its position in the server's order. Reusing rows keeps the selection, any open details
   /// and the keyboard focus, which is restored if the refresh moved it.
   /// </summary>
   private void RenderJobRows(JobPage page)
   {
      string? selected = (JobsList.SelectedItem as JobRowView)?.Snapshot.JobId;
      var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
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
            row = new JobRowView(job, ToggleJobDetails, JobDetailsAsync, RetryJobAsync, CancelJobAsync, AcknowledgeJobAsync, RefreshJobDetailsAsync);
            _jobRows.Add(job.JobId, row);
            JobsList.Items.Insert(index, row);
         }
         row.Update(job, FriendlyState(job.Status), FormatJobCreated(job.CreatedAt));
         if(JobsList.Items.IndexOf(row) != index)
         {
            JobsList.Items.Remove(row);
            JobsList.Items.Insert(index, row);
         }
      }
      JobsList.SelectedItem = selected is not null && _jobRows.TryGetValue(selected, out JobRowView? selectedRow) ? selectedRow : null;
      if(focused?.XamlRoot is not null && !ReferenceEquals(focused, FocusManager.GetFocusedElement(Content.XamlRoot)))
      {
         _ = focused.Focus(FocusState.Programmatic);
      }
   }



   /// <summary>Formats a job's creation time in the user's local time and culture, or "Not recorded" for the historical rows that never had one.</summary>
   private static string FormatJobCreated(string? value) => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
      System.Globalization.DateTimeStyles.None, out DateTimeOffset timestamp) ? timestamp.ToLocalTime().ToString("G", System.Globalization.CultureInfo.CurrentCulture) : "Not recorded";



   /// <summary>Moves back one page, not below the first, and reloads.</summary>
   private async void PreviousJobs_Click(object sender, RoutedEventArgs e)
   {
      InvalidateJobDetails();
      _jobOffset = Math.Max(0, _jobOffset - JobPageSize);
      await RefreshJobPageAsync();
   }



   /// <summary>Moves forward one page only when more rows exist, so a double click cannot run past the end.</summary>
   private async void NextJobs_Click(object sender, RoutedEventArgs e)
   {
      if(_jobOffset + JobPageSize < _jobTotal)
      {
         InvalidateJobDetails();
         _jobOffset += JobPageSize;
         await RefreshJobPageAsync();
      }
   }



   /// <summary>
   /// Opens the full details window for a job. The window is busy while the service answers, the reply must be for the job
   /// that was asked about, and a failure shows a plain message. The dialog opens after the busy state is cleared, so it never holds the window disabled.
   /// </summary>
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
         string response = await _sendRequest(new PipeRequest("job-details", JobId: job.JobId));
         if(IsActionError(response))
         {
            return;
         }
         details = JsonSerializer.Deserialize<JobDetailsSnapshot>(response, PrintApiClient.JsonOptions);
         if(details?.Job is null || details.Events is null || !string.Equals(details.Job.JobId, job.JobId, StringComparison.Ordinal))
         {
            throw new JsonException("Job details do not match the selected row.");
         }
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         Feedback("Job details unavailable", "The service could not return this job. Refresh and try again.", InfoBarSeverity.Error);
      }
      finally
      {
         SetBusy(false);
      }
      if(details is not null && !_updatesClosed)
      {
         await ShowJobDetailsAsync(details);
      }
   }



   /// <summary>Shows the details in a wide dialog through the shared dialog guard so it cannot open over another dialog.</summary>
   private async Task ShowJobDetailsAsync(JobDetailsSnapshot details)
   {
      ContentDialog dialog = CreateWideDialog("Print job details", new JobDetailsView(details, FriendlyState, FormatTime));
      await KofTwentyTwo.AppKit.WinUI.DialogGuard.ShowAsync(dialog);
   }



   /// <summary>
   /// Asks the operator to confirm printing another copy, with a warning that the earlier attempt may already have produced
   /// output, and only then sends a retry request marked as an acknowledged duplicate risk. The service creates a new job; nothing is resubmitted silently.
   /// </summary>
   private async Task RetryJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync("Print another copy?", $"{job.Title ?? job.JobId}\n{job.PrinterName ?? job.PrinterId}\n\nThis creates a new job. Check the printer first: the previous attempt may already have produced output.", "Print again"))
      {
         await RunActionAsync(new PipeRequest("retry-job", JobId: job.JobId, ConfirmDuplicateRisk: true));
      }
   }



   /// <summary>Confirms, then asks the service to cancel; the prompt says cancellation is best effort because pages already sent to the printer may still print.</summary>
   private async Task CancelJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync("Cancel this job?", "Cancellation is best effort. Pages or labels already sent to the printer may still print.", "Cancel job"))
      {
         await RunActionAsync(new PipeRequest("cancel-job", JobId: job.JobId));
      }
   }



   /// <summary>Confirms, then records the operator's review of a failed or uncertain job; the prompt states that this changes no printing result.</summary>
   private async Task AcknowledgeJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync("Mark this job reviewed?", "This clears its attention indicator. It does not print, cancel or change the recorded outcome.", "Mark reviewed"))
      {
         await RunActionAsync(new PipeRequest("acknowledge-job", JobId: job.JobId));
      }
   }
}
