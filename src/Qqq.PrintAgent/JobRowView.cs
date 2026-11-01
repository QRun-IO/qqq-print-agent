/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>A stable job row with one lazily loaded, bounded inline detail view.</summary>
internal sealed class JobRowView : Grid
{
   private readonly TextBlock[] _cells = [Cell(), Cell(), Cell(), Cell(), Cell()];
   private readonly Button _disclosure = new() { Width = 32, Height = 32, Padding = new Thickness(0) };
   private readonly FontIcon _chevron = new() { Glyph = "\uE76C", FontSize = 12 };
   private readonly StackPanel _details = new() { Spacing = 12, Margin = new Thickness(36, 12, 0, 16), Visibility = Visibility.Collapsed, Tag = "JobInlineDetails" };
   private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
   private readonly Button _full;
   private readonly Button _retry;
   private readonly Button _cancel;
   private readonly Button _review;
   private readonly Button _refresh;
   private JobSnapshot? _detailsFor;
   private readonly JobDetailsRequestTracker _detailRequests = new();
   internal JobSnapshot Snapshot { get; private set; }



   /// <summary>True while the inline details panel is showing; collapsing it discards the loaded body so a closed row holds no detail data.</summary>
   internal bool Expanded => _details.Visibility == Visibility.Visible;



   /// <summary>
   /// Builds a row once and reuses it across refreshes, so selection and expansion survive. It lays out five columns
   /// (document, printer, status, format, created), a disclosure button and the action buttons; the callbacks are supplied
   /// by the window, so the row itself never talks to the service.
   /// </summary>
   internal JobRowView(JobSnapshot job, Action<JobRowView> toggle, Func<JobSnapshot, Task> full,
      Func<JobSnapshot, Task> retry, Func<JobSnapshot, Task> cancel, Func<JobSnapshot, Task> review, Func<JobRowView, Task> refresh)
   {
      Snapshot = job;
      ColumnSpacing = 12;
      RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
      RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
      _disclosure.Content = _chevron;
      _disclosure.Command = new RelayCommand(() => toggle(this));
      AutomationProperties.SetAutomationId(_disclosure, "ToggleJobDetails");
      AddCells();
      StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
      _full = AddAction(actions, "Full details", "JobDetails", full);
      _retry = AddAction(actions, "Print again", "RetryJob", retry);
      _cancel = AddAction(actions, "Cancel job", "CancelJob", cancel);
      _review = AddAction(actions, "Mark reviewed", "ReviewJob", review);
      _refresh = AddAction(actions, "Refresh details", "RefreshJobDetails", _ => refresh(this));
      _details.Children.Add(actions);
      _details.Children.Add(_body);
      SetRow(_details, 1);
      SetColumnSpan(_details, 5);
      Children.Add(_details);
   }



   /// <summary>Places the disclosure button beside the document cell and the remaining cells in their columns, with automation identifiers on the document and created cells for tests and screen readers.</summary>
   private void AddCells()
   {
      Grid title = new() { ColumnSpacing = 4 };
      title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
      title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      title.Children.Add(_disclosure);
      SetColumn(_cells[0], 1);
      title.Children.Add(_cells[0]);
      Children.Add(title);
      AutomationProperties.SetAutomationId(_cells[0], "JobDocumentCell");
      AutomationProperties.SetAutomationId(_cells[4], "JobCreatedCell");
      for(int index = 1; index < _cells.Length; index++)
      {
         SetColumn(_cells[index], index);
         Children.Add(_cells[index]);
      }
   }



   /// <summary>Adds one action button that runs its callback against the row's current snapshot, not the one captured at construction, and tags it for automation.</summary>
   private Button AddAction(Panel actions, string label, string id, Func<JobSnapshot, Task> action)
   {
      Button button = new() { Content = label, Command = new AsyncRelayCommand(() => action(Snapshot)) };
      AutomationProperties.SetAutomationId(button, id);
      actions.Children.Add(button);
      return button;
   }



