/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Diagnostics for the browser hand-off. The protocol handler runs as the ordinary (non-elevated) user, launched by the
/// browser, so it cannot write the protected machine log; it appends one-line, credential-free entries to a small per-user
/// trace file instead, and the companion copies the recent entries into the activity log when a browser sign-in does not
/// complete. The callback address itself (which carries the one-time code and state) is never written. How entries are
/// written and read back is in <see cref="BrowserCallbackTrace"/>; this part says what the handler records.
/// </summary>
public sealed partial class QqqBrowserCallback
{
   /// <summary>The per-user trace file the protocol handler writes.</summary>
   public static string TracePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QqqPrintAgent", "auth-callback.log");



   /// <summary>Forwards a callback from the protocol handler and records each step and the outcome in the trace file, so a hand-off that never reaches the companion can be diagnosed afterwards.</summary>
   [ExcludeFromCodeCoverage(Justification = "Binds the hand-off to this user's real trace file, this session's real callback pipe and the installed companion under Program Files. A test must not write the user's trace file and does not run from the installed location. It only passes the call on; what is recorded and what happens on failure is tested through the overload below with a trace in memory.")]
   public static Task ForwardFromHandlerAsync(string callback, CancellationToken token = default)
      => ForwardFromHandlerAsync(callback, new BrowserCallbackTrace(new DiskTraceFile(TracePath), TimeProvider.System), steps => ForwardAsync(callback, PipeName(), InstalledCompanionPath(), token, steps));



   /// <summary>
   /// Records that the handler started, forwards the callback, and records the outcome: delivered, or not delivered with
   /// the kind and message of the failure, which is then passed on. Only the callback's length is written, never the
   /// callback, and the steps of the forwarding itself go to the same trace.
   /// </summary>
   internal static async Task ForwardFromHandlerAsync(string callback, BrowserCallbackTrace trace, Func<Action<string>, Task> forward)
   {
      ArgumentNullException.ThrowIfNull(callback);
      trace.Write("Protocol handler started by the browser (process " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ", session "
         + System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture) + "); callback length " + callback.Length.ToString(CultureInfo.InvariantCulture) + ".");
      try
      {
         await forward(trace.Write).ConfigureAwait(false);
         trace.Write("Callback delivered: the companion acknowledged it.");
      }
      catch(Exception exception)
      {
         trace.Write("Callback NOT delivered: " + exception.GetType().Name + ": " + exception.Message
            + (exception.InnerException is null ? "" : " (" + exception.InnerException.GetType().Name + ": " + exception.InnerException.Message + ")"));
         throw;
      }
   }



   /// <summary>Returns the trace entries written within the window (newest last, at most 40), or an empty list when there is no trace or it cannot be read.</summary>
   [ExcludeFromCodeCoverage(Justification = "Reads this user's real trace file, whose content depends on what the user has done on this computer. It only passes the call on; which entries count as recent is tested in BrowserCallbackTrace with a trace in memory.")]
   public static IReadOnlyList<string> RecentTrace(TimeSpan window) => new BrowserCallbackTrace(new DiskTraceFile(TracePath), TimeProvider.System).Recent(window);
}
