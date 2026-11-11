/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real Windows printing rules over a fake Windows, given to a running service in place of its printers, with a
/// note of what each read and each print asked that Windows. The service calls its printers from several threads at
/// once (the lanes, the inventory report, the pipe), and the fake Windows is one plain object, so every call is let
/// through one at a time; what the fake recorded during a call is then that call's and nobody else's. A test reads the
/// notes to tell which queues a job's own calls touched, apart from what the inventory report did meanwhile.
/// <para>
/// Handles are written by the order in which a call first used them (p1, p2 for queue handles, c1, c2 for device
/// contexts), because the fake numbers its handles across the whole life of the service and a test cannot know how many
/// were handed out before the call it looks at.
/// </para>
/// </summary>
internal sealed class RecordedPrinters(WindowsPrinters inner, FakeWindowsPrinting windows) : IPrinterBackend, IDisposable
{
   /// <summary>Lets one read or print through at a time.</summary>
   private readonly SemaphoreSlim _gate = new(1, 1);



   /// <summary>The notes taken so far; read and written only while the gate is held.</summary>
   private readonly List<(string Read, string[] Calls, bool Failed)> _reads = [];



   /// <summary>Every read and print since the notes were last forgotten, in order: its name (with the identifier or the job it was for), the calls it sent to the fake Windows, and whether it failed.</summary>
   internal (string Read, string[] Calls, bool Failed)[] Reads()
   {
      _gate.Wait();
      try
      {
         return [.. _reads];
      }
      finally
      {
         _ = _gate.Release();
      }
   }



   /// <summary>Forgets the notes so far and then changes the station as the test says, with no read or print in progress, so every note taken afterwards is of the changed station.</summary>
   internal void Forget(Action change)
   {
      _gate.Wait();
      try
      {
         _reads.Clear();
         windows.Calls.Clear();
         change();
      }
      finally
      {
         _ = _gate.Release();
      }
   }



   /// <summary>The full discovery, noted as "Discover".</summary>
   public IReadOnlyList<PrinterInfo> Discover() => Noted("Discover", inner.Discover);



   /// <summary>The list without media, noted as "ListWithoutMedia".</summary>
   public IReadOnlyList<PrinterInfo> ListWithoutMedia() => Noted("ListWithoutMedia", inner.ListWithoutMedia);



   /// <summary>The lookup of one queue, noted as "FindWithMedia" and the identifier.</summary>
   public PrinterInfo? FindWithMedia(string printerId) => Noted("FindWithMedia " + printerId, () => inner.FindWithMedia(printerId));



   /// <summary>The print of one staged file, noted as "PrintFileAsync" and the job identifier.</summary>
   public async Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
   {
      await _gate.WaitAsync(cancellationToken);
      int from = windows.Calls.Count;
      bool failed = true;
      try
      {
         string receipt = await inner.PrintFileAsync(job, path, submissionTimeout, cancellationToken);
         failed = false;
         return receipt;
      }
      finally
      {
         _reads.Add(("PrintFileAsync " + job.JobId, Normalize(windows.Calls.Skip(from)), failed));
         _ = _gate.Release();
      }
   }



   /// <summary>Releases the gate.</summary>
   public void Dispose() => _gate.Dispose();



   /// <summary>Runs one read alone and notes what it sent, whether it returned or threw.</summary>
   private T Noted<T>(string name, Func<T> read)
   {
      _gate.Wait();
      int from = windows.Calls.Count;
      bool failed = true;
      try
      {
         T result = read();
         failed = false;
         return result;
      }
      finally
      {
         _reads.Add((name, Normalize(windows.Calls.Skip(from)), failed));
         _ = _gate.Release();
      }
   }



   /// <summary>
   /// Rewrites the handle a call begins with by the order in which these calls first used it. A queue handle becomes p1,
   /// p2 and so on, a device context c1, c2 and so on; the fake hands out queue handles from 0x201 and contexts from 0xd01.
   /// </summary>
   private static string[] Normalize(IEnumerable<string> calls)
   {
      Dictionary<string, string> names = new(StringComparer.Ordinal);
      int queues = 0;
      int contexts = 0;
      List<string> rewritten = [];
      foreach(string call in calls)
      {
         int open = call.IndexOf('(', StringComparison.Ordinal);
         int end = call.IndexOfAny([',', ')'], open + 1);
         string first = call[(open + 1)..end];
         if(!first.StartsWith("0x", StringComparison.Ordinal))
         {
            rewritten.Add(call);
            continue;
         }
         if(!names.TryGetValue(first, out string? name))
         {
            bool context = int.Parse(first.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) >= 0xd00;
            name = context ? "c" + ++contexts : "p" + ++queues;
            names[first] = name;
         }
         rewritten.Add(string.Concat(call.AsSpan(0, open + 1), name, call.AsSpan(end)));
      }
      return [.. rewritten];
   }
}
