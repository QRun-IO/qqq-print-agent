/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text;

namespace Qqq.PrintAgent.Windows;

/// <summary>One whitelisted order for SQLite pages and the bounded in-memory cloud queue merge.</summary>
internal static class JobSorting
{
   internal static readonly string[] Fields = ["createdAt", "title", "printerName", "status", "format"];

   public static string SqlOrder(JobQuery query)
   {
      string direction = query.Descending ? " DESC" : " ASC";
      return SqlColumn(query.SortBy) + " COLLATE NOCASE" + direction + ",job_id COLLATE BINARY" + direction;
   }

   internal static string SqlColumn(string field) => field switch
   {
      "createdAt" => "created_at",
      "title" => "COALESCE(title,job_id)",
      "printerName" => "COALESCE(printer_name,printer_id,'Not recorded')",
      "status" => "(CASE COALESCE(state,'CLAIMED') WHEN 'READY' THEN 'Ready' WHEN 'OFFLINE' THEN 'Offline' WHEN 'PAUSED' THEN 'Paused' WHEN 'ERROR' THEN 'Needs attention' WHEN 'QUEUED' THEN 'Queued' WHEN 'CLAIMED' THEN 'Assigned to this computer' WHEN 'RECEIVED' THEN 'Received' WHEN 'SUBMITTING' THEN 'Sending to printer' WHEN 'SPOOLED' THEN 'Sent to printer' WHEN 'SPOOLER_COMPLETED' THEN 'Windows completed the job' WHEN 'FAILED' THEN 'Failed' WHEN 'OUTCOME_UNKNOWN' THEN 'Check printer before retrying' ELSE 'Status unavailable' END || CASE WHEN failure_acknowledged<>0 THEN ' · Reviewed' ELSE '' END)",
      "format" => "COALESCE(format,'—')",
      _ => throw new ArgumentException("Unknown job sort field.", nameof(field)),
   };

   public static int Compare(JobSnapshot left, JobSnapshot right, JobQuery query)
   {
      int comparison = CompareText(Value(left, query.SortBy), Value(right, query.SortBy), noCase: true);
      if(comparison == 0)
      {
         comparison = CompareText(left.JobId, right.JobId, noCase: false);
      }
      return query.Descending ? -comparison : comparison;
   }

   public static string? NormalizeCreatedAt(string? value)
      => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset instant)
         ? instant.ToString("O", CultureInfo.InvariantCulture) : null;

   public static bool MatchesPrefix(string? value, string prefix)
   {
      if(value is null)
      {
         return false;
      }
      byte[] text = Encoding.UTF8.GetBytes(value);
      byte[] search = Encoding.UTF8.GetBytes(prefix);
      if(text.Length < search.Length)
      {
         return false;
      }
      for(int index = 0; index < search.Length; index++)
      {
         if(FoldAscii(text[index]) != FoldAscii(search[index]))
         {
            return false;
         }
      }
      return true;
   }

   private static string? Value(JobSnapshot job, string sortBy) => sortBy switch
   {
      "createdAt" => job.CreatedAt,
      "title" => job.Title ?? job.JobId,
      "printerName" => job.PrinterName ?? job.PrinterId ?? "Not recorded",
      "status" => JobLabels.Status(job.Status, job.FailureAcknowledged),
      "format" => job.Format ?? "—",
      _ => throw new ArgumentException("Unknown job sort field.", nameof(sortBy)),
   };

   // SQLite's built-in NOCASE folds ASCII only and compares UTF-8 bytes. .NET's
   // OrdinalIgnoreCase folds additional Unicode characters and orders UTF-16 instead.
   private static int CompareText(string? left, string? right, bool noCase)
   {
      if(left is null || right is null)
      {
         return left is null ? right is null ? 0 : -1 : 1;
      }
      byte[] leftBytes = Encoding.UTF8.GetBytes(left);
      byte[] rightBytes = Encoding.UTF8.GetBytes(right);
      int count = Math.Min(leftBytes.Length, rightBytes.Length);
      for(int index = 0; index < count; index++)
      {
         byte first = noCase ? FoldAscii(leftBytes[index]) : leftBytes[index];
         byte second = noCase ? FoldAscii(rightBytes[index]) : rightBytes[index];
         int comparison = first.CompareTo(second);
         if(comparison != 0)
         {
            return comparison;
         }
         if(noCase && first == 0)
         {
            break;
         }
      }
      return leftBytes.Length.CompareTo(rightBytes.Length);
   }

   private static byte FoldAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
}
