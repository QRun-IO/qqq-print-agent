using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private bool _renderingJobPage;

   private void ToggleJobDetails(JobRowView row)
      => JobsList.SelectedItem = ReferenceEquals(JobsList.SelectedItem, row) ? null : row;

   private async void JobSelection_Changed(object sender, SelectionChangedEventArgs e)
   {
      if(_renderingJobPage || _updatesClosed)
      {
         return;
      }
      UpdateSelectedJob();
      await RefreshExpandedJobAsync();
   }

   private void UpdateSelectedJob()
   {
      foreach(JobRowView row in _jobRows.Values)
      {
         row.SetExpanded(ReferenceEquals(JobsList.SelectedItem, row));
         row.SetBusy(_busy);
      }
   }

   private async Task RefreshJobDetailsAsync(JobRowView row)
   {
      row.InvalidateDetails();
      await RefreshExpandedJobAsync();
   }

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

   private void InvalidateJobDetails()
   {
      foreach(JobRowView row in _jobRows.Values)
      {
         row.InvalidateDetails();
      }
   }

   private bool CanApplyJobDetails(JobRowView row, JobSnapshot requested, long generation) => !_updatesClosed
      && JobsPage.Visibility == Visibility.Visible && row.IsCurrentDetailsRequest(generation)
      && ReferenceEquals(JobsList.SelectedItem, row) && row.Expanded && Equals(row.Snapshot, requested)
      && _jobRows.TryGetValue(requested.JobId, out JobRowView? current) && ReferenceEquals(current, row);
}
