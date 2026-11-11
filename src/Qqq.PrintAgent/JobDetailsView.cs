/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Presentation.Jobs;

namespace Qqq.PrintAgent;

/// <summary>Readable, bounded job summary and event table; all diagnostic values remain selectable.</summary>
internal sealed class JobDetailsView : StackPanel
{
   /// <summary>
   /// Lays out prepared job details: the field summary, then the event history. The compact form used inline puts the events in
   /// an expander; the full form shows them under a heading. Which fields, how many events and every sentence are decided by
   /// <see cref="JobDetailsContent"/>; this view only arranges them.
   /// </summary>
   internal JobDetailsView(JobDetailsContent content, bool compact = false)
   {
      Spacing = 20;
      Children.Add(Summary(content));
      if(compact)
      {
         Expander events = new()
         {
            Header = content.EventsHeading,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = History(content),
         };
         Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(events, "JobEventHistory");
         Children.Add(events);
         Children.Add(Text(content.EventsNote));
      }
      else
      {
         Children.Add(Text(content.EventsHeading, heading: true));
         Children.Add(Text(content.EventsNote));
         Children.Add(History(content));
      }
      if(content.PrunedNote is not null)
      {
         Children.Add(Text(content.PrunedNote));
      }
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, compact ? "JobInlineDetailsContent" : "JobDetailsContent");
   }



   /// <summary>Builds the labelled summary rows for one job.</summary>
   private static StackPanel Summary(JobDetailsContent content)
   {
      StackPanel summary = new() { Spacing = 12 };
      foreach(JobDetailField field in content.Fields)
      {
         AddField(summary, field);
      }
      return summary;
   }



   /// <summary>Adds one label and value row. A shortened value is limited to three lines with the full text in a tooltip, so a long outcome message cannot push the rest of the table out of view.</summary>
   private static void AddField(Panel parent, JobDetailField detail)
   {
      Grid field = new() { ColumnSpacing = 16 };
      field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
      field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      TextBlock title = Text(detail.Label, heading: true);
      TextBlock content = Text(detail.Value);
      if(detail.Shortened)
      {
         content.MaxLines = 3;
         content.TextTrimming = TextTrimming.CharacterEllipsis;
         ToolTipService.SetToolTip(content, detail.Value);
      }
      Grid.SetColumn(content, 1);
      field.Children.Add(title);
      field.Children.Add(content);
      parent.Children.Add(field);
   }



   /// <summary>Builds the event table: a heading row, then one row per event, and a sentence instead of an empty table when no events are retained.</summary>
   private static StackPanel History(JobDetailsContent content)
   {
      StackPanel history = new() { Spacing = 12 };
      history.Children.Add(EventRow(content.EventColumns, heading: true));
      foreach(JobEventRow entry in content.Events)
      {
         history.Children.Add(EventRow(entry));
      }
      if(content.NoEventsNote is not null)
      {
         history.Children.Add(Text(content.NoEventsNote));
      }
      return history;
   }



   /// <summary>Builds one three-column row (time, status, message) of the event table, bold for the heading row.</summary>
   private static Grid EventRow(JobEventRow entry, bool heading = false)
   {
      Grid row = new() { ColumnSpacing = 16 };
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      string[] values = [entry.Time, entry.Status, entry.Message];
      for(int index = 0; index < values.Length; index++)
      {
         TextBlock cell = Text(values[index], heading);
         Grid.SetColumn(cell, index);
         row.Children.Add(cell);
      }
      return row;
   }



   /// <summary>Creates a wrapping text block that can be selected and copied, bold when it is a heading, so every diagnostic value stays copyable.</summary>
   private static TextBlock Text(string text, bool heading = false) => new()
   {
      Text = text,
      TextWrapping = TextWrapping.Wrap,
      IsTextSelectionEnabled = true,
      FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
   };
}
