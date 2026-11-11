/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The service's own log: one place that redacts, bounds and routes every line the service writes. A line goes to the
/// in-memory ring the companion shows (the newest 100 operational entries), to the shared activity log file with the
/// "[Service]" marker, and to the host's sink. Debug detail goes to the file only, and only while debug logging is on.
/// It is a type of its own, not part of the runtime, because it belongs to the computer and not to one server
/// connection: a connection is given a view of it through <see cref="ForServer"/> that puts that server's name in front
/// of each line and otherwise behaves exactly the same.
/// </summary>
internal sealed class ServiceLog
{
   /// <summary>How many operational entries the in-memory ring keeps; the oldest is dropped first.</summary>
   internal const int RingCapacity = 100;

   /// <summary>The longest operational line, in characters, after redaction.</summary>
   internal const int LineLimit = 1000;

   /// <summary>The longest line of debug detail, in characters, after redaction; it is longer because it carries exception text.</summary>
   internal const int DebugLimit = 4000;

   /// <summary>The longest server name a prefixed view shows, in characters, so an operator's long name cannot use up a line.</summary>
   internal const int ServerNameLimit = 64;

   private readonly Sinks _sinks;
   private readonly string _prefix;



   /// <summary>Creates the log over the shared activity log file and the host's own sink, which receives each operational line after redaction and bounding.</summary>
   internal ServiceLog(AgentActivityLog activityLog, Action<string> sink) : this(new Sinks(activityLog, sink), string.Empty)
   {
      ArgumentNullException.ThrowIfNull(activityLog);
      ArgumentNullException.ThrowIfNull(sink);
   }



   /// <summary>Creates a view over the same sinks and ring with a prefix for every line.</summary>
   private ServiceLog(Sinks sinks, string prefix)
   {
      _sinks = sinks;
      _prefix = prefix;
   }



   /// <summary>True while the saved settings ask for debug detail, so a caller can skip building detail nobody will see.</summary>
   internal bool IsDebugEnabled => _sinks.ActivityLog.IsDebugEnabled;



