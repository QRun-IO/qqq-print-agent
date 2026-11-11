/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The service's log as a type of its own. It must write exactly what the runtime's own log method wrote before it was
/// moved here (the same redaction, caps, routing of debug detail, ring and file lines), and a view of it for one server
/// must add that server's short identifier and change nothing else.
/// </summary>
public sealed class ServiceLogTests : IDisposable
{
   private readonly string _directory = Path.Combine(Path.GetTempPath(), "qqq-service-log-tests", Guid.NewGuid().ToString("N"));
   private readonly string _referenceDirectory = Path.Combine(Path.GetTempPath(), "qqq-service-log-tests", Guid.NewGuid().ToString("N"));
   private readonly List<string> _sink = [];



   /// <summary>The identifier of the first server a view is made for; its lines start with "[3f2b8c1e] ".</summary>
   private const string Production = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";



   /// <summary>The identifier of the second server a view is made for; its lines start with "[9a1c0e2d] ".</summary>
   private const string Staging = "9a1c0e2d-77b3-4c58-8f10-5e6d7c8b9a01";



   /// <summary>Removes the two temporary state directories.</summary>
   public void Dispose()
   {
      foreach(string directory in new[] { _directory, _referenceDirectory })
      {
         if(Directory.Exists(directory))
         {
            Directory.Delete(directory, recursive: true);
         }
      }
   }



   /// <summary>Creates the log under test over a fresh activity log, with debug detail on or off as the saved settings say.</summary>
   private (ServiceLog Log, AgentActivityLog File) Create(bool debug = false)
   {
      if(debug)
      {
         AgentActivityLog.SaveSettings(_directory, new LogSettings(DebugLogging: true));
      }
      var file = AgentActivityLog.Create(_directory);
      return (new ServiceLog(file, _sink.Add), file);
   }



   /// <summary>The lines of a log file without their timestamps, so two files written at different moments can be compared.</summary>
   private static List<string> Lines(AgentActivityLog file)
      => File.Exists(file.CurrentLogFilePath)
         ? File.ReadAllLines(file.CurrentLogFilePath).Select(line => line.IndexOf(" [", StringComparison.Ordinal) is int at and >= 0 ? line[at..] : line).ToList()
         : [];



   /// <summary>
   /// What the runtime's log method did before it became this type, transcribed from the runtime as it was: redact, cap at
   /// 1000 characters (4000 for debug detail), write debug detail to the file only and only while it is on, and write
   /// everything else to the file under its level with the "[Service]" marker. It is the reference the moved code is held to.
   /// It redacts through the shared <see cref="AgentActivityLog.Redact"/>, as the runtime did, so it follows the
   /// patterns added to that method since (a cookie header line, an authorization header line, the code and state of
   /// a callback address, and four more named values) and is otherwise the method as it was;
   /// <see cref="LinesWithNoPatternAddedSince_AreRedactedAsBefore"/> holds the redaction of every other kind of line
   /// to the text it gave before those patterns were added.
   /// </summary>
   private static string? WriteAsTheRuntimeDid(AgentActivityLog file, string level, string message)
   {
      bool debug = string.Equals(level, "DEBUG", StringComparison.Ordinal);
      if(debug && !file.IsDebugEnabled)
      { return null; }
      string sanitized = AgentActivityLog.Redact(message);
      int cap = debug ? 4000 : 1000;
      string bounded = sanitized.Length <= cap ? sanitized : sanitized[..cap];
      if(debug)
      {
         file.Debug("[Service] " + bounded);
         return null;
      }
      switch(level)
      {
         case "ERROR":
            file.Error("[Service] " + bounded);
            break;
         case "WARN":
            file.Warning("[Service] " + bounded);
            break;
         default:
            file.Info("[Service] " + bounded);
            break;
      }
      return bounded;
   }



