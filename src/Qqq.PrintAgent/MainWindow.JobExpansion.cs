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

/// <summary>
/// Inline job details part of the main window: which job row is expanded, and loading its recent history on demand. A
/// reply is applied only if the row, its snapshot, the page and the request generation are all still the ones that asked, so a
/// slow answer can never appear under a different job.
/// </summary>
public sealed partial class MainWindow
{
   private bool _renderingJobPage;



   /// <summary>Expands the clicked job row, or collapses it when it is already the selected one, so the disclosure button toggles.</summary>
   private void ToggleJobDetails(JobRowView row)
      => JobsList.SelectedItem = ReferenceEquals(JobsList.SelectedItem, row) ? null : row;



   /// <summary>Reacts to a selection change by expanding the chosen row and loading its details, ignoring changes the window itself causes while it redraws the page or closes.</summary>
   private async void JobSelection_Changed(object sender, SelectionChangedEventArgs e)
   {
      if(_renderingJobPage || _updatesClosed)
      {
         return;
      }
      UpdateSelectedJob();
      await RefreshExpandedJobAsync();
   }



   /// <summary>Marks the selected row expanded and every other collapsed, and gives each row the current busy state so its action buttons match what the window allows.</summary>
   private void UpdateSelectedJob()
   {
      foreach(JobRowView row in _jobRows.Values)
      {
         row.SetExpanded(ReferenceEquals(JobsList.SelectedItem, row));
         row.SetBusy(_busy);
      }
   }



   /// <summary>Forces the row's details to be fetched again (its loaded snapshot is discarded first), for the Refresh details button.</summary>
   private async Task RefreshJobDetailsAsync(JobRowView row)
   {
      row.InvalidateDetails();
      await RefreshExpandedJobAsync();
   }



   /// <summary>
   /// Loads details for the expanded row, if it needs them. The reply must pass <c>CanApplyJobDetails</c> before it is shown, an error
   /// reply or a reply for a different job becomes a short message instead of wrong data, and the request is always marked finished
   /// so refresh can be enabled again.
   /// </summary>
   private async Task RefreshExpandedJobAsync()
   {
      if(_busy || _updatesClosed || JobsPage.Visibility != Visibility.Visible || JobsList.SelectedItem is not JobRowView row || !row.BeginDetails(out long generation))
      {
         return;
      }
      JobSnapshot requested = row.Snapshot;
      row.SetBusy(_busy);
      try
      {
         string response = await _sendRequest(new PipeRequest("job-details", JobId: requested.JobId));
         if(!CanApplyJobDetails(row, requested, generation))
         {
            return;
         }
         if(response.StartsWith("Error:", StringComparison.Ordinal))
         {
            row.SetMessage("Details unavailable. Use Refresh details to try again.");
            return;
         }
         JobDetailsSnapshot details = JsonSerializer.Deserialize<JobDetailsSnapshot>(response, PrintApiClient.JsonOptions) ?? throw new JsonException("Missing job details.");
         if(details.Job is null || details.Events is null || !string.Equals(details.Job.JobId, requested.JobId, StringComparison.Ordinal))
         {
            throw new JsonException("Job details do not match the selected row.");
         }
         row.SetDetails(details, FriendlyState, FormatTime);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         if(CanApplyJobDetails(row, requested, generation))
         {
            row.SetMessage("Details unavailable. Check the service, then refresh details.");
         }
      }
      finally
      {
         row.FinishDetails(generation);
         if(!_updatesClosed)
         {
            row.SetBusy(_busy);
         }
      }
   }



   /// <summary>Discards every row's loaded details and pending request, used when the page, filter, sort or busy state changes so no old detail can be shown against new data.</summary>
   private void InvalidateJobDetails()
   {
      foreach(JobRowView row in _jobRows.Values)
      {
         row.InvalidateDetails();
      }
   }



   /// <summary>True only when the window is open, the Jobs page is showing, the request is the row's latest, the row is still selected, expanded and unchanged, and it is still the row in the list.</summary>
   private bool CanApplyJobDetails(JobRowView row, JobSnapshot requested, long generation) => !_updatesClosed
      && JobsPage.Visibility == Visibility.Visible && row.IsCurrentDetailsRequest(generation)
      && ReferenceEquals(JobsList.SelectedItem, row) && row.Expanded && Equals(row.Snapshot, requested)
      && _jobRows.TryGetValue(requested.JobId, out JobRowView? current) && ReferenceEquals(current, row);
}