   /// <summary>
   /// Records a service message in the in-memory ring shown to the companion (newest 100) and in the shared activity log.
   /// The text is redacted first and capped at 1000 characters (4000 for debug detail), so credentials and document content
   /// cannot reach either sink. DEBUG entries go only to the file, only while debug logging is on, and never displace the
   /// ring's operational entries. A level other than ERROR, WARN or DEBUG is written to the file as information and kept
   /// in the ring under the level it was given.
   /// </summary>
   internal void Write(string level, string message)
   {
      bool debug = string.Equals(level, "DEBUG", StringComparison.Ordinal);
      if(debug && !_sinks.ActivityLog.IsDebugEnabled)
      { return; }
      //////////////////////////////////////////////////////////////////////////////////
      // the name in the prefix was redacted when the view was made, and is joined on //
      // only here: redacted as one string with the message, a name ending in an open //
      // quote would pair with the quote in front of a secret in the message, and the //
      // secret itself would be left outside what is redacted                         //
      //////////////////////////////////////////////////////////////////////////////////
      string sanitized = _prefix + AgentActivityLog.Redact(message);
      int cap = debug ? DebugLimit : LineLimit;
      string bounded = sanitized.Length <= cap ? sanitized : sanitized[..cap];
      if(debug)
      {
         _sinks.ActivityLog.Debug("[Service] " + bounded);
         return;
      }
      _sinks.Remember(new AgentLogEntry(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), level, bounded));
      switch(level)
      {
         case "ERROR":
            _sinks.ActivityLog.Error("[Service] " + bounded);
            break;
         case "WARN":
            _sinks.ActivityLog.Warning("[Service] " + bounded);
            break;
         default:
            _sinks.ActivityLog.Info("[Service] " + bounded);
            break;
      }
      _sinks.Sink(bounded);
   }



   /// <summary>Records a failure with its type and message at ERROR, and the full exception (stack and inner causes) as debug detail.</summary>
   internal void Failure(string context, Exception exception)
   {
      Write("ERROR", context + ": " + QqqSessionDiagnostics.Describe(exception));
      Write("DEBUG", context + " detail: " + exception);
   }



   /// <summary>The session client's progress sink: step entries at INFO, request detail at DEBUG, both through this redacting log.</summary>
   internal QqqSessionDiagnostics SessionDiagnostics() => new(message => Write("INFO", "[Session] " + message),
      message => Write("DEBUG", "[Session] " + message), () => _sinks.ActivityLog.IsDebugEnabled);



   /// <summary>A copy of the ring, oldest entry first, for the snapshot the companion reads.</summary>
   internal AgentLogEntry[] Entries() => _sinks.Entries();



   /// <summary>Empties the ring, which is what clearing the logs does to the entries the companion shows; the files are cleared by the caller.</summary>
   internal void ClearEntries() => _sinks.Forget();



   /// <summary>
   /// A view of this log for one server's connection. Every line it writes starts with the server's name in square
   /// brackets, so lines from several connections can be told apart; redaction, the caps (which then include the name),
   /// the debug routing, the ring and the file are the same ones. The name is an operator's text, so it is made safe
   /// here, once: a character that would end the line or reorder it becomes a space (see <see cref="StaysInItsLine"/>),
   /// the name is redacted on its own, and it is cut at 64 characters. It is redacted apart from the lines it will
   /// stand in front of because the redaction reads a quoted value up to the next quote: a name ending in an open quote,
   /// redacted together with a line, would pair with the quote in front of a secret in that line and leave the secret
   /// in the log. A name with nothing left to show is refused.
   /// </summary>
   internal ServiceLog ForServer(string name)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(name);
      string plain = new string(name.Select(character => StaysInItsLine(character) ? character : ' ').ToArray()).Trim();
      ArgumentException.ThrowIfNullOrWhiteSpace(plain, nameof(name));
      string shown = AgentActivityLog.Redact(plain);
      return new ServiceLog(_sinks, "[" + (shown.Length <= ServerNameLimit ? shown : shown[..ServerNameLimit]) + "] ");
   }



   /// <summary>
   /// True for a character that shows as itself where it stands. False for the ones that let a name forge or disguise a
   /// log line: control characters, the line and paragraph separators (U+2028, U+2029) that end a line as surely as a
   /// newline, and the format characters, which include the bidirectional embeddings, overrides and isolates that make
   /// text display in another order than it was written. It is the rule Core applies to text a server sends.
   /// </summary>
   private static bool StaysInItsLine(char character) => char.GetUnicodeCategory(character) is not (UnicodeCategory.Control
      or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);



   /// <summary>What a log and every view made from it share: the file, the host's sink and the ring with its lock.</summary>
   private sealed class Sinks(AgentActivityLog activityLog, Action<string> sink)
   {
      private readonly Lock _lock = new();
      private readonly Queue<AgentLogEntry> _ring = new();



      /// <summary>The shared activity log file.</summary>
      internal AgentActivityLog ActivityLog { get; } = activityLog;



      /// <summary>The host's sink for operational lines.</summary>
      internal Action<string> Sink { get; } = sink;



      /// <summary>Adds an entry to the ring and drops the oldest ones beyond its capacity.</summary>
      internal void Remember(AgentLogEntry entry)
      {
         lock(_lock)
         {
            _ring.Enqueue(entry);
            while(_ring.Count > RingCapacity)
            {
               _ring.Dequeue();
            }
         }
      }



      /// <summary>Copies the ring, oldest first.</summary>
      internal AgentLogEntry[] Entries()
      {
         lock(_lock)
         { return _ring.ToArray(); }
      }



      /// <summary>Empties the ring.</summary>
      internal void Forget()
      {
         lock(_lock)
         { _ring.Clear(); }
      }
   }
}
