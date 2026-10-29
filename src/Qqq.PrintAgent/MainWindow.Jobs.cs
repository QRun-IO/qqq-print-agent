/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private string? _jobViewSignature;

   private void JobFilters_Changed(object sender, RoutedEventArgs e)
   {
      if(!_initializing)
      {
         RefreshJobList();
      }
   }

   private void RefreshJobList()
   {
      string search = JobSearch.Text.Trim();
      string filter = (JobStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "ALL";
      string signature = search + filter + JsonSerializer.Serialize(_jobs, PrintApiClient.JsonOptions);
      if(string.Equals(signature, _jobViewSignature, StringComparison.Ordinal))
      {
         return;
      }
      _jobViewSignature = signature;
      JobSnapshot[] visible = _jobs.Where(job => MatchesJob(job, search, filter)).ToArray();
      JobsList.Items.Clear();
      foreach(JobSnapshot job in visible)
      {
         JobsList.Items.Add(CreateJobRow(job));
      }
      JobsSummary.Text = $"{visible.Length} of {_jobs.Count} recent jobs";
      JobsEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
      JobsEmptyTitle.Text = _jobs.Count == 0 ? "No jobs yet" : "No matching jobs";
      JobsEmptyMessage.Text = _jobs.Count == 0 ? "Jobs sent from QQQ appear here once the service receives them." : "Try another search or choose All jobs.";
   }

   private static bool MatchesJob(JobSnapshot job, string search, string filter)
   {
      bool statusMatches = filter switch
      {
         "ATTENTION" => job.Status is "FAILED" or "OUTCOME_UNKNOWN" && !job.FailureAcknowledged,
         "ACTIVE" => job.Status is "QUEUED" or "CLAIMED" or "RECEIVED" or "SUBMITTING",
         "SPOOLED" => job.Status is "SPOOLED" or "SPOOLER_COMPLETED",
         _ => true,
      };
      string searchable = $"{job.Title} {job.PrinterName} {job.PrinterId} {job.JobId} {job.Message}";
      return statusMatches && searchable.Contains(search, StringComparison.OrdinalIgnoreCase);
   }

   private StackPanel CreateJobRow(JobSnapshot job)
   {
      StackPanel row = new() { Spacing = 8, Margin = new Thickness(4, 12, 4, 12) };
      row.Children.Add(new TextBlock { Text = job.Title ?? "Document title not recorded", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
      row.Children.Add(new TextBlock { Text = $"{job.PrinterName ?? job.PrinterId ?? "Printer not recorded"} · {job.Format ?? "Format not recorded"}", Foreground = SecondaryBrush(), TextWrapping = TextWrapping.Wrap });
      row.Children.Add(new TextBlock { Text = $"{FriendlyState(job.Status)}{(job.FailureAcknowledged ? " · Reviewed" : "")} · {FormatTime(job.UpdatedAt)}", TextWrapping = TextWrapping.Wrap });
      if(!string.IsNullOrWhiteSpace(job.Message))
      {
         row.Children.Add(new TextBlock { Text = job.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = SecondaryBrush() });
      }
      StackPanel commands = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
      commands.Children.Add(JobButton("Details", job, JobDetailsAsync));
      if(job.Status is "FAILED" or "OUTCOME_UNKNOWN" or "SPOOLED" or "SPOOLER_COMPLETED")
      {
         commands.Children.Add(JobButton("Print again", job, RetryJobAsync));
      }
      if(job.Status is "QUEUED" || job.Status is "SPOOLED" && job.SpoolerJobId is not null)
      {
         commands.Children.Add(JobButton("Cancel job", job, CancelJobAsync));
      }
      if(job.Status is "FAILED" or "OUTCOME_UNKNOWN" && !job.FailureAcknowledged)
      {
         commands.Children.Add(JobButton("Mark reviewed", job, AcknowledgeJobAsync));
      }
      row.Children.Add(commands);
      return row;
   }

   private static Button JobButton(string label, JobSnapshot job, Func<JobSnapshot, Task> handler)
   {
      Button button = new() { Content = label, Tag = job };
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{label}: {job.Title ?? job.JobId}");
      button.Command = new AsyncRelayCommand(() => handler(job));
      return button;
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
      string history = string.Join("\n\n", details.Events.Select(entry => $"{FormatTime(entry.RecordedAt)} · {FriendlyState(entry.Status)}\n{entry.Message}\n{(entry.Delivered ? "Reported to QQQ" : "Waiting to report to QQQ")}"));
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
