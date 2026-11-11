/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The service's log as a type of its own. It must write exactly what the runtime's own log method wrote before it was
/// moved here (the same redaction, caps, routing of debug detail, ring and file lines), and a view of it for one server
/// must add that server's name and change nothing else.
/// </summary>
public sealed class ServiceLogTests : IDisposable
{
   private readonly string _directory = Path.Combine(Path.GetTempPath(), "qqq-service-log-tests", Guid.NewGuid().ToString("N"));
   private readonly string _referenceDirectory = Path.Combine(Path.GetTempPath(), "qqq-service-log-tests", Guid.NewGuid().ToString("N"));
   private readonly List<string> _sink = [];



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
   /// content, and a user name and password inside an address.
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
   /// What the redaction does not recognise is unchanged by the move, and recorded here so that it is a known fact and not
   /// a surprise: a session cookie and the one-time code and state of a sign-in callback are not credential-shaped to the
   /// patterns. They stay out of the log because the service never writes them: addresses are logged without their query,
   /// and cookies are never logged at all.
   /// </summary>
   [Theory]
   [InlineData("Cookie: QQQ_SESSION=synthetic-cookie")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-code&state=synthetic-state")]
   public void WhatTheRedactionDoesNotRecognise_IsTheSameAsBefore(string message)
   {
      (ServiceLog log, _) = Create();

      log.Write("INFO", message);

      Assert.Equal(message, Assert.Single(_sink));
      Assert.Equal(message, AgentActivityLog.Redact(message));
      Assert.Equal("io.qrun.qqq-print-agent:///oauth/callback", QqqSessionDiagnostics.Describe(new Uri("io.qrun.qqq-print-agent:/oauth/callback?code=synthetic-code&state=synthetic-state")));
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



   /// <summary>Clearing the ring empties what the companion is shown and nothing else; later lines are kept again.</summary>
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
   /// It never enters the ring or the host's sink, so it cannot displace the operational entries the companion shows.
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
   /// A view for one server writes the same lines with that server's name in front, in the ring, the sink and the file.
   /// It shares the ring and the file with the log it came from, so lines from the service and from each server appear
   /// together in the order they were written.
   /// </summary>
   [Fact]
   public void ForServer_PrefixesTheServerNameAndSharesTheRingAndTheFile()
   {
      (ServiceLog log, AgentActivityLog file) = Create();
      ServiceLog production = log.ForServer("Production");
      ServiceLog staging = log.ForServer("Staging");

      log.Write("INFO", "Print service started.");
      production.Write("WARN", "Printing blocked.");
      staging.Write("ERROR", "Local command failed.");
      production.Write("INFO", "Signed in.");

      string[] expected = ["Print service started.", "[Production] Printing blocked.", "[Staging] Local command failed.", "[Production] Signed in."];
      Assert.Equal(expected, _sink, StringComparer.Ordinal);
      Assert.Equal(expected, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal(expected, staging.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal(["INFO", "WARN", "ERROR", "INFO"], production.Entries().Select(entry => entry.Level), StringComparer.Ordinal);
      Assert.Equal([" [INFO] [Service] Print service started.", " [WARN] [Service] [Production] Printing blocked.", " [ERROR] [Service] [Staging] Local command failed.", " [INFO] [Service] [Production] Signed in."], Lines(file));
      staging.ClearEntries();
      Assert.Empty(log.Entries());
   }



   /// <summary>
   /// A server's view redacts exactly as the log does, and the caps do not grow by the name: the line is still at most
   /// 1000 characters (4000 for debug detail) with the name counted in. Debug detail is still dropped while it is off
   /// and goes to the file only while it is on, and a failure and the session client's progress carry the name too.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void ForServer_KeepsRedactionCapsAndDebugRouting(bool debug)
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug);
      ServiceLog server = log.ForServer("Production");

      server.Write("ERROR", "Authorization: Bearer synthetic-server-bearer enrollmentCode=synthetic-server-code");
      server.Write("INFO", new string('p', 1500));
      server.Write("DEBUG", new string('q', 4500));
      server.Failure("Sign-in failed", new IOException("password=synthetic-server-password"));
      server.SessionDiagnostics().Info("Discovery started.");

      Assert.Equal(debug, server.IsDebugEnabled);
      Assert.Equal(
      [
         "[Production] Authorization: Bearer [REDACTED] enrollmentCode=[REDACTED]",
         "[Production] " + new string('p', 1000 - "[Production] ".Length),
         "[Production] Sign-in failed: IOException: password=[REDACTED]",
         "[Production] [Session] Discovery started.",
      ], _sink);
      Assert.Equal(_sink, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      List<string> lines = Lines(file);
      Assert.Equal(debug ? 6 : 4, lines.Count(line => line.StartsWith(" [", StringComparison.Ordinal)));
      Assert.Equal(debug, lines.Contains(" [INFO] [DEBUG] [Service] [Production] " + new string('q', 4000 - "[Production] ".Length), StringComparer.Ordinal));
      Assert.DoesNotContain(lines, line => line.Contains("synthetic-server", StringComparison.Ordinal));
   }



   /// <summary>
   /// A server's name is an operator's text, so it is made safe to stand at the front of a line: a line break or other
   /// control character becomes a space (it cannot start a forged log entry), surrounding spaces go, and a long name is
   /// cut at 64 characters. A name with nothing in it is refused.
   /// </summary>
   [Fact]
   public void ForServer_MakesTheNameSafeToPrefix()
   {
      (ServiceLog log, AgentActivityLog file) = Create();

      log.ForServer("  Warehouse\r\n2026-11-10 12:00:00 [ERROR] forged\t ").Write("INFO", "one");
      log.ForServer(new string('n', 80)).Write("INFO", "two");

      Assert.Equal(["[Warehouse  2026-11-10 12:00:00 [ERROR] forged] one", "[" + new string('n', 64) + "] two"], _sink);
      Assert.Equal(2, File.ReadAllLines(file.CurrentLogFilePath).Length);
      Assert.Equal(64, ServiceLog.ServerNameLimit);
      Assert.Throws<ArgumentException>(() => log.ForServer("   "));
      Assert.Throws<ArgumentNullException>(() => log.ForServer(null!));
   }



   /// <summary>
   /// A server's name cannot switch off the redaction of the line it stands in front of. The redaction reads a quoted
   /// value up to the next quote, so a name ending in an open quote, redacted as one string with the line, pairs with
   /// the quote in front of the password and leaves the password itself in the log. The name and the line are redacted
   /// apart: the password is gone from the ring, the host's sink and the file, for an operational line, for debug detail
   /// and for a failure, and a secret written into the name itself is gone as well.
   /// </summary>
   [Theory]
   [InlineData("A token=\"", "[A token=[REDACTED]] ")]
   [InlineData("B secret='", "[B secret=[REDACTED]] ")]
   [InlineData("C password=\"synthetic-name-secret", "[C password=[REDACTED]] ")]
   public void ForServer_ANameCannotDefeatTheRedactionOfItsLine(string name, string prefix)
   {
      (ServiceLog log, AgentActivityLog file) = Create(debug: true);
      ServiceLog server = log.ForServer(name);

      server.Write("ERROR", "Sign-in failed: password=\"hunter2\" rejected");
      server.Write("DEBUG", "POST /session password=\"hunter2\" rejected");
      server.Failure("Sign-in failed", new IOException("token=\"hunter2\" refused"));

      Assert.Equal([prefix + "Sign-in failed: password=[REDACTED] rejected", prefix + "Sign-in failed: IOException: token=[REDACTED] refused"], _sink);
      Assert.Equal(_sink, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      string written = File.ReadAllText(file.CurrentLogFilePath);
      Assert.DoesNotContain("hunter2", written, StringComparison.Ordinal);
      Assert.DoesNotContain("synthetic-name-secret", written, StringComparison.Ordinal);
      Assert.Contains("POST /session password=[REDACTED] rejected", written, StringComparison.Ordinal);
      Assert.Equal(4, File.ReadAllLines(file.CurrentLogFilePath).Count(line => line.Contains(" [Service] ", StringComparison.Ordinal)));
   }



   /// <summary>
   /// A character in a server's name that would end the line or change the order it is shown in becomes a space, as a
   /// control character does: the line and paragraph separators (U+2028, U+2029), the next-line control (U+0085), the
   /// bidirectional embeddings, overrides and isolates with the characters that end them (U+202A to U+202E, U+2066 to
   /// U+2069), the directional marks and the zero-width characters. None of them is a control character, so each used
   /// to pass through, and a name could show a forged line or text in reverse.
   /// </summary>
   [Theory]
   [InlineData(0x0085)]
   [InlineData(0x2028)]
   [InlineData(0x2029)]
   [InlineData(0x202A)]
   [InlineData(0x202B)]
   [InlineData(0x202C)]
   [InlineData(0x202D)]
   [InlineData(0x202E)]
   [InlineData(0x2066)]
   [InlineData(0x2067)]
   [InlineData(0x2068)]
   [InlineData(0x2069)]
   [InlineData(0x200E)]
   [InlineData(0x200F)]
   [InlineData(0x061C)]
   [InlineData(0x200B)]
   [InlineData(0xFEFF)]
   public void ForServer_ACharacterThatEndsOrReordersALine_BecomesASpace(int codePoint)
   {
      (ServiceLog log, AgentActivityLog file) = Create();
      char character = (char)codePoint;

      log.ForServer("Ware" + character + "house" + character).Write("INFO", "one");
      log.ForServer(character + "2026-11-10 12:00:00 [ERROR] forged" + character + character + "entry").Write("INFO", "two");

      Assert.Equal(["[Ware house] one", "[2026-11-10 12:00:00 [ERROR] forged  entry] two"], _sink);
      Assert.Equal(_sink, log.Entries().Select(entry => entry.Message), StringComparer.Ordinal);
      Assert.Equal([" [INFO] [Service] [Ware house] one", " [INFO] [Service] [2026-11-10 12:00:00 [ERROR] forged  entry] two"], Lines(file));
      Assert.DoesNotContain(character.ToString(), File.ReadAllText(file.CurrentLogFilePath), StringComparison.Ordinal);
      Assert.Throws<ArgumentException>(() => log.ForServer(new string(character, 3)));
   }



   /// <summary>Several threads writing at once lose no line and never corrupt the ring.</summary>
   [Fact]
   public async Task ConcurrentWriters_LoseNothing()
   {
      (ServiceLog log, AgentActivityLog file) = Create();
      ServiceLog server = log.ForServer("Production");
      List<string> received = [];
      var shared = new ServiceLog(file, line =>
      {
         lock(received)
         { received.Add(line); }
      });

      await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
      {
         for(int index = 0; index < 25; index++)
         {
            (writer % 2 == 0 ? shared : shared.ForServer("Production")).Write("INFO", $"writer {writer} line {index}");
         }
      })));

      Assert.Equal(200, received.Count);
      Assert.Equal(200, received.Distinct(StringComparer.Ordinal).Count());
      Assert.Equal(100, shared.Entries().Length);
      Assert.Empty(server.Entries());
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
}