   /// <summary>
   /// A varied set of lines (every level, a level the log does not know, secrets of each redacted kind, an over-long line,
   /// a line with a line break, debug detail) produces a file that is the same, line for line, as the one the runtime's own
   /// method produced, and hands the host's sink and the ring the same text.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void EveryLine_IsWrittenAsTheRuntimeWroteIt(bool debug)
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug);
      if(debug)
      {
         AgentActivityLog.SaveSettings(_referenceDirectory, new LogSettings(DebugLogging: true));
      }
      var reference = AgentActivityLog.Create(_referenceDirectory);
      (string Level, string Message)[] lines =
      [
         ("INFO", "Print service started."),
         ("WARN", "Printing blocked: QQQ authorization is required."),
         ("ERROR", "Local command failed: IOException: Pipe is broken."),
         ("NOTICE", "A level this log does not know is written as information."),
         ("INFO", "Authorization: Bearer synthetic-bearer deviceToken=synthetic-device enrollmentCode=\"synthetic-code\""),
         ("ERROR", "https://operator:synthetic-password@print.example.com/ refused access_token=synthetic-access&refresh_token=synthetic-refresh"),
         ("INFO", "First line\r\n2026-11-10 12:00:00 [ERROR] forged entry"),
         ("WARN", new string('w', 1500)),
         ("DEBUG", "[Session] GET /qqq/v1/metaData token=synthetic-debug-token"),
         ("DEBUG", new string('d', 5000)),
      ];
      List<string> expectedSink = [];

      foreach((string level, string message) in lines)
      {
         log.Write(level, message);
         if(WriteAsTheRuntimeDid(reference, level, message) is string bounded)
         {
            expectedSink.Add(bounded);
         }
      }

      Assert.Equal(Lines(reference), Lines(file));
      Assert.Equal(expectedSink, _sink);
      Assert.Equal(expectedSink, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal(["INFO", "WARN", "ERROR", "NOTICE", "INFO", "ERROR", "INFO", "WARN"], log.Entries().Select(entry => entry.Level), StringComparer.Ordinal);
      Assert.Equal(debug ? 11 : 9, Lines(file).Count);
   }



   /// <summary>
   /// Each kind of secret the service redacts today is gone from all three places a line reaches (the ring, the host's sink
   /// and the file), with the redaction marker in its place: a bearer credential, a device token, an enrollment code, an
   /// access or refresh token (including in the query of a callback address), a password, a secret, an API key, document
   /// content, a user name and password inside an address, the value of a cookie header line, and the one-time code and
   /// the state of a sign-in callback in the query or the fragment of an address. So are the values the product handles
   /// that were added later: the identity provider's ID-token hint, an authorization header of any scheme or of none,
   /// the PKCE code verifier, the session cookie under either of the product's names wherever it stands (bare, in JSON,
   /// on the continuation line of a folded header, under a header name that only ends in "Cookie"), and a callback's
   /// code and state when the value is quoted or the callback is percent-encoded inside another address.
   /// </summary>
   [Theory]
   [InlineData("Authorization: Bearer synthetic-value-1", "synthetic-value-1")]
   [InlineData("deviceToken=synthetic-value-2 was refused", "synthetic-value-2")]
   [InlineData("enrollmentCode=\"synthetic-value-3\"", "synthetic-value-3")]
   [InlineData("{\"enrollmentCode\":\"synthetic-value-4\"}", "synthetic-value-4")]
   [InlineData("callback io.qrun.qqq-print-agent:/oauth/callback?access_token=synthetic-value-5&refresh_token=synthetic-value-6", "synthetic-value-5")]
   [InlineData("callback io.qrun.qqq-print-agent:/oauth/callback?state=s&refresh_token=synthetic-value-6", "synthetic-value-6")]
   [InlineData("password='synthetic-value-7'", "synthetic-value-7")]
   [InlineData("secret: synthetic-value-8", "synthetic-value-8")]
   [InlineData("api_key=synthetic-value-9 api-key=synthetic-value-9", "synthetic-value-9")]
   [InlineData("contentBase64=synthetic-value-10", "synthetic-value-10")]
   [InlineData("token = synthetic-value-11", "synthetic-value-11")]
   [InlineData("https://operator:synthetic-value-12@print.example.com/qqq", "synthetic-value-12")]
   [InlineData("Cookie: sessionUUID=synthetic-value-13; theme=dark", "synthetic-value-13")]
   [InlineData("Set-Cookie: sessionUUID=synthetic-value-14; Path=/; HttpOnly; Secure", "synthetic-value-14")]
   [InlineData("callback io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-value-15&state=synthetic-value-16", "synthetic-value-15")]
   [InlineData("callback io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-value-15&state=synthetic-value-16", "synthetic-value-16")]
   [InlineData("returned to https://print.example.com/signed-in#state=synthetic-value-17&code=synthetic-value-18", "synthetic-value-17")]
   [InlineData("returned to https://print.example.com/signed-in#state=synthetic-value-17&code=synthetic-value-18", "synthetic-value-18")]
   [InlineData("GET https://id.example.com/logout?id_token_hint=synthetic-value-19&post_logout_redirect_uri=x&state=synthetic-state", "synthetic-value-19")]
   [InlineData("Authorization: Basic synthetic-value-20", "synthetic-value-20")]
   [InlineData("authorization: Digest username=\"operator\", response=\"synthetic-value-21\"", "synthetic-value-21")]
   [InlineData("Proxy-Authorization: Negotiate synthetic-value-22", "synthetic-value-22")]
   [InlineData("Authorization: synthetic-value-23", "synthetic-value-23")]
   [InlineData("{\"Authorization\":\"Basic synthetic-value-24\"}", "synthetic-value-24")]
   [InlineData("token request: grant_type=authorization_code&code_verifier=synthetic-value-25", "synthetic-value-25")]
   [InlineData("sessionId=synthetic-value-26", "synthetic-value-26")]
   [InlineData("sessionUUID=synthetic-value-27", "synthetic-value-27")]
   [InlineData("{\"sessionUUID\":\"synthetic-value-28\"}", "synthetic-value-28")]
   [InlineData("{\"Cookie\":\"sessionUUID=synthetic-value-29\"}", "synthetic-value-29")]
   [InlineData("Cookie:\r\n sessionUUID=synthetic-value-30", "synthetic-value-30")]
   [InlineData("SessionCookie: sessionUUID=synthetic-value-31", "synthetic-value-31")]
   [InlineData("callback ?code=\"synthetic-value-32\"&state='synthetic-value-33'", "synthetic-value-32")]
   [InlineData("callback ?code=\"synthetic-value-32\"&state='synthetic-value-33'", "synthetic-value-33")]
   [InlineData("callback ?code%3Dsynthetic-value-34", "synthetic-value-34")]
   [InlineData("redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb%3Fcode%3Dsynthetic-value-35%26state%3Dsynthetic-value-36", "synthetic-value-35")]
   [InlineData("redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb%3Fcode%3Dsynthetic-value-35%26state%3Dsynthetic-value-36", "synthetic-value-36")]
   public void Secrets_NeverReachTheRingTheSinkOrTheFile(string message, string secret)
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      log.Write("ERROR", message);

      Assert.DoesNotContain(secret, File.ReadAllText(file.CurrentLogFilePath), StringComparison.Ordinal);
      Assert.DoesNotContain(secret, Assert.Single(_sink), StringComparison.Ordinal);
      Assert.DoesNotContain(secret, Assert.Single(log.Entries()).Message, StringComparison.Ordinal);
      Assert.Contains("[REDACTED]", _sink[0], StringComparison.Ordinal);
      Assert.Equal(AgentActivityLog.Redact(message), _sink[0]);
   }



   /// <summary>
   /// A cookie header line loses its whole value, to the end of that line and no further, and a callback address loses
   /// the values of its code and state and keeps everything else, in the ring, the host's sink and the file. The header's
   /// name and the parameters' names stay, so the log still shows what was hidden. A parameter whose name only ends in
   /// "state" or begins with "code" is another parameter and is left alone.
   /// </summary>
   [Theory]
   [InlineData("Cookie: sessionUUID=synthetic-cookie; theme=dark", "Cookie: [REDACTED]")]
   [InlineData("Set-Cookie: sessionUUID=synthetic-cookie; Path=/; HttpOnly; Secure", "Set-Cookie: [REDACTED]")]
   [InlineData("set-cookie:sessionUUID=synthetic-cookie", "set-cookie:[REDACTED]")]
   [InlineData("Request headers:\nAccept: */*\nCookie: sessionUUID=synthetic-cookie\nUser-Agent: agent", "Request headers:\n  Accept: */*\n  Cookie: [REDACTED]\n  User-Agent: agent")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-code&state=synthetic-state", "io.qrun.qqq-print-agent:/oauth/callback?code=[REDACTED]&state=[REDACTED]")]
   [InlineData("GET https://print.example.com/cb?state=synthetic-state&session_state=kept&code=synthetic-code#top", "GET https://print.example.com/cb?state=[REDACTED]&session_state=kept&code=[REDACTED]#top")]
   [InlineData("GET https://print.example.com/cb?codec=h264&stateless=true&STATE=synthetic-state", "GET https://print.example.com/cb?codec=h264&stateless=true&STATE=[REDACTED]")]
   [InlineData("opened \"https://id.example.com/authorize?client_id=agent&state=synthetic-state\" in the browser", "opened \"https://id.example.com/authorize?client_id=agent&state=[REDACTED]\" in the browser")]
   [InlineData("returned to https://print.example.com/signed-in#code=synthetic-code&state=synthetic-state", "returned to https://print.example.com/signed-in#code=[REDACTED]&state=[REDACTED]")]
   public void CookieHeadersAndCallbackParameters_AreRedacted(string message, string expected)
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      log.Write("INFO", message);

      Assert.Equal(expected, AgentActivityLog.Redact(message));
      Assert.Equal(expected, Assert.Single(_sink));
      Assert.Equal(expected, Assert.Single(log.Entries()).Message);
      string written = File.ReadAllText(file.CurrentLogFilePath);
      Assert.DoesNotContain("synthetic-", written, StringComparison.Ordinal);
      Assert.Contains("[REDACTED]", written, StringComparison.Ordinal);
   }



   /// <summary>
   /// An authorization header line loses its whole value, to the end of that line and no further, whatever its scheme
   /// and in either letter case, and keeps its name; so does the same header as a member of a JSON object. The ID-token
   /// hint, the code verifier and the session cookie's two names lose their value and keep their name and everything
   /// around it. A callback's code and state lose a quoted value with its quotes, and a percent-encoded callback loses
   /// both values up to the encoded separator. Each text is the same in the ring, the host's sink and the file.
   /// </summary>
   [Theory]
   [InlineData("Authorization: Basic c3ludGhldGlj", "Authorization: [REDACTED]")]
   [InlineData("authorization: Digest username=\"operator\", response=\"synthetic-response\"", "authorization: [REDACTED]")]
   [InlineData("Proxy-Authorization:Negotiate synthetic-ticket", "Proxy-Authorization:[REDACTED]")]
   [InlineData("Authorization: Bearer synthetic-bearer deviceToken=synthetic-device", "Authorization: [REDACTED]")]
   [InlineData("Request headers:\nAccept: */*\nAuthorization: Basic c3ludGhldGlj\nUser-Agent: agent", "Request headers:\n  Accept: */*\n  Authorization: [REDACTED]\n  User-Agent: agent")]
   [InlineData("{\"Authorization\":\"Basic c3ludGhldGlj\"}", "{\"Authorization\":[REDACTED]")]
   [InlineData("GET https://id.example.com/logout?id_token_hint=synthetic-hint&post_logout_redirect_uri=io.qrun.qqq-print-agent:/oauth/callback&state=synthetic-state",
      "GET https://id.example.com/logout?id_token_hint=[REDACTED]&post_logout_redirect_uri=io.qrun.qqq-print-agent:/oauth/callback&state=[REDACTED]")]
   [InlineData("token request: grant_type=authorization_code&code_verifier=synthetic-verifier&redirect_uri=x", "token request: grant_type=authorization_code&code_verifier=[REDACTED]&redirect_uri=x")]
   [InlineData("sessionUUID=synthetic-session; Path=/", "sessionUUID=[REDACTED]; Path=/")]
   [InlineData("The server refused sessionId=synthetic-session for this device", "The server refused sessionId=[REDACTED] for this device")]
   [InlineData("{\"sessionUUID\":\"synthetic-session\",\"theme\":\"dark\"}", "{\"sessionUUID\":[REDACTED],\"theme\":\"dark\"}")]
   [InlineData("callback ?code=\"synthetic-quoted\"&state='synthetic-quoted-2' received", "callback ?code=[REDACTED]&state=[REDACTED] received")]
   [InlineData("callback ?code%3Dsynthetic-encoded%26state%3Dsynthetic-encoded-2 received", "callback ?code%3D[REDACTED]%26state%3D[REDACTED] received")]
   [InlineData("redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb%3FCODE%3dsynthetic-encoded%23top", "redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb%3FCODE%3d[REDACTED]%23top")]
   public void AuthorizationHeadersAndTheValuesAddedLater_AreRedacted(string message, string expected)
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      log.Write("INFO", message);

      Assert.Equal(expected, AgentActivityLog.Redact(message));
      Assert.Equal(expected, Assert.Single(_sink));
      Assert.Equal(expected, Assert.Single(log.Entries()).Message);
      string written = File.ReadAllText(file.CurrentLogFilePath);
      Assert.DoesNotContain("synthetic-", written, StringComparison.Ordinal);
      Assert.DoesNotContain("c3ludGhldGlj", written, StringComparison.Ordinal);
      Assert.Contains("[REDACTED]", written, StringComparison.Ordinal);
   }



   /// <summary>
   /// "code" and "state" are ordinary words in a log, so neither is a keyword on its own the way "token" is: only a
   /// parameter of an address is redacted. Lines that report a status code, an exit code, an error code or a state, with
   /// a colon, an equals sign or neither, and lines that only mention a cookie, an authorization, a session, an ID
   /// token or a code verifier without giving a value, are written exactly as they were given.
   /// </summary>
   [Theory]
   [InlineData("The server answered with status code: 401")]
   [InlineData("Print helper ended with exit code 1")]
   [InlineData("Print helper ended: exit code=1")]
   [InlineData("Worker exit code = -1073741819")]
   [InlineData("Windows Installer returned code: 1603 (state: rolled back)")]
   [InlineData("Printer state=Paused, error code=0x80070005")]
   [InlineData("Queue state: Ready; jobs: 0")]
   [InlineData("Unexpected status code 502 from /qqq/v1/metaData")]
   [InlineData("code=0 state=idle")]
   [InlineData("Is the printer in a ready state? code 0")]
   [InlineData("HTTP GET /qqq/v1/session: session cookie yes, device proof no")]
   [InlineData("The server sent no session cookie; sign in again")]
   [InlineData("Printing blocked: QQQ authorization is required.")]
   [InlineData("Authorization required by the server; sign in again")]
   [InlineData("Waiting for authorization (attempt 2 of 5)")]
   [InlineData("Native grant: id token hint present yes, code verifier present yes")]
   [InlineData("The session identifier changed; signing in again")]
   [InlineData("Session established for print.example.com, session UUID present yes")]
   [InlineData("GET /qqq/v1/operator/printers?limit=50&after=printer-a1 answered 200")]
   public void OrdinaryLinesAboutCodesStatesAndCookies_AreUntouched(string message)
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      log.Write("INFO", message);

      Assert.Equal(message, AgentActivityLog.Redact(message));
      Assert.Equal(message, Assert.Single(_sink));
      Assert.Equal(message, Assert.Single(log.Entries()).Message);
      Assert.Equal(" [INFO] [Service] " + message, Assert.Single(Lines(file)));
   }



   /// <summary>
   /// The redaction of a line that holds none of the shapes added since (a cookie header, an authorization header, a
   /// callback's code or state, an ID-token hint, a code verifier, the session cookie's names) is, to the character,
   /// what it was before they were added: each expected text here was produced by the method as it stood before them.
   /// It is what lets the comparison with the runtime's old method keep its meaning. The line that used to stand here
   /// with an "Authorization:" header in front is now hidden to its end, which the test above holds; the same
   /// credentials without the header name are still redacted one by one.
   /// </summary>
   [Theory]
   [InlineData("Print service started.", "Print service started.")]
   [InlineData("Sent Bearer synthetic-bearer deviceToken=synthetic-device enrollmentCode=\"synthetic-code\"", "Sent Bearer [REDACTED] deviceToken=[REDACTED] enrollmentCode=[REDACTED]")]
   [InlineData("https://operator:synthetic-password@print.example.com/ refused access_token=synthetic-access&refresh_token=synthetic-refresh", "https://[REDACTED]@print.example.com/ refused access_token=[REDACTED]&refresh_token=[REDACTED]")]
   [InlineData("First line\r\n2026-11-10 12:00:00 [ERROR] forged entry", "First line\n  2026-11-10 12:00:00 [ERROR] forged entry")]
   [InlineData("{\"enrollmentCode\":\"synthetic-code\",\"name\":\"Front desk\"}", "{\"enrollmentCode\":[REDACTED],\"name\":\"Front desk\"}")]
   [InlineData("password='synthetic-secret' contentBase64=synthetic-document, api-key: synthetic-key; secret = synthetic-secret", "password=[REDACTED] contentBase64=[REDACTED], api-key: [REDACTED]; secret = [REDACTED]")]
   [InlineData("[Session] GET /qqq/v1/metaData token=synthetic-debug-token", "[Session] GET /qqq/v1/metaData token=[REDACTED]")]
   public void LinesWithNoPatternAddedSince_AreRedactedAsBefore(string message, string expected)
      => Assert.Equal(expected, AgentActivityLog.Redact(message));



   /// <summary>
   /// What the redaction still does not recognise, recorded so that each is a known fact and not a surprise. A code or a
   /// state that is not a parameter of an address (on its own, or as members of a JSON object), a cookie whose name is
   /// not one of the product's two when it stands under a header name that only ends in "Cookie", with an equals sign
   /// in place of the colon, or as a JSON member, and a nonce or a client assertion all pass through unchanged. The
   /// redaction cannot help with these. They stay out of the log only for as long as nothing writes them: the service
   /// is handed a session credential and not the callback, the session client logs an address without its query
   /// (asserted below) and says only whether a cookie was attached. This test does not show that nothing writes them;
   /// it shows what would happen if something did.
   /// </summary>
   [Theory]
   [InlineData("code=synthetic-code state=synthetic-state")]
   [InlineData("{\"code\":\"synthetic-code\",\"state\":\"synthetic-state\"}")]
   [InlineData("SessionCookie: other=synthetic-cookie")]
   [InlineData("cookie = other=synthetic-cookie")]
   [InlineData("{\"Cookie\":\"other=synthetic-cookie\"}")]
   [InlineData("nonce=synthetic-nonce client_assertion=synthetic-assertion")]
   public void WhatTheRedactionStillDoesNotRecognise_IsRecorded(string message)
   {
      (ServiceLog log, _) = Create();

      log.Write("INFO", message);

      Assert.Equal(message, Assert.Single(_sink));
      Assert.Equal(message, AgentActivityLog.Redact(message));
      Assert.Equal("io.qrun.qqq-print-agent:///oauth/callback", QqqSessionDiagnostics.Describe(new Uri("io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-code&state=synthetic-state")));
   }



   /// <summary>
   /// What the redaction hides although it is not a secret, recorded for the same reason. A header pattern takes the
   /// rest of its line, so a sentence that happens to put a colon after the word "cookie" or "authorization" loses
   /// what follows; a header whose name only ends in "-Cookie" is hidden like a cookie header; and a parameter named
   /// "state" or "code" in an ordinary address loses its value. No line the product writes has these shapes: the
   /// session client writes "session cookie yes" without a colon, and its one address with a query is the printer
   /// list's. If such a line is ever written it must be worded differently, or the pattern anchored; hiding too much
   /// is the side these patterns err on.
   /// </summary>
   [Theory]
   [InlineData("HTTP GET /x: session cookie: yes, device proof: no, basic credential: no", "HTTP GET /x: session cookie: [REDACTED]")]
   [InlineData("Proxy stripped the cookie: the request was refused with 407 by proxy.example.com", "Proxy stripped the cookie: [REDACTED]")]
   [InlineData("X-Cookie: abc", "X-Cookie: [REDACTED]")]
   [InlineData("Waiting for authorization: the administrator must approve this device", "Waiting for authorization: [REDACTED]")]
   [InlineData("GET /qqq/v1/qbit/printing/jobs?state=QUEUED&printerId=printer-a1 answered 200", "GET /qqq/v1/qbit/printing/jobs?state=[REDACTED]&printerId=printer-a1 answered 200")]
   [InlineData("GET /report?code=E1234&lang=en answered 500", "GET /report?code=[REDACTED]&lang=en answered 500")]
   public void WhatTheRedactionHidesAlthoughItIsNotASecret_IsRecorded(string message, string expected)
   {
      (ServiceLog log, _) = Create();

      log.Write("INFO", message);

      Assert.Equal(expected, AgentActivityLog.Redact(message));
      Assert.Equal(expected, Assert.Single(_sink));
   }



   /// <summary>An operational line is cut at 1000 characters after redaction, and a line of exactly that length is kept whole.</summary>
   [Fact]
   public void OperationalLine_IsCappedAtOneThousandCharacters()
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      log.Write("INFO", new string('a', 1000));
      log.Write("INFO", new string('b', 1001));
      log.Write("INFO", "deviceToken=" + new string('c', 2000));

      Assert.Equal([1000, 1000, "deviceToken=[REDACTED]".Length], _sink.Select(line => line.Length));
      Assert.Equal(new string('b', 1000), _sink[1]);
      Assert.Equal(_sink, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Contains(" [INFO] [Service] " + new string('b', 1000), Lines(file), StringComparer.Ordinal);
      Assert.Equal(1000, ServiceLog.LineLimit);
   }



   /// <summary>The ring keeps the newest 100 operational entries in order; the file and the host's sink keep all of them.</summary>
   [Fact]
   public void Ring_KeepsTheNewestHundredEntries()
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      for(int index = 1; index <= 130; index++)
      {
         log.Write(index % 2 == 0 ? "WARN" : "INFO", "entry " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
      }
      AgentLogEntry[] entries = log.Entries();

      Assert.Equal(100, entries.Length);
      Assert.Equal(100, ServiceLog.RingCapacity);
      Assert.Equal("entry 31", entries[0].Message);
      Assert.Equal("INFO", entries[0].Level);
      Assert.Equal("entry 130", entries[^1].Message);
      Assert.Equal("WARN", entries[^1].Level);
      Assert.All(entries, entry => Assert.True(DateTimeOffset.TryParseExact(entry.Timestamp, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _)));
      Assert.Equal(130, _sink.Count);
      Assert.Equal(130, Lines(file).Count);
   }



   /// <summary>Clearing the ring empties what the snapshot carries and nothing else; later lines are kept again.</summary>
   [Fact]
   public void ClearingTheRing_EmptiesItAndLaterLinesAreKept()
   {
      (ServiceLog log, AgentActivityLog file) = Create();
      log.Write("INFO", "before");

      log.ClearEntries();
      log.Write("INFO", "after");

      Assert.Equal("after", Assert.Single(log.Entries()).Message);
      Assert.Equal(2, Lines(file).Count);
   }



   /// <summary>While debug logging is off, debug detail is dropped: nothing reaches the file, the ring or the host's sink.</summary>
   [Fact]
   public void DebugDetail_IsDroppedWhileDebugLoggingIsOff()
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug: false);

      log.Write("DEBUG", "request detail");

      Assert.False(log.IsDebugEnabled);
      Assert.Empty(Lines(file));
      Assert.Empty(_sink);
      Assert.Empty(log.Entries());
   }



   /// <summary>
   /// While debug logging is on, debug detail goes to the file only, marked as debug, redacted and cut at 4000 characters.
   /// It never enters the ring or the host's sink, so it cannot displace the operational entries the snapshot carries.
   /// </summary>
   [Fact]
   public void DebugDetail_GoesToTheFileOnly_RedactedAndCappedAtFourThousand()
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug: true);
      log.Write("INFO", "operational");

      log.Write("DEBUG", "GET /session password=synthetic-debug-secret");
      log.Write("DEBUG", new string('x', 4500));

      Assert.True(log.IsDebugEnabled);
      Assert.Equal("operational", Assert.Single(_sink));
      Assert.Equal("operational", Assert.Single(log.Entries()).Message);
      Assert.Equal([" [INFO] [Service] operational", " [INFO] [DEBUG] [Service] GET /session password=[REDACTED]", " [INFO] [DEBUG] [Service] " + new string('x', 4000)], Lines(file));
      Assert.Equal(4000, ServiceLog.DebugLimit);
   }



   /// <summary>A failure is one ERROR line with its type, message and inner causes, and its full text as debug detail only while that is on.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void Failure_IsAnErrorLineAndDebugDetail(bool debug)
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug);
      InvalidOperationException failure = new("The session could not be saved.", new IOException("deviceToken=synthetic-inner-secret"));

      log.Failure("Sign-in failed", failure);

      Assert.Equal("Sign-in failed: InvalidOperationException: The session could not be saved. <- IOException: deviceToken=[REDACTED]", Assert.Single(_sink));
      Assert.Equal("ERROR", Assert.Single(log.Entries()).Level);
      List<string> lines = Lines(file);
      Assert.Equal(" [ERROR] [Service] " + _sink[0], lines[0]);
      Assert.Equal(debug, lines.Any(line => line.StartsWith(" [INFO] [DEBUG] [Service] Sign-in failed detail: System.InvalidOperationException: The session could not be saved.", StringComparison.Ordinal)));
      Assert.DoesNotContain(lines, line => line.Contains("synthetic-inner-secret", StringComparison.Ordinal));
   }



   /// <summary>The session client's progress goes through the same log: a step is an INFO line marked "[Session]", request detail is debug detail, and the client is told whether debug detail is wanted.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void SessionDiagnostics_WriteThroughTheLog(bool debug)
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug);
      QqqSessionDiagnostics diagnostics = log.SessionDiagnostics();
      int built = 0;

      diagnostics.Info("Discovery started for print.example.com.");
      diagnostics.Debug(() =>
      {
         built++;
         return "GET /qqq/v1/metaData token=synthetic-session-secret";
      });

      Assert.Equal(debug, diagnostics.DebugEnabled);
      Assert.Equal(debug ? 1 : 0, built);
      Assert.Equal("[Session] Discovery started for print.example.com.", Assert.Single(_sink));
      Assert.Equal(debug
         ? [" [INFO] [Service] [Session] Discovery started for print.example.com.", " [INFO] [DEBUG] [Service] [Session] GET /qqq/v1/metaData token=[REDACTED]"]
         : new List<string> { " [INFO] [Service] [Session] Discovery started for print.example.com." }, Lines(file));
   }



   /// <summary>
   /// A view for one server writes the same lines with that server's short identifier in front (the first eight
   /// characters of its identifier), in the ring, the sink and the file. It shares the ring and the file with the log
   /// it came from, so lines from the service and from each server appear together in the order they were written.
   /// </summary>
   [Fact]
   public void ForServer_PrefixesTheShortIdentifierAndSharesTheRingAndTheFile()
   {
      (ServiceLog log, AgentActivityLog file) = Create();
      ServiceLog production = log.ForServer(Production);
      ServiceLog staging = log.ForServer(Staging);

      log.Write("INFO", "Print service started.");
      production.Write("WARN", "Printing blocked.");
      staging.Write("ERROR", "Local command failed.");
      production.Write("INFO", "Signed in.");

      string[] expected = ["Print service started.", "[3f2b8c1e] Printing blocked.", "[9a1c0e2d] Local command failed.", "[3f2b8c1e] Signed in."];
      Assert.Equal(expected, _sink, StringComparer.Ordinal);
      Assert.Equal(expected, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal(expected, staging.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal(["INFO", "WARN", "ERROR", "INFO"], production.Entries().Select(entry => entry.Level), StringComparer.Ordinal);
      Assert.Equal([" [INFO] [Service] Print service started.", " [WARN] [Service] [3f2b8c1e] Printing blocked.", " [ERROR] [Service] [9a1c0e2d] Local command failed.", " [INFO] [Service] [3f2b8c1e] Signed in."], Lines(file));
      staging.ClearEntries();
      Assert.Empty(log.Entries());
   }



   /// <summary>
   /// A server's view redacts exactly as the log does, and the caps do not grow by the prefix: the line is still at most
   /// 1000 characters (4000 for debug detail) with the prefix counted in. Debug detail is still dropped while it is off
   /// and goes to the file only while it is on, and a failure and the session client's progress carry the prefix too.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void ForServer_KeepsRedactionCapsAndDebugRouting(bool debug)
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug);
      ServiceLog server = log.ForServer(Production);

      server.Write("ERROR", "Sent Bearer synthetic-server-bearer enrollmentCode=synthetic-server-code");
      server.Write("INFO", new string('p', 1500));
      server.Write("DEBUG", new string('q', 4500));
      server.Failure("Sign-in failed", new IOException("password=synthetic-server-password"));
      server.SessionDiagnostics().Info("Discovery started.");

      Assert.Equal(debug, server.IsDebugEnabled);
      Assert.Equal(
      [
         "[3f2b8c1e] Sent Bearer [REDACTED] enrollmentCode=[REDACTED]",
         "[3f2b8c1e] " + new string('p', 1000 - "[3f2b8c1e] ".Length),
         "[3f2b8c1e] Sign-in failed: IOException: password=[REDACTED]",
         "[3f2b8c1e] [Session] Discovery started.",
      ], _sink);
      Assert.Equal(_sink, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      List<string> lines = Lines(file);
      Assert.Equal(debug ? 6 : 4, lines.Count(line => line.StartsWith(" [", StringComparison.Ordinal)));
      Assert.Equal(debug, lines.Contains(" [INFO] [DEBUG] [Service] [3f2b8c1e] " + new string('q', 4000 - "[3f2b8c1e] ".Length), StringComparer.Ordinal));
      Assert.DoesNotContain(lines, line => line.Contains("synthetic-server", StringComparison.Ordinal));
   }



   /// <summary>
   /// A view is named by a server's full identifier and by nothing else. A server's name is refused, whatever it holds:
   /// a credential keyword that would make the file's second redaction hide the server's lines, square brackets that
   /// would forge structure inside a line, a line break that would start a forged entry. So are an identifier's
   /// beginning, an identifier in another spelling, and nothing at all. The refusal does not repeat what was given, and
   /// nothing is written.
   /// </summary>
   [Theory]
   [InlineData("Production")]
   [InlineData("Cookie:")]
   [InlineData("Test Cookie: staging")]
   [InlineData("Lab token:")]
   [InlineData("password=")]
   [InlineData("A token=\"")]
   [InlineData("a] [ERROR] forged [b")]
   [InlineData("Warehouse\r\n2026-11-10 12:00:00 [ERROR] forged")]
   [InlineData("3f2b8c1e")]
   [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5")]
   [InlineData("3F2B8C1E-5D4A-4F6B-9A7C-0E1D2C3B4A5F")]
   [InlineData("{3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f}")]
   [InlineData(" 3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f")]
   [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f\n")]
   [InlineData("")]
   [InlineData("   ")]
   [InlineData(null)]
   public void ForServer_TakesAFullIdentifierAndNothingElse(string? value)
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      ArgumentException refusal = Assert.Throws<ArgumentException>(() => log.ForServer(value!));

      Assert.True(string.IsNullOrWhiteSpace(value) || !refusal.Message.Contains(value, StringComparison.Ordinal));
      Assert.Empty(_sink);
      Assert.Empty(log.Entries());
      Assert.False(File.Exists(file.CurrentLogFilePath));
   }



   /// <summary>
   /// The prefix of a server's view changes nothing about the line behind it, in the ring, the host's sink or the file.
   /// Each message is written once through the log and once through a view: the view's line is the log's own line with
   /// the short identifier in front, for a plain line, for lines that hold a secret in each quoting, and for a line
   /// that ends in an open quote. In the file, which redacts every joined line a second time, the server's line keeps
   /// its marker, its prefix and its text: a prefix made of an operator's name could lose all three there.
   /// </summary>
   [Theory]
   [InlineData("Print lane stopped: The printer is offline (job job-204).")]
   [InlineData("Sign-in failed: password=\"synthetic-quoted\" rejected")]
   [InlineData("Sign-in failed: token='synthetic-quoted' refused")]
   [InlineData("Cookie: sessionUUID=synthetic-cookie; theme=dark")]
   [InlineData("returned to io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-code&state=synthetic-state")]
   [InlineData("The server sent secret=\"")]
   public void ForServer_ThePrefixChangesNothingAboutTheLineBehindIt(string message)
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug: true);
      ServiceLog server = log.ForServer(Production);

      log.Write("WARN", message);
      server.Write("WARN", message);
      log.Write("DEBUG", message);
      server.Write("DEBUG", message);

      List<string> lines = Lines(file);
      Assert.Equal([AgentActivityLog.Redact(message), "[3f2b8c1e] " + AgentActivityLog.Redact(message)], _sink);
      Assert.Equal(_sink, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal(4, lines.Count);
      Assert.Equal(lines[0].Replace(" [Service] ", " [Service] [3f2b8c1e] ", StringComparison.Ordinal), lines[1]);
      Assert.Equal(lines[2].Replace(" [Service] ", " [Service] [3f2b8c1e] ", StringComparison.Ordinal), lines[3]);
      Assert.StartsWith(" [WARN] [Service] [3f2b8c1e] ", lines[1], StringComparison.Ordinal);
      Assert.StartsWith(" [INFO] [DEBUG] [Service] [3f2b8c1e] ", lines[3], StringComparison.Ordinal);
      Assert.DoesNotContain(lines, line => line.Contains("synthetic-", StringComparison.Ordinal));
   }



   /// <summary>
   /// The ring's order is the order of its timestamps. The first writer's reading of the clock is held open while a
   /// second writer, on a thread of its own, writes a whole line. An entry is stamped inside the lock that orders the
   /// ring, so the second writer cannot enter the ring until the first has, and the earlier stamp is the earlier entry.
   /// Stamped before that lock, the second writer would finish during the reading and stand in the ring in front of an
   /// earlier stamp. The wait for the second writer is bounded, and with the stamp inside the lock it always runs out:
   /// that the second writer could not finish is the thing being shown. It is a deliberate wait of 200 milliseconds of
   /// real time for something that must not happen, the one kind of timed wait a test of an absence cannot avoid, and
   /// it is counted with the other timed waits in the audit (W28). It cannot fail falsely: a slow machine only makes
   /// the second writer later still.
   /// </summary>
   [Fact]
   public void RingOrder_IsTimestampOrder_WhenASecondWriterArrivesDuringTheFirstStamp()
   {
      SteppingClock clock = new();
      ServiceLog log = new(AgentActivityLog.Create(_directory), _ => { }, clock);
      Thread second = new(() => log.Write("WARN", "second"));
      bool secondFinishedDuringTheFirstStamp = true;
      clock.OnReading = reading =>
      {
         if(reading == 1)
         {
            second.Start();
            secondFinishedDuringTheFirstStamp = second.Join(TimeSpan.FromMilliseconds(200));
         }
      };

      log.Write("INFO", "first");
      bool secondFinished = second.Join(TimeSpan.FromSeconds(30));

      AgentLogEntry[] entries = log.Entries();
      Assert.True(secondFinished);
      Assert.False(secondFinishedDuringTheFirstStamp);
      Assert.Equal(["first", "second"], entries.Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal([SteppingClock.Reading(1), SteppingClock.Reading(2)], entries.Select(entry => entry.Timestamp), StringComparer.Ordinal);
   }



   /// <summary>
   /// Several threads writing at once, half of them through a server's view, lose no line and never corrupt the ring:
   /// the host's sink and the file receive all 200 lines, each once, and the ring ends with 100 of them, each once,
   /// the same 100 whether it is read through the log or through a view. On a clock that gives every reading a later
   /// time than the last, the ring's timestamps are the last 100 readings in order. That holds here on every run, but
   /// this test seldom catches a stamp taken outside the ring's lock, because the writers rarely overlap at that point;
   /// the test above is the one that proves it.
   /// </summary>
   [Fact]
   public async Task ConcurrentWriters_LoseNothing()
   {
      (_, AgentActivityLog file) = Create();
      List<string> received = [];
      var shared = new ServiceLog(file, line =>
      {
         lock(received)
         { received.Add(line); }
      }, new SteppingClock());

      await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
      {
         for(int index = 0; index < 25; index++)
         {
            (writer % 2 == 0 ? shared : shared.ForServer(Production)).Write("INFO", $"writer {writer} line {index}");
         }
      })));

      string[] ring = shared.Entries().Select(entry => entry.Message).ToArray();
      Assert.Equal(200, received.Count);
      Assert.Equal(200, received.Distinct(StringComparer.Ordinal).Count());
      Assert.Equal(100, ring.Length);
      Assert.Equal(100, ring.Distinct(StringComparer.Ordinal).Count());
      Assert.All(ring, line => Assert.Contains(line, received, StringComparer.Ordinal));
      Assert.Equal(ring, shared.ForServer(Production).Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal(Enumerable.Range(101, 100).Select(reading => SteppingClock.Reading(reading)), shared.Entries().Select(entry => entry.Timestamp), StringComparer.Ordinal);
      Assert.Equal(200, Lines(file).Count);
   }



   /// <summary>The log cannot be built without its file or its sink, so a wiring mistake fails at start.</summary>
   [Fact]
   public void MissingFileOrSink_IsRefused()
   {
      var file = AgentActivityLog.Create(_directory);

      Assert.Throws<ArgumentNullException>(() => new ServiceLog(null!, _sink.Add));
      Assert.Throws<ArgumentNullException>(() => new ServiceLog(file, null!));
   }



   /// <summary>
   /// A clock that counts instead of keeping time: every reading is one second later than the one before, so each stamp
   /// names the reading that made it and no two are equal, whatever the computer's clock does. A test can run code of
   /// its own while one reading is being made, which is how it puts a second writer in the middle of a first one.
   /// </summary>
   private sealed class SteppingClock : TimeProvider
   {
      private static readonly DateTimeOffset s_start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
      private long _readings;



      /// <summary>Called during each reading with its number, the first being 1, before the reading returns.</summary>
      internal Action<long>? OnReading { get; set; }



      /// <summary>The timestamp the log writes for a given reading, in the round-trip form the ring holds.</summary>
      internal static string Reading(long number) => s_start.AddSeconds(number).ToString("O", CultureInfo.InvariantCulture);



      /// <summary>Counts the reading, lets the test act during it, and returns its time.</summary>
      public override DateTimeOffset GetUtcNow()
      {
         long number = Interlocked.Increment(ref _readings);
         OnReading?.Invoke(number);
         return s_start.AddSeconds(number);
      }
   }
}
