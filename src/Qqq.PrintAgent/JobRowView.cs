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
   internal bool Expanded => _details.Visibility == Visibility.Visible;

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

   private Button AddAction(Panel actions, string label, string id, Func<JobSnapshot, Task> action)
   {
      Button button = new() { Content = label, Command = new AsyncRelayCommand(() => action(Snapshot)) };
      AutomationProperties.SetAutomationId(button, id);
      actions.Children.Add(button);
      return button;
   }

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

   internal void SetBusy(bool busy)
   {
      _full.IsEnabled = !busy;
      _refresh.IsEnabled = !busy && !_detailRequests.IsLoading;
      _retry.IsEnabled = !busy && Snapshot.Status is "FAILED" or "OUTCOME_UNKNOWN" or "SPOOLED" or "SPOOLER_COMPLETED";
      _cancel.IsEnabled = !busy && (Snapshot.Status is "QUEUED" || Snapshot.Status is "SPOOLED" && Snapshot.SpoolerJobId is not null);
      _review.IsEnabled = !busy && Snapshot is { FailureAcknowledged: false, Status: "FAILED" or "OUTCOME_UNKNOWN" };
   }

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

   internal bool IsCurrentDetailsRequest(long generation) => _detailRequests.IsCurrent(generation);
   internal void FinishDetails(long generation) => _detailRequests.Complete(generation);
   internal void InvalidateDetails()
   {
      _detailsFor = null;
      _detailRequests.Invalidate();
   }
   internal void SetDetails(JobDetailsSnapshot details, Func<string, string> state, Func<string?, string> time)
      => _body.Content = new JobDetailsView(details, state, time, compact: true);
   internal void SetMessage(string message) => _body.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

   private static TextBlock Cell() => new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
}