   /// <summary>
   /// Applies a refreshed snapshot in place. A changed job invalidates any loaded details, so stale detail text is never
   /// shown for new data. Cells are rewritten only when their text changed, which keeps tooltips and screen-reader focus
   /// stable, and the full creation time is kept as help text beside the shortened display.
   /// </summary>
   internal void Update(JobSnapshot job, string status, string time)
   {
      if(!Equals(Snapshot, job))
      {
         InvalidateDetails();
      }
      Snapshot = job;
      AutomationProperties.SetHelpText(_cells[4], job.CreatedAt ?? string.Empty);
      string[] values = [job.Title ?? job.JobId, job.PrinterName ?? job.PrinterId ?? "Not recorded", status + (job.FailureAcknowledged ? " · Reviewed" : ""), job.Format ?? "—", time];
      for(int index = 0; index < _cells.Length; index++)
      {
         if(!string.Equals(_cells[index].Text, values[index], StringComparison.Ordinal))
         {
            _cells[index].Text = values[index];
            ToolTipService.SetToolTip(_cells[index], values[index]);
         }
      }
      SetExpanded(Expanded);
   }



   /// <summary>
   /// Shows or hides the inline details and keeps the disclosure button's glyph, name, status and tooltip in step with
   /// that state. Collapsing clears the loaded body and invalidates its request, so an in-flight load cannot repopulate a closed row.
   /// </summary>
   internal void SetExpanded(bool expanded)
   {
      if(!expanded && Expanded)
      {
         _body.Content = null;
         InvalidateDetails();
      }
      _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
      _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
      string action = expanded ? "Collapse" : "Expand";
      AutomationProperties.SetName(_disclosure, $"{action} job details for {Snapshot.Title ?? Snapshot.JobId}");
      AutomationProperties.SetItemStatus(_disclosure, expanded ? "Expanded" : "Collapsed");
      ToolTipService.SetToolTip(_disclosure, $"{action} job details");
   }



   /// <summary>
   /// Enables each action only when it is valid for the job and no administration request is running: retry for finished
   /// or uncertain jobs, cancel for queued jobs or spooled ones with a spooler number, and review for unreviewed failures.
   /// A running detail load also disables refresh, so requests cannot overlap.
   /// </summary>
   internal void SetBusy(bool busy)
   {
      _full.IsEnabled = !busy;
      _refresh.IsEnabled = !busy && !_detailRequests.IsLoading;
      _retry.IsEnabled = !busy && Snapshot.Status is "FAILED" or "OUTCOME_UNKNOWN" or "SPOOLED" or "SPOOLER_COMPLETED";
      _cancel.IsEnabled = !busy && (Snapshot.Status is "QUEUED" || Snapshot.Status is "SPOOLED" && Snapshot.SpoolerJobId is not null);
      _review.IsEnabled = !busy && Snapshot is { FailureAcknowledged: false, Status: "FAILED" or "OUTCOME_UNKNOWN" };
   }



   /// <summary>
   /// Starts loading details when the row is open, the snapshot has not already been loaded, and no load is running. It
   /// returns the request's generation so a late reply can be checked against it, and shows a loading message meanwhile.
   /// </summary>
   internal bool BeginDetails(out long generation)
   {
      generation = 0;
      if(!Expanded || Equals(_detailsFor, Snapshot) || !_detailRequests.TryBegin(out generation))
      {
         return false;
      }
      _detailsFor = Snapshot;
      SetMessage("Loading job details…");
      return true;
   }



   /// <summary>True when the generation is still the latest request, so a late reply for an older one is dropped.</summary>
   internal bool IsCurrentDetailsRequest(long generation) => _detailRequests.IsCurrent(generation);



   /// <summary>Marks the request finished so refresh can be enabled again.</summary>
   internal void FinishDetails(long generation) => _detailRequests.Complete(generation);



   /// <summary>Forgets which snapshot was loaded and cancels the pending request's claim to update this row.</summary>
   internal void InvalidateDetails()
   {
      _detailsFor = null;
      _detailRequests.Invalidate();
   }



   /// <summary>Shows loaded details in the compact form used inline.</summary>
   internal void SetDetails(JobDetailsSnapshot details, Func<string, string> state, Func<string?, string> time)
      => _body.Content = new JobDetailsView(details, state, time, compact: true);



   /// <summary>Replaces the detail body with a plain wrapped message, such as loading or an error.</summary>
   internal void SetMessage(string message) => _body.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };



   /// <summary>Creates one single-line, centered, ellipsized text cell.</summary>
   private static TextBlock Cell() => new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
}
