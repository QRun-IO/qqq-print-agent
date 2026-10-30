using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>One bounded table row; full strings remain available through tooltips and Details.</summary>
internal sealed class JobRowView : Grid
{
   private readonly TextBlock[] _cells = [Cell(), Cell(), Cell(), Cell(), Cell()];
   internal JobSnapshot Snapshot { get; private set; }

   internal JobRowView(JobSnapshot job)
   {
      Snapshot = job;
      ColumnSpacing = 12;
      MinHeight = 40;
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
      for(int index = 0; index < _cells.Length; index++)
      {
         SetColumn(_cells[index], index);
         Children.Add(_cells[index]);
      }
   }

   internal void Update(JobSnapshot job, string status, string time)
   {
      Snapshot = job;
      string[] values = [job.Title ?? job.JobId, job.PrinterName ?? job.PrinterId ?? "Not recorded", status + (job.FailureAcknowledged ? " · Reviewed" : ""), job.Format ?? "—", time];
      for(int index = 0; index < _cells.Length; index++)
      {
         _cells[index].Text = values[index];
         ToolTipService.SetToolTip(_cells[index], values[index]);
      }
   }

   private static TextBlock Cell() => new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
}
