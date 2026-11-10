/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Qqq.PrintAgent.Presentation.Jobs;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// Jobs page part of the main window: filtered, sorted, paged history fetched from the service and rendered as stable
/// rows. Where the page is in the history, and which reply is still the newest, is kept by <see cref="JobListState"/>; the
/// texts and the row contents come from the presentation assembly, and this part moves them into controls.
/// </summary>
public sealed partial class MainWindow
{
   private readonly Dictionary<string, JobRowView> _jobRows = new(StringComparer.Ordinal);
   private readonly JobListState _jobs = new();



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
      int version = _jobs.FiltersChanged();
      await Task.Delay(300);
      if(_jobs.IsCurrent(version) && !_updatesClosed)
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
      JobQuery query = _jobs.BeginQuery(JobSearch.Text, (JobStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string, out int version);
      int mutationVersion = _mutationVersion;
      JobsPrevious.IsEnabled = false;
      JobsNext.IsEnabled = false;
      bool refreshDetails = false;
      try
      {
         ServiceResult<JobPage> result = await _service.QueryJobsAsync(query);
         refreshDetails = ApplyJobPage(result, query, version, mutationVersion);
      }
      finally
      {
         if(_jobs.IsCurrent(version) && !_updatesClosed)
         {
            JobsPrevious.IsEnabled = _jobs.CanGoPrevious(_busy);
            JobsNext.IsEnabled = _jobs.CanGoNext(_busy);
         }
      }
      if(refreshDetails && _jobs.IsCurrent(version) && !_updatesClosed)
      {
         await RefreshExpandedJobAsync();
      }
   }



   /// <summary>
   /// Applies the answer to a job query and says whether a page was rendered. No usable answer only changes the count line,
   /// and only if this query is still the newest. A reply (a page, an error or something unreadable) is dropped when it is no
   /// longer the newest or an administration request started meanwhile; otherwise an error is shown with a note that the
   /// rows on screen may be stale, and a page replaces the rows and confirms the sort the service applied.
   /// </summary>
   private bool ApplyJobPage(ServiceResult<JobPage> result, JobQuery query, int version, int mutationVersion)
   {
      bool current = _jobs.IsCurrent(version) && !_updatesClosed;
      if(result.Outcome == ServiceOutcome.Unavailable && result.Failure != ServiceFailure.UnreadableReply)
      {
         if(current)
         {
            JobsSummary.Text = JobPageSummary.Failure(rejected: false);
         }
         return false;
      }
      if(!current || _busy || mutationVersion != _mutationVersion)
      {
         return false;
      }
      if(!result.Succeeded)
      {
         if(result.Outcome == ServiceOutcome.Rejected)
         {
            Feedback(ShellFeedback.ActionRejected(result.Message));
         }
         JobsSummary.Text = JobPageSummary.Failure(result.Outcome == ServiceOutcome.Rejected);
         return false;
      }
      _jobs.Apply(result.Payload);
      RenderJobPage(result.Payload);
      UpdateJobSortHeaders(query.SortBy, query.Descending);
      return true;
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
      JobsSummary.Text = JobPageSummary.Text(page);
      JobsEmpty.Visibility = page.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      JobsEmptyTitle.Text = JobPageSummary.EmptyTitle;
      JobsEmptyMessage.Text = JobPageSummary.EmptyMessage;
      JobsNotice.Text = JobPageSummary.Notice(page);
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
         row.Update(job, JobRow.From(job, _time));
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



   /// <summary>Moves back one page, not below the first, and reloads.</summary>
   private async void PreviousJobs_Click(object sender, RoutedEventArgs e)
   {
      InvalidateJobDetails();
      _jobs.Previous();
      await RefreshJobPageAsync();
   }



   /// <summary>Moves forward one page only when more rows exist, so a double click cannot run past the end.</summary>
   private async void NextJobs_Click(object sender, RoutedEventArgs e)
   {
      if(_jobs.Next())
      {
         InvalidateJobDetails();
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
      JobDetailsSnapshot? details;
      try
      {
         ServiceResult<JobDetailsSnapshot> result = await _service.JobDetailsAsync(job.JobId);
         details = result.Payload;
         if(result.Outcome == ServiceOutcome.Rejected)
         {
            Feedback(ShellFeedback.ActionRejected(result.Message));
         }
         else if(!result.Succeeded)
         {
            Feedback(JobActions.DetailsUnavailable);
         }
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
      ContentDialog dialog = CreateWideDialog("Print job details", new JobDetailsView(JobDetailsContent.From(details, _time, compact: false)));
      await KofTwentyTwo.AppKit.WinUI.DialogGuard.ShowAsync(dialog);
   }



   /// <summary>
   /// Asks the operator to confirm printing another copy, with a warning that the earlier attempt may already have produced
   /// output, and only then sends a retry request marked as an acknowledged duplicate risk. The service creates a new job; nothing is resubmitted silently.
   /// </summary>
   private async Task RetryJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync(JobActions.RetryPrompt(job)))
      {
         await RunActionAsync(ServiceRequests.RetryJob(job.JobId));
      }
   }



   /// <summary>Confirms, then asks the service to cancel; the prompt says cancellation is best effort because pages already sent to the printer may still print.</summary>
   private async Task CancelJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync(JobActions.CancelPrompt))
      {
         await RunActionAsync(ServiceRequests.CancelJob(job.JobId));
      }
   }



   /// <summary>Confirms, then records the operator's review of a failed or uncertain job; the prompt states that this changes no printing result.</summary>
   private async Task AcknowledgeJobAsync(JobSnapshot job)
   {
      if(await ConfirmAsync(JobActions.ReviewPrompt))
      {
         await RunActionAsync(ServiceRequests.AcknowledgeJob(job.JobId));
      }
   }
}
