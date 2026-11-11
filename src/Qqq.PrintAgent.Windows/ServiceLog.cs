/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The service's own log: one place that redacts, bounds and routes every line the service writes. A line goes to the
/// in-memory ring (the newest 100 operational entries), to the shared activity log file with the "[Service]" marker,
/// and to the host's sink. The ring is what version 1 of the pipe carries in its snapshot, which the command line
/// prints; the companion does not show it (its Logs page reads the activity log file), and ADR-0004 removes the ring
/// with version 1. Debug detail goes to the file only, and only while debug logging is on.
/// It is a type of its own, not part of the runtime, because it belongs to the computer and not to one server
/// connection: a connection is given a view of it through <see cref="ForServer"/> that puts that server's short
/// identifier in front of each line and otherwise behaves exactly the same.
/// </summary>
internal sealed class ServiceLog
{
   /// <summary>How many operational entries the in-memory ring keeps; the oldest is dropped first.</summary>
   internal const int RingCapacity = 100;



   /// <summary>The longest operational line, in characters, after redaction.</summary>
   internal const int LineLimit = 1000;



   /// <summary>The longest line of debug detail, in characters, after redaction; it is longer because it carries exception text.</summary>
   internal const int DebugLimit = 4000;



   private readonly Sinks _sinks;
   private readonly string _prefix;



   /// <summary>
   /// Creates the log over the shared activity log file and the host's own sink, which receives each operational line
   /// after redaction and bounding. The clock stamps the entries of the ring; it is the system clock unless a test
   /// gives another, so that the order of stamping can be tested without waiting on real time.
   /// </summary>
   internal ServiceLog(AgentActivityLog activityLog, Action<string> sink, TimeProvider? clock = null) : this(new Sinks(activityLog, sink, clock ?? TimeProvider.System), string.Empty)
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
   /// Records a service message in the in-memory ring (newest 100) and in the shared activity log.
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
      // the prefix of a server's view is that server's short identifier, which holds //
      // nothing to redact; it is joined on after the message is redacted, so the     //
      // line a view writes is the log's own line with eight digits in front of it    //
      //////////////////////////////////////////////////////////////////////////////////
      string sanitized = _prefix + AgentActivityLog.Redact(message);
      int cap = debug ? DebugLimit : LineLimit;
      string bounded = sanitized.Length <= cap ? sanitized : sanitized[..cap];
      if(debug)
      {
         _sinks.ActivityLog.Debug("[Service] " + bounded);
         return;
      }
      _sinks.Remember(level, bounded);
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



   /// <summary>A copy of the ring, oldest entry first, for the version 1 snapshot.</summary>
   internal AgentLogEntry[] Entries() => _sinks.Entries();



   /// <summary>Empties the ring, which is what clearing the logs does to the entries the snapshot carries; the files are cleared by the caller.</summary>
   internal void ClearEntries() => _sinks.Forget();



   /// <summary>
   /// A view of this log for one server's connection. Every line it writes starts with the server's short identifier
   /// in square brackets (the first eight characters of its identifier, which the command line's <c>--server</c>
   /// accepts), so lines from several connections can be told apart; redaction, the caps (which then include the
   /// prefix), the debug routing, the ring and the file are the same ones. The prefix is the identifier and not the
   /// server's name (ADR-0004, "Server names"): the activity log file redacts each joined line a second time, so a name
   /// holding a credential keyword would hide that server's lines, square brackets in a name would forge structure
   /// inside a line, and a name changes when the server is renamed. Eight hexadecimal digits can do none of that, so
   /// nothing here has to make an operator's text safe. Anything but a full identifier is refused.
   /// </summary>
   internal ServiceLog ForServer(string serverId)
   {
      if(!ManagementProtocol.IsServerId(serverId))
      {
         throw new ArgumentException("A server's view of the log is named by the server's full identifier.", nameof(serverId));
      }
      return new ServiceLog(_sinks, "[" + serverId[..ServerSelector.MinimumPrefixLength] + "] ");
   }



   /// <summary>What a log and every view made from it share: the file, the host's sink, and the ring with its lock and the clock that stamps its entries.</summary>
   private sealed class Sinks(AgentActivityLog activityLog, Action<string> sink, TimeProvider clock)
   {
      private readonly Lock _lock = new();
      private readonly Queue<AgentLogEntry> _ring = new();



      /// <summary>The shared activity log file.</summary>
      internal AgentActivityLog ActivityLog { get; } = activityLog;



      /// <summary>The host's sink for operational lines.</summary>
      internal Action<string> Sink { get; } = sink;



      /// <summary>
      /// Adds an entry to the ring and drops the oldest ones beyond its capacity. The entry is stamped with the time
      /// inside the lock that orders the ring, so the ring's order is the order of its timestamps: stamped before the
      /// lock, two writers could take their times in one order and enter the ring in the other.
      /// </summary>
      internal void Remember(string level, string message)
      {
         lock(_lock)
         {
            _ring.Enqueue(new AgentLogEntry(clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), level, message));
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
