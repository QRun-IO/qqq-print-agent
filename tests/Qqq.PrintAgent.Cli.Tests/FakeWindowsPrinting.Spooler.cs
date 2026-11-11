/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The spooler part of the fake: listing the queues, opening and closing one, and taking one RAW document.
/// </summary>
internal sealed partial class FakeWindowsPrinting
{
   private readonly Dictionary<IntPtr, FakeSpoolDocument> _openDocuments = [];
   private uint _spoolJobs = 4100;



   /// <summary>When set, the size the sizing call reports for the queue list instead of 136 bytes a queue.</summary>
   internal uint? ReportedListSize { get; set; }



   /// <summary>Every RAW document that was started, in order.</summary>
   internal List<FakeSpoolDocument> RawDocuments { get; } = [];



   /// <summary>When set, the most bytes the spooler takes in one write, so a write can be partial.</summary>
   internal int? WriteLimit { get; set; }



   /// <summary>When set, turns the number of bytes taken into the number the spooler reports, so a test can make it report nonsense.</summary>
   internal Func<int, int>? WrittenAnswer { get; set; }



   /// <summary>
   /// Answers the sizing call the way Windows does: true with no size when there is nothing to list, otherwise false,
   /// error 122 and the size the list needs. A failure a test asked for keeps the reported size and sets its own error.
   /// </summary>
   public bool EnumPrintersSize(uint flags, out uint needed)
   {
      StaleLastError.Leave();
      Calls.Add($"EnumPrintersSize({flags})");
      needed = ReportedListSize ?? (uint)(Queues.Count * 136);
      if(Failing("EnumPrintersSize", out _))
      {
         return false;
      }
      if(needed == 0)
      {
         return true;
      }
      Marshal.SetLastPInvokeError(122);
      return false;
   }



   /// <summary>Lists the queues as plain records, or nothing when told to fail.</summary>
   public bool EnumPrinters(uint flags, int size, out IReadOnlyList<SpoolerQueue> queues)
   {
      StaleLastError.Leave();
      Calls.Add($"EnumPrinters({flags}, {size})");
      if(Failing("EnumPrinters", out _))
      {
         queues = [];
         return false;
      }
      queues = [.. Queues.Select(queue => new SpoolerQueue(queue.Name, queue.Driver, queue.Port, queue.Status))];
      return true;
   }



   /// <summary>Opens a known queue and hands out a new handle; an unknown name fails with error 1801, a queue can refuse with its own error, and a test can make one particular open fail.</summary>
   public bool OpenPrinter(string name, out IntPtr printer)
   {
      StaleLastError.Leave();
      Calls.Add($"OpenPrinter({name})");
      printer = IntPtr.Zero;
      if(Failing("OpenPrinter", out _))
      {
         return false;
      }
      FakeQueue? queue = Find(name);
      if(queue is null || queue.OpenError is not null)
      {
         Marshal.SetLastPInvokeError(queue?.OpenError ?? 1801);
         return false;
      }
      printer = new IntPtr(0x200 + ++_printerHandles);
      _printers[printer] = queue;
      return true;
   }



   /// <summary>Starts a document on the open queue and returns a new job number, or zero when told to fail.</summary>
   public uint StartDocPrinter(IntPtr printer, string documentName, string dataType)
   {
      StaleLastError.Leave();
      Calls.Add($"StartDocPrinter({Handle(printer)}, {documentName}, {dataType})");
      FakeQueue queue = Printer(printer);
      if(Failing("StartDocPrinter", out _))
      {
         return 0;
      }
      FakeSpoolDocument document = new(queue.Name, documentName, dataType, ++_spoolJobs);
      RawDocuments.Add(document);
      _openDocuments[printer] = document;
      return document.Job;
   }



   /// <summary>Takes the bytes offered, or only as many as the write limit allows, into the open document and reports the count.</summary>
   public bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written)
   {
      StaleLastError.Leave();
      Calls.Add($"WritePrinter({Handle(printer)}, {bytes.Length} bytes, {count})");
      written = 0;
      if(Failing("WritePrinter", out _))
      {
         return false;
      }
      int taken = Math.Min(count, WriteLimit ?? count);
      _openDocuments[printer].Bytes.Write(bytes, 0, taken);
      written = WrittenAnswer?.Invoke(taken) ?? taken;
      return true;
   }



   /// <summary>Finishes the open document, or leaves it open when told to fail.</summary>
   public bool EndDocPrinter(IntPtr printer)
   {
      StaleLastError.Leave();
      Calls.Add($"EndDocPrinter({Handle(printer)})");
      if(Failing("EndDocPrinter", out _))
      {
         return false;
      }
      _openDocuments[printer].Ended = true;
      _ = _openDocuments.Remove(printer);
      return true;
   }



   /// <summary>Discards the open document.</summary>
   public bool AbortPrinter(IntPtr printer)
   {
      StaleLastError.Leave();
      Calls.Add($"AbortPrinter({Handle(printer)})");
      _openDocuments[printer].Aborted = true;
      _ = _openDocuments.Remove(printer);
      return true;
   }



   /// <summary>Closes a handle this fake handed out; closing anything else fails the test.</summary>
   public bool ClosePrinter(IntPtr printer)
   {
      StaleLastError.Leave();
      Calls.Add($"ClosePrinter({Handle(printer)})");
      Assert.True(_printers.Remove(printer), "A queue handle was closed that was not open.");
      return true;
   }
}
