/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>Readable, bounded job summary and event table; all diagnostic values remain selectable.</summary>
internal sealed class JobDetailsView : StackPanel
{
   internal JobDetailsView(JobDetailsSnapshot details, Func<string, string> state, Func<string?, string> time, bool compact = false)
   {
      Spacing = 20;
      JobSnapshot job = details.Job;
      Children.Add(Summary(job, state, time, compact));
      if(compact)
      {
         Children.Add(new Expander
         {
            Header = "Recent events (up to 5)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = History(details.Events.TakeLast(5).ToArray(), state, time),
         });
         Children.Add(Text("Use Full details for the retained event history. Sent to printer is spool acceptance, not confirmed paper output."));
      }
      else
      {
         Children.Add(Text("Event history", heading: true));
         Children.Add(Text("The service returns up to 200 retained events. Sent to printer means Windows accepted the job, not confirmed physical output."));
         Children.Add(History(details.Events.Take(200).ToArray(), state, time));
      }
      if(details.HistoryPruned)
      {
         Children.Add(Text("Older event detail has been removed by the retention policy. The job summary remains available."));
      }
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, compact ? "JobInlineDetailsContent" : "JobDetailsContent");
   }

   private static StackPanel Summary(JobSnapshot job, Func<string, string> state, Func<string?, string> time, bool compact)
   {
      StackPanel summary = new() { Spacing = 12 };
      AddField(summary, "Document", job.Title ?? "Not recorded", compact);
      AddField(summary, "Printer", job.PrinterName ?? job.PrinterId ?? "Not recorded", compact);
      AddField(summary, "Status", state(job.Status) + (job.FailureAcknowledged ? " · Reviewed" : ""));
      AddField(summary, "Created", job.CreatedAt is null ? "Not recorded" : time(job.CreatedAt));
      AddField(summary, "Last update", time(job.UpdatedAt));
      AddField(summary, "Outcome", string.IsNullOrWhiteSpace(job.Message) ? "No additional outcome message recorded." : job.Message, compact);
      AddField(summary, "Format", job.Format ?? "Not recorded");
      AddField(summary, "Job ID", job.JobId);
      AddField(summary, "Windows job", job.SpoolerJobId ?? "Not assigned");
      return summary;
   }

   private static void AddField(Panel parent, string label, string value, bool compact = false)
   {
      Grid field = new() { ColumnSpacing = 16 };
      field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
      field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      TextBlock title = Text(label, heading: true);
      TextBlock content = Text(value);
      if(compact)
      {
         content.MaxLines = 3;
         content.TextTrimming = TextTrimming.CharacterEllipsis;
         ToolTipService.SetToolTip(content, value);
      }
      Grid.SetColumn(content, 1);
      field.Children.Add(title);
      field.Children.Add(content);
      parent.Children.Add(field);
   }

   private static StackPanel History(JobEventSnapshot[] events, Func<string, string> state, Func<string?, string> time)
   {
      StackPanel history = new() { Spacing = 12 };
      history.Children.Add(EventRow("Time", "Status", "Message and delivery", heading: true));
      foreach(JobEventSnapshot entry in events)
      {
         string message = $"{entry.Message ?? "No message"}\n{(entry.Delivered ? "Reported to server" : "Waiting to report")}\nEvent: {entry.EventId}";
         if(entry.SpoolerJobId is not null)
         {
            message += $"\nWindows job: {entry.SpoolerJobId}";
         }
         history.Children.Add(EventRow(time(entry.RecordedAt), state(entry.Status), message));
      }
      if(events.Length == 0)
      {
         history.Children.Add(Text("No retained local events are available for this job."));
      }
      return history;
   }

   private static Grid EventRow(string timestamp, string status, string message, bool heading = false)
   {
      Grid row = new() { ColumnSpacing = 16 };
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      string[] values = [timestamp, status, message];
      for(int index = 0; index < values.Length; index++)
      {
         TextBlock cell = Text(values[index], heading);
         Grid.SetColumn(cell, index);
         row.Children.Add(cell);
      }
      return row;
   }

   private static TextBlock Text(string text, bool heading = false) => new()
   {
      Text = text,
      TextWrapping = TextWrapping.Wrap,
      IsTextSelectionEnabled = true,
      FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
   };
}
