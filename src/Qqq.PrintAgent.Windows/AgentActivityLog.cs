/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Text.RegularExpressions;
using KofTwentyTwo.AppKit.Logging;


namespace Qqq.PrintAgent.Windows;

/// <summary>Serializes service and companion writes to the same AppKit daily activity log.</summary>
public sealed partial class AgentActivityLog : IActivityLog
{
   private readonly FileActivityLog _log;



   private AgentActivityLog(string directory)
   {
      _log = new FileActivityLog(Path.Combine(Path.GetFullPath(directory), "logs"), "qqq-print-agent");
   }



   /// <summary>Uses the protected service directory; child files inherit its installed access controls.</summary>
   public static IActivityLog Create(string directory) => new AgentActivityLog(directory);

   /// <inheritdoc/>
   public string LogDirectory => _log.LogDirectory;

   /// <inheritdoc/>
   public string CurrentLogFilePath => _log.CurrentLogFilePath;

   /// <inheritdoc/>
   public void Info(string message) => Write(() => _log.Info(Redact(message)));

   /// <inheritdoc/>
   public void Warning(string message) => Write(() => _log.Warning(Redact(message)));

   /// <inheritdoc/>
   public void Error(string message, Exception? exception = null)
      => Write(() => _log.Error(Redact(exception is null ? message : message + Environment.NewLine + exception)));



   /// <summary>Redacts credential-shaped diagnostics; callers must still never log documents or credential payloads.</summary>
   public static string Redact(string message)
   {
      try
      {
         string bounded = message.Length <= 8192 ? message : message[..8192] + " [truncated]";
         string redacted = BearerCredential().Replace(bounded, "Bearer [REDACTED]");
         redacted = NamedCredential().Replace(redacted, "${prefix}[REDACTED]");
         redacted = UrlCredential().Replace(redacted, "${scheme}[REDACTED]@");
         // Continuations cannot forge another timestamped AppKit log entry.
         return redacted.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\n  ", StringComparison.Ordinal);
      }
      catch(RegexMatchTimeoutException)
      {
         return "[Log entry omitted because credential redaction timed out.]";
      }
   }

   [GeneratedRegex("""\bBearer\s+[^\s,;"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex BearerCredential();

   [GeneratedRegex("""(?<prefix>["']?(?:deviceToken|enrollmentCode|access_token|refresh_token|api[_-]?key|token|password|secret|contentBase64)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;&]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NamedCredential();

   [GeneratedRegex("""(?<scheme>https?://)[^/\s:@]+:[^/\s@]+@""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex UrlCredential();



   /// <summary>A shared exclusive file handle spans each append and retention pass, across processes and sessions.</summary>
   private void Write(Action append)
   {
      long started = Stopwatch.GetTimestamp();
      while(true)
      {
         try
         {
            Directory.CreateDirectory(LogDirectory);
            using FileStream ownership = new(Path.Combine(LogDirectory, "activity.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            append();
            return;
         }
         catch(IOException) when(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
         {
            Thread.Sleep(10);
         }
         catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
         {
            // Activity logging must never stop printing or the companion when the disk is unavailable.
            return;
         }
      }
   }
}
