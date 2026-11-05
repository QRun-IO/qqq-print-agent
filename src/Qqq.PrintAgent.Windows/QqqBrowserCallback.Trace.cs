/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Diagnostics for the browser hand-off. The protocol handler runs as the ordinary (non-elevated) user, launched by the
/// browser, so it cannot write the protected machine log; it appends one-line, credential-free entries to a small per-user
/// trace file instead, and the companion copies the recent entries into the activity log when a browser sign-in does not
/// complete. The callback address itself (which carries the one-time code and state) is never written.
/// </summary>
public sealed partial class QqqBrowserCallback
{
   private const long TraceLimit = 256 * 1024;



   /// <summary>The per-user trace file the protocol handler writes.</summary>
   public static string TracePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QqqPrintAgent", "auth-callback.log");



   /// <summary>Forwards a callback from the protocol handler and records each step and the outcome in the trace file, so a hand-off that never reaches the companion can be diagnosed afterwards.</summary>
   public static async Task ForwardFromHandlerAsync(string callback, CancellationToken token = default)
   {
      ArgumentNullException.ThrowIfNull(callback);
      Trace("Protocol handler started by the browser (process " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ", session "
         + System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture) + "); callback length " + callback.Length.ToString(CultureInfo.InvariantCulture) + ".");
      try
      {
         await ForwardAsync(callback, PipeName(), InstalledCompanionPath(), token, Trace).ConfigureAwait(false);
         Trace("Callback delivered: the companion acknowledged it.");
      }
      catch(Exception exception)
      {
         Trace("Callback NOT delivered: " + exception.GetType().Name + ": " + exception.Message
            + (exception.InnerException is null ? "" : " (" + exception.InnerException.GetType().Name + ": " + exception.InnerException.Message + ")"));
         throw;
      }
   }



   /// <summary>Returns the trace entries written within the window (newest last, at most 40), or an empty list when there is no trace or it cannot be read.</summary>
   public static IReadOnlyList<string> RecentTrace(TimeSpan window)
   {
      try
      {
         if(!File.Exists(TracePath))
         { return []; }
         DateTimeOffset cutoff = DateTimeOffset.Now - window;
         var recent = new List<string>();
         foreach(string line in File.ReadLines(TracePath))
         {
            if(line.Length > 24 && DateTimeOffset.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset at) && at >= cutoff)
            { recent.Add(line); }
         }
         return recent.Count <= 40 ? recent : recent[^40..];
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         return [];
      }
   }



   /// <summary>Appends one timestamped line to the per-user trace, truncating the file when it grows past the limit; a failure to write never affects the hand-off.</summary>
   private static void Trace(string message)
   {
      try
      {
         string path = TracePath;
         Directory.CreateDirectory(Path.GetDirectoryName(path)!);
         if(File.Exists(path) && new FileInfo(path).Length > TraceLimit)
         { File.Delete(path); }
         File.AppendAllText(path, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         // The trace is best-effort diagnostics only.
      }
   }
}
