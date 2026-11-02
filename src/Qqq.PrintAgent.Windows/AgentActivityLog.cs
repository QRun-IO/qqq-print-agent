/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;
using KofTwentyTwo.AppKit.Logging;


namespace Qqq.PrintAgent.Windows;

/// <summary>Serializes service and companion writes to the same AppKit daily activity log.</summary>
public sealed partial class AgentActivityLog : IActivityLog
{
   private readonly FileActivityLog _log;



   /// <summary>Opens the log under a "logs" folder of the state directory after validating that directory, so a hostile pre-existing folder is rejected before anything is written. Daily files are kept by this class, not by the inner log, hence retention 0 there.</summary>
   private AgentActivityLog(string directory)
   {
      Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(directory);
      _log = new FileActivityLog(Path.Combine(Path.GetFullPath(directory), "logs"), "qqq-print-agent", retentionDays: 0);
   }



   /// <summary>Uses the protected service directory; child files inherit its installed access controls.</summary>
   public static IActivityLog Create(string directory) => new AgentActivityLog(directory);



   /// <summary>The folder holding the dated log files, as the shared AppKit viewer needs to find them.</summary>
   public string LogDirectory => _log.LogDirectory;



   /// <summary>Today's log file path, used by the Settings page to show its size and by the cap check.</summary>
   public string CurrentLogFilePath => _log.CurrentLogFilePath;



   /// <summary>Writes an informational entry through the shared, locked and redacting write path.</summary>
   public void Info(string message) => Write(_log.Info, message);



   /// <summary>Writes a warning entry through the shared, locked and redacting write path.</summary>
   public void Warning(string message) => Write(_log.Warning, message);



   /// <summary>Writes an error entry; when an exception is given its full text follows the message, and the whole entry is redacted before it is written.</summary>
   public void Error(string message, Exception? exception = null)
      => Write(text => _log.Error(text), exception is null ? message : message + Environment.NewLine + exception);



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



   /// <summary>Matches an HTTP bearer credential (the word Bearer and its token) so it can be replaced before anything is logged. The match has a one second limit so a crafted message cannot stall logging.</summary>
   [GeneratedRegex("""\bBearer\s+[^\s,;"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex BearerCredential();



   /// <summary>Matches a named secret such as a token, password, enrollment code, API key or document payload field written as name and value, keeping the name so the log still shows which field was hidden.</summary>
   [GeneratedRegex("""(?<prefix>["']?(?:deviceToken|enrollmentCode|access_token|refresh_token|api[_-]?key|token|password|secret|contentBase64)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;&]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NamedCredential();



   /// <summary>Matches a user name and password embedded in a web address, keeping the scheme so the log still shows that a URL was involved.</summary>
   [GeneratedRegex("""(?<scheme>https?://)[^/\s:@]+:[^/\s@]+@""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex UrlCredential();



   /// <summary>A shared exclusive file handle spans each append and retention pass, across processes and sessions.</summary>
   private void Write(Action<string> append, string message)
   {
      try
      {
         using FileStream ownership = AcquireOwnership();
         LogSettings settings = ReadSettings();
         PruneOwnedLogs(settings.RetentionDays);
         string redacted = Redact(message);
         if(CanAppend(redacted, settings))
         {
            append(redacted);
         }
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         // Logging failures and daily size limits must not stop printing or the companion.
      }
   }
}
