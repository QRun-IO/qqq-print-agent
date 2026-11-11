/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Text.RegularExpressions;
using KofTwentyTwo.AppKit.Logging;


namespace Qqq.PrintAgent.Windows;

/// <summary>Serializes service and companion writes to the same AppKit daily activity log.</summary>
public sealed partial class AgentActivityLog : IAgentLog
{
   private readonly FileActivityLog _log;
   private LogSettings? _cachedSettings;
   private long _cachedSettingsAt;



   /// <summary>Opens the log under a "logs" folder of the state directory after validating that directory, so a hostile pre-existing folder is rejected before anything is written. Daily files are kept by this class, not by the inner log, hence retention 0 there.</summary>
   private AgentActivityLog(string directory)
   {
      Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(directory);
      _log = new FileActivityLog(Path.Combine(Path.GetFullPath(directory), "logs"), "qqq-print-agent", retentionDays: 0);
   }



   /// <summary>Uses the protected service directory; child files inherit its installed access controls.</summary>
   public static AgentActivityLog Create(string directory) => new(directory);



   /// <summary>
   /// True while the saved settings ask for debug detail. The settings file is re-read at most every two seconds, so a toggle
   /// in the companion reaches both processes quickly without a file read on every candidate debug line.
   /// </summary>
   public bool IsDebugEnabled
   {
      get
      {
         if(_cachedSettings is null || Stopwatch.GetElapsedTime(_cachedSettingsAt) > TimeSpan.FromSeconds(2))
         {
            try
            { _cachedSettings = ReadSettings(); }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
            { _cachedSettings ??= new LogSettings(); }
            _cachedSettingsAt = Stopwatch.GetTimestamp();
         }
         return _cachedSettings.DebugLogging;
      }
   }



   /// <summary>Writes a debug entry (request-level detail) only while debug logging is enabled; the entry goes through the same locked, redacting path.</summary>
   public void Debug(string message)
   {
      if(IsDebugEnabled)
      { Write(_log.Info, "[DEBUG] " + message); }
   }



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



   /// <summary>
   /// Redacts credential-shaped diagnostics; callers must still never log documents or credential payloads. What it
   /// recognises: a bearer credential; a named secret written as name and value, which includes the identity
   /// provider's ID-token hint, the PKCE code verifier and the session cookie under the product's own two names
   /// wherever they stand; a user name and password inside a web address; the value of a cookie header line and of an
   /// authorization header line of any scheme; and the one-time code and the state of a sign-in callback in the query
   /// or fragment of an address, quoted or percent-encoded. It is a second line of defense, not a parser: a secret in
   /// a shape it does not know passes through.
   /// </summary>
   public static string Redact(string message)
   {
      try
      {
         string bounded = message.Length <= 8192 ? message : message[..8192] + " [truncated]";
         string redacted = BearerCredential().Replace(bounded, "Bearer [REDACTED]");
         redacted = NamedCredential().Replace(redacted, "${prefix}[REDACTED]");
         redacted = UrlCredential().Replace(redacted, "${scheme}[REDACTED]@");
         redacted = CookieHeader().Replace(redacted, "${prefix}[REDACTED]");
         redacted = AuthorizationHeader().Replace(redacted, "${prefix}[REDACTED]");
         redacted = CallbackParameter().Replace(redacted, "${prefix}[REDACTED]");
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



   /// <summary>
   /// Matches a named secret such as a token, password, enrollment code, API key or document payload field written as
   /// name and value, keeping the name so the log still shows which field was hidden. Three names are values the
   /// product itself handles and must never show: <c>id_token_hint</c>, which the identity provider's sign-out request
   /// carries; <c>code_verifier</c>, the secret half of a browser sign-in; and <c>sessionUUID</c> and <c>sessionId</c>,
   /// the two names of the session cookie, which are redacted wherever they stand and not only inside a cookie header.
   /// </summary>
   [GeneratedRegex("""(?<prefix>["']?(?:deviceToken|enrollmentCode|access_token|refresh_token|id_token_hint|code_verifier|sessionUUID|sessionId|api[_-]?key|token|password|secret|contentBase64)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;&]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NamedCredential();



   /// <summary>Matches a user name and password embedded in a web address, keeping the scheme so the log still shows that a URL was involved.</summary>
   [GeneratedRegex("""(?<scheme>https?://)[^/\s:@]+:[^/\s@]+@""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex UrlCredential();



   /// <summary>
   /// Matches the value of a <c>Cookie:</c> or <c>Set-Cookie:</c> header line, to the end of the line, keeping the header's
   /// name. The whole value goes because a cookie header carries any number of name and value pairs and the session
   /// cookie's name is the server's choice; whatever follows on the same line is hidden with it, which errs on the safe side.
   /// </summary>
   [GeneratedRegex("""(?<prefix>\b(?:Set-)?Cookie[ \t]*:[ \t]*)[^\r\n]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex CookieHeader();



   /// <summary>
   /// Matches the value of an <c>Authorization:</c> or <c>Proxy-Authorization:</c> header line, to the end of the line,
   /// keeping the header's name. The whole value goes, whatever the scheme (Basic, Digest, Negotiate, a bare key),
   /// because the scheme decides where the credential is and a new scheme must not need a new pattern; whatever follows
   /// on the same line is hidden with it, which errs on the safe side, as it does for a cookie header. A quote between
   /// the name and the colon is allowed, so the header as a member of a JSON object is covered too.
   /// </summary>
   [GeneratedRegex("""(?<prefix>\b(?:Proxy-)?Authorization["']?[ \t]*:[ \t]*)[^\r\n]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex AuthorizationHeader();



   /// <summary>
   /// Matches the one-time authorization code and the state of a sign-in callback where they are parameters of an
   /// address: <c>code=</c> or <c>state=</c> straight after the <c>?</c>, <c>&amp;</c> or <c>#</c> that starts a
   /// parameter, keeping the name. The separator is required on purpose. "code" and "state" are ordinary words in a
   /// log ("status code: 401", "exit code=1", "state=Paused"), so neither is a keyword on its own the way "token" is.
   /// The value may be quoted, and the separator, the equals sign and the end of the value may be percent-encoded
   /// (<c>%3F</c>, <c>%26</c>, <c>%23</c>, <c>%3D</c>), which is how a callback looks inside another address.
   /// </summary>
   [GeneratedRegex("""(?<prefix>(?:[?&#]|%3F|%26|%23)(?:code|state)(?:=|%3D))(?:"[^"\r\n]*"|'[^'\r\n]*'|(?:(?!%26|%23)[^\s&#"'<>])+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex CallbackParameter();



   /// <summary>A shared exclusive file handle spans each append and retention pass, across processes and sessions.</summary>
   private void Write(Action<string> append, string message)
   {
      try
      {
         using FileStream ownership = AcquireOwnership();
         LogSettings settings = ReadSettings();
         PruneOwnedLogs(settings.RetentionDays);
         string redacted = Redact(message);
         RotateIfNeeded(redacted, settings);
         append(redacted);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         // Logging failures and daily size limits must not stop printing or the companion.
      }
   }
}
