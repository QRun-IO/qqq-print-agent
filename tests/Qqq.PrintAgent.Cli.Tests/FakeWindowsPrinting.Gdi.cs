/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Runtime.InteropServices;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The GDI part of the fake: device contexts that report what their <see cref="FakeQueue"/> says, and documents of
/// bitmap pages that are only counted and kept.
/// </summary>
internal sealed partial class FakeWindowsPrinting
{
   private readonly Dictionary<IntPtr, FakeGdiDocument> _openGdiDocuments = [];
   private int _gdiJobs = 7300;



   /// <summary>The settings each device context was created from, in order; null stands for the queue's current defaults.</summary>
   internal List<byte[]?> ContextModes { get; } = [];



   /// <summary>Every GDI document that was started, in order.</summary>
   internal List<FakeGdiDocument> GdiDocuments { get; } = [];



   /// <summary>Creates a device context for a known queue; an unknown device, a queue that refuses or a failure a test asked for returns zero with the error.</summary>
   public IntPtr CreateDC(string driver, string device, byte[]? devMode)
   {
      StaleLastError.Leave();
      Calls.Add($"CreateDC({driver}, {device}, {(devMode is null ? "null" : $"{devMode.Length} bytes")})");
      ContextModes.Add(devMode?.ToArray());
      FakeQueue? queue = Find(device);
      if(queue is null || queue.CreateContextError is not null)
      {
         Marshal.SetLastPInvokeError(queue?.CreateContextError ?? 1801);
         return IntPtr.Zero;
      }
      if(Failing("CreateDC", out _))
      {
         return IntPtr.Zero;
      }
      IntPtr dc = new(0xd00 + ++_contextHandles);
      _contexts[dc] = queue;
      return dc;
   }



   /// <summary>Reports what the context's queue says for the index; an index the queue does not describe fails the test, so the rules cannot lean on a value nobody chose.</summary>
   public int GetDeviceCaps(IntPtr dc, int index)
   {
      Calls.Add($"GetDeviceCaps({Handle(dc)}, {index})");
      return Context(dc).DeviceCaps.TryGetValue(index, out int value) ? value : throw new InvalidOperationException($"Device capability {index} is not one this queue describes.");
   }



   /// <summary>Deletes a context this fake created; deleting anything else fails the test.</summary>
   public bool DeleteDC(IntPtr dc)
   {
      Calls.Add($"DeleteDC({Handle(dc)})");
      Assert.True(_contexts.Remove(dc), "A device context was deleted that does not exist.");
      return true;
   }



   /// <summary>Starts a document on the context and returns a new job number, or the failure value a test asked for.</summary>
   public int StartDoc(IntPtr dc, string documentName)
   {
      StaleLastError.Leave();
      Calls.Add($"StartDoc({Handle(dc)}, {documentName})");
      FakeQueue queue = Context(dc);
      if(Failing("StartDoc", out int result))
      {
         return result;
      }
      FakeGdiDocument document = new(queue.Name, documentName, ++_gdiJobs);
      GdiDocuments.Add(document);
      _openGdiDocuments[dc] = document;
      return document.Job;
   }



   /// <summary>Counts a started page.</summary>
   public int StartPage(IntPtr dc)
   {
      StaleLastError.Leave();
      Calls.Add($"StartPage({Handle(dc)})");
      if(Failing("StartPage", out int result))
      {
         return result;
      }
      _openGdiDocuments[dc].PagesStarted++;
      return 1;
   }



   /// <summary>
   /// Accepts a stretch mode and returns the previous one, which is never zero, or the failure value a test asked for. Its
   /// import is declared without SetLastError, so neither success nor failure changes the thread's last error.
   /// </summary>
   public int SetStretchBltMode(IntPtr dc, int mode)
   {
      Calls.Add($"SetStretchBltMode({Handle(dc)}, {mode})");
      return Failing("SetStretchBltMode", out int result, recordsError: false) ? result : 1;
   }



   /// <summary>Keeps the bitmap that was drawn and returns the number of rows, as Windows does.</summary>
   public int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, byte[] bits, GdiBitmapHeader header, uint usage, uint operation)
   {
      StaleLastError.Leave();
      Calls.Add(string.Create(CultureInfo.InvariantCulture, $"StretchDIBits({Handle(dc)}, {x}, {y}, {width}, {height}, {sourceX}, {sourceY}, {sourceWidth}, {sourceHeight}, {bits.Length} bytes, {header.Width}x{header.Height} planes {header.Planes} bits {header.BitCount}, {usage}, 0x{operation:x})"));
      if(Failing("StretchDIBits", out int result))
      {
         return result;
      }
      _openGdiDocuments[dc].Drawn.Add(bits.ToArray());
      return Math.Abs(header.Height);
   }



   /// <summary>Counts an ended page.</summary>
   public int EndPage(IntPtr dc)
   {
      StaleLastError.Leave();
      Calls.Add($"EndPage({Handle(dc)})");
      if(Failing("EndPage", out int result))
      {
         return result;
      }
      _openGdiDocuments[dc].PagesEnded++;
      return 1;
   }



   /// <summary>Ends the open document, or leaves it open when told to fail.</summary>
   public int EndDoc(IntPtr dc)
   {
      StaleLastError.Leave();
      Calls.Add($"EndDoc({Handle(dc)})");
      if(Failing("EndDoc", out int result))
      {
         return result;
      }
      _openGdiDocuments[dc].Ended = true;
      _ = _openGdiDocuments.Remove(dc);
      return 1;
   }



   /// <summary>Cancels the open document.</summary>
   public int AbortDoc(IntPtr dc)
   {
      StaleLastError.Leave();
      Calls.Add($"AbortDoc({Handle(dc)})");
      _openGdiDocuments[dc].Aborted = true;
      _ = _openGdiDocuments.Remove(dc);
      return 1;
   }
}
