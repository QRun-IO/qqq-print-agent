/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Runtime.InteropServices;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The Windows spooler, printer settings and GDI as one object that exists only in memory. It answers from the queues a
/// test describes, records every call it was sent with its arguments in one list (so the order across the three is
/// known), keeps what was written to it, and can be told to fail any call the way the real one can. A failed call sets
/// the thread's last error, as the native call does, so the rules read it the same way. No print queue on this computer
/// is listed, opened or printed to. The calls are split by interface across the partial files.
/// </summary>
internal sealed partial class FakeWindowsPrinting : ISpooler, IPrinterSettings, IGdiPrinter
{
   private readonly Dictionary<IntPtr, FakeQueue> _printers = [];
   private readonly Dictionary<IntPtr, FakeQueue> _contexts = [];
   private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
   private readonly List<(string Call, int Occurrence, int Error, int Result)> _failures = [];
   private int _printerHandles;
   private int _contextHandles;



   /// <summary>The installed queues, in the order the spooler lists them.</summary>
   internal List<FakeQueue> Queues { get; } = [];



   /// <summary>Every call sent, in order, written as the call and its arguments.</summary>
   internal List<string> Calls { get; } = [];



   /// <summary>Queue handles that were opened and not yet closed.</summary>
   internal int OpenPrinters => _printers.Count;



   /// <summary>Device contexts that were created and not yet deleted.</summary>
   internal int OpenContexts => _contexts.Count;



   /// <summary>Adds a queue and returns it, so a test can describe it further.</summary>
   internal FakeQueue Add(FakeQueue queue)
   {
      Queues.Add(queue);
      return queue;
   }



   /// <summary>
   /// Makes one call fail: the given occurrence of the named call (the first unless said otherwise) sets the Windows error
   /// and returns the failure value, which is false for a call that answers yes or no and the given number for one that
   /// answers with a number.
   /// </summary>
   internal void Fail(string call, int error, int occurrence = 1, int result = 0) => _failures.Add((call, occurrence, error, result));



   /// <summary>Counts a call and says whether this occurrence was told to fail; when it was, the error is left as the thread's last error.</summary>
   private bool Failing(string call, out int result)
   {
      int occurrence = _seen[call] = _seen.GetValueOrDefault(call) + 1;
      foreach((string name, int at, int error, int value) in _failures)
      {
         if(string.Equals(name, call, StringComparison.Ordinal) && at == occurrence)
         {
            Marshal.SetLastPInvokeError(error);
            result = value;
            return true;
         }
      }
      result = 0;
      return false;
   }



   /// <summary>Finds a queue by name; a name the spooler does not know is null.</summary>
   private FakeQueue? Find(string name) => Queues.Find(queue => string.Equals(queue.Name, name, StringComparison.Ordinal));



   /// <summary>The queue an open handle belongs to; using a handle that is not open fails the test.</summary>
   private FakeQueue Printer(IntPtr printer) => _printers.TryGetValue(printer, out FakeQueue? queue) ? queue : throw new InvalidOperationException("A queue handle was used that is not open.");



   /// <summary>The queue a device context belongs to; using a context that does not exist fails the test.</summary>
   private FakeQueue Context(IntPtr dc) => _contexts.TryGetValue(dc, out FakeQueue? queue) ? queue : throw new InvalidOperationException("A device context was used that does not exist.");



   /// <summary>Writes a handle the way the call log shows it.</summary>
   private static string Handle(IntPtr handle) => "0x" + handle.ToInt64().ToString("x", CultureInfo.InvariantCulture);
}
