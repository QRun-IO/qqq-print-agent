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



   private AgentActivityLog(string directory)
   {
      Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(directory);
      _log = new FileActivityLog(Path.Combine(Path.GetFullPath(directory), "logs"), "qqq-print-agent", retentionDays: 0);
   }



   /// <summary>Uses the protected service directory; child files inherit its installed access controls.</summary>
   public static IActivityLog Create(string directory) => new AgentActivityLog(directory);

   /// <inheritdoc/>
   public string LogDirectory => _log.LogDirectory;

   /// <inheritdoc/>
   public string CurrentLogFilePath => _log.CurrentLogFilePath;

   /// <inheritdoc/>
   public void Info(string message) => Write(_log.Info, message);

   /// <inheritdoc/>
   public void Warning(string message) => Write(_log.Warning, message);

   /// <inheritdoc/>
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

   [GeneratedRegex("""\bBearer\s+[^\s,;"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex BearerCredential();

   [GeneratedRegex("""(?<prefix>["']?(?:deviceToken|enrollmentCode|access_token|refresh_token|api[_-]?key|token|password|secret|contentBase64)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;&]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NamedCredential();

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
