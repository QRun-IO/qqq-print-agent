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
   /// recognises:
   /// <list type="bullet">
   /// <item>a bearer credential;</item>
   /// <item>a named secret written as name and value (<see cref="NamedCredential"/>), also with the equals sign percent-encoded (<see cref="EncodedNamedCredential"/>);</item>
   /// <item>a user name and password inside a web address;</item>
   /// <item>the value of a cookie header line and of an authorization header line of any scheme, on the header's own line or, when that line holds nothing after the name, on the next;</item>
   /// <item>the one-time code and the state of a sign-in callback in the query or fragment of an address, quoted or percent-encoded;</item>
   /// <item>a member or parameter named <c>code</c> in a message that also names a code verifier, which is the code-exchange request (<see cref="ExchangedCode"/>).</item>
   /// </list>
   /// It is a second line of defense, not a parser: a secret in a shape it does not know passes through, and the
   /// service log's tests record the shapes that are known to pass.
   /// </summary>
   public static string Redact(string message)
   {
      try
      {
         string bounded = message.Length <= 8192 ? message : message[..8192] + " [truncated]";
         string redacted = BearerCredential().Replace(bounded, "Bearer [REDACTED]");
         redacted = NamedCredential().Replace(redacted, "${prefix}[REDACTED]");
         redacted = EncodedNamedCredential().Replace(redacted, "${prefix}[REDACTED]");
         redacted = UrlCredential().Replace(redacted, "${scheme}[REDACTED]@");
         redacted = CookieHeader().Replace(redacted, "${prefix}[REDACTED]");
         redacted = AuthorizationHeader().Replace(redacted, "${prefix}[REDACTED]");
         redacted = CallbackParameter().Replace(redacted, "${prefix}[REDACTED]");
         // The verifier's name is looked for in the message as it was given, not in the redacted text: a rule above
         // may have hidden the name together with the value it stood in (a header's line, another secret's quotes).
         redacted = CodeVerifierName().IsMatch(bounded) ? ExchangedCode().Replace(redacted, "${prefix}[REDACTED]") : redacted;
         // Continuations cannot forge another timestamped AppKit log entry.
         return redacted.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\n  ", StringComparison.Ordinal);
      }
      catch(RegexMatchTimeoutException)
      {
         return "[Log entry omitted because credential redaction timed out.]";
      }
   }



   /// <summary>
   /// The names of the named secrets, as the one alternation <see cref="NamedCredential"/> and
   /// <see cref="EncodedNamedCredential"/> share; letter case is ignored. Beside the general words (token, password,
   /// secret, API key, document content) it holds every name under which the product's own requests and responses
   /// carry a secret, in the product's spelling and in the standard ones:
   /// <list type="bullet">
   /// <item><c>enrollmentCode</c>, the one-time code of the enrollment request, and <c>deviceToken</c>, the device's secret in the enrollment response, which the <c>X-QQQ-Device-Token</c> header also carries (that name ends in "token");</item>
   /// <item><c>uuid</c>, the session identifier in the body of a session response, and <c>sessionUUID</c> and <c>sessionId</c>, the two names of the session cookie; <c>sessionToken</c>, the stored session, ends in "token";</item>
   /// <item><c>codeVerifier</c>, the secret half of a browser sign-in in the code-exchange request, with <c>code_verifier</c> and <c>code-verifier</c>;</item>
   /// <item><c>idTokenHint</c>, which the sign-out response carries, and <c>id_token_hint</c>, the parameter the identity provider's sign-out request carries it in, with <c>id-token-hint</c>.</item>
   /// </list>
   /// "uuid" is a name only at the start of a word or straight after a percent-encoded separator, so an identifier
   /// whose name merely ends in it (a device's, a job's) is left alone.
   /// </summary>
   private const string SecretNames = """deviceToken|enrollment[_-]?code|access_token|refresh_token|id[_-]?token[_-]?hint|code[_-]?verifier|sessionUUID|sessionId|(?:\b|(?<=%3F|%26|%23))uuid|api[_-]?key|token|password|secret|contentBase64""";



   /// <summary>Matches an HTTP bearer credential (the word Bearer and its token) so it can be replaced before anything is logged. The match has a one second limit so a crafted message cannot stall logging.</summary>
   [GeneratedRegex("""\bBearer\s+[^\s,;"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex BearerCredential();



   /// <summary>
   /// Matches a named secret (<see cref="SecretNames"/>) written as name and value, keeping the name so the log still
   /// shows which field was hidden: <c>name=value</c>, <c>name: value</c> and a member of a JSON object, with the
   /// name and the value quoted or not. White space, a no-break space or a character that shows as nothing may stand
   /// between the name and the separator. A name is redacted wherever it stands in a line, not only inside a header.
   /// </summary>
   [GeneratedRegex("""(?<prefix>["']?(?:""" + SecretNames + """)["']?[\s\p{Cf}]*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;&]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NamedCredential();



   /// <summary>
   /// Matches a named secret (<see cref="SecretNames"/>) whose equals sign is percent-encoded (<c>%3D</c>), which is
   /// how a parameter looks inside another address, keeping the name. The value ends at a percent-encoded separator
   /// (<c>%26</c>, <c>%23</c>) as well as at a plain one, so the parameters after it stay readable.
   /// </summary>
   [GeneratedRegex("""(?<prefix>(?:""" + SecretNames + """)%3D)(?:(?!%26|%23)[^\s,;&"'<>])+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex EncodedNamedCredential();



   /// <summary>Matches a user name and password embedded in a web address, keeping the scheme so the log still shows that a URL was involved.</summary>
   [GeneratedRegex("""(?<scheme>https?://)[^/\s:@]+:[^/\s@]+@""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex UrlCredential();



   /// <summary>
   /// Matches the value of a cookie header line, to the end of the line, keeping the header's name. The name is
   /// "Cookie" where no letter or digit stands before it, so <c>Cookie:</c>, <c>Set-Cookie:</c> and
   /// <c>HTTP_COOKIE:</c> are all one; white space, a no-break space or a character that shows as nothing may stand
   /// before the colon. When the header's own line holds nothing after the colon, the value is the next line, which
   /// is where a folded header puts it. The whole value goes because a cookie header carries any number of name and
   /// value pairs and the session cookie's name is the server's choice; whatever follows on the same line is hidden
   /// with it, which errs on the safe side.
   /// </summary>
   [GeneratedRegex("""(?<prefix>(?<![A-Za-z0-9])Cookie[\s\p{Cf}-[\r\n]]*:[ \t]*(?:\r?\n[ \t]*)?)[^\r\n]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex CookieHeader();



   /// <summary>
   /// Matches the value of an authorization header line, to the end of the line, keeping the header's name. The name
   /// is "Authorization" where no letter or digit stands before it, so <c>Authorization:</c>,
   /// <c>Proxy-Authorization:</c> and <c>HTTP_AUTHORIZATION:</c> are all one. The separator is a colon or an equals
   /// sign, and white space, a no-break space, a character that shows as nothing or a quote may stand before it, so
   /// the header as a member of a JSON object and in a list of <c>name=value</c> pairs is covered too. When the
   /// header's own line holds nothing after the separator, the value is the next line, which is where a folded header
   /// puts it. The whole value goes, whatever the scheme (Basic, Digest, Negotiate, a bare key), because the scheme
   /// decides where the credential is and a new scheme must not need a new pattern; whatever follows on the same line
   /// is hidden with it, which errs on the safe side, as it does for a cookie header.
   /// </summary>
   [GeneratedRegex("""(?<prefix>(?<![A-Za-z0-9])Authorization["']?[\s\p{Cf}-[\r\n]]*[:=][ \t]*(?:\r?\n[ \t]*)?)[^\r\n]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex AuthorizationHeader();



   /// <summary>
   /// Matches the one-time authorization code and the state of a sign-in callback where they are parameters of an
   /// address: <c>code=</c> or <c>state=</c> straight after the <c>?</c>, <c>&amp;</c> or <c>#</c> that starts a
   /// parameter, keeping the name. The separator is required on purpose. "code" and "state" are ordinary words in a
   /// log ("status code: 401", "exit code=1", "state=Paused"), so neither is a keyword on its own the way "token" is.
   /// The value may be quoted, and the separator, the equals sign and the end of the value may be percent-encoded
   /// (<c>%3F</c>, <c>%26</c>, <c>%23</c>, <c>%3D</c>), which is how a callback looks inside another address; the
   /// ampersand may also be written the way HTML writes it (<c>&amp;amp;</c>).
   /// </summary>
   [GeneratedRegex("""(?<prefix>(?:[?&#]|&amp;|%3F|%26|%23)(?:code|state)(?:=|%3D))(?:"[^"\r\n]*"|'[^'\r\n]*'|(?:(?!%26|%23)[^\s&#"'<>])+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex CallbackParameter();



   /// <summary>Matches the name of a code verifier in any of its spellings (<c>codeVerifier</c>, <c>code_verifier</c>, <c>code-verifier</c>). A message that holds one is about a code exchange, which is what lets <see cref="ExchangedCode"/> treat "code" as a secret's name there.</summary>
   [GeneratedRegex("""code[_-]?verifier""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex CodeVerifierName();



   /// <summary>
   /// Matches a member or parameter named exactly <c>code</c> with its value, keeping the name: a member of a JSON
   /// object, <c>code=value</c>, <c>code: value</c>, or with the equals sign percent-encoded. It is applied only to a
   /// message that also names a code verifier (<see cref="CodeVerifierName"/>): the product's code-exchange request
   /// carries the one-time authorization code as <c>code</c> beside <c>codeVerifier</c>, and outside that request
   /// "code" is an ordinary word whose value an operator needs (an exit code, an error's code).
   /// </summary>
   [GeneratedRegex("""(?<prefix>["']?\bcode["']?(?:[\s\p{Cf}]*[:=]\s*|%3D))(?:"[^"\r\n]*"|'[^'\r\n]*'|(?:(?!%26|%23)[^\s,;&"'<>])+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ExchangedCode();



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
