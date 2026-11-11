/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;
using Windows.Storage.Streams;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A PDF renderer that exists only in memory. It does not interpret the stream it is given, but it reads it, from where
/// it stood when it was handed over to its end, and keeps those bytes, so a test can see what the renderer was given.
/// A "document" is the list of page sizes a test describes, and a rendered page is four marker bytes with the size that
/// was asked for. Every load, page taken, render and page release is written to the call list it shares with
/// <see cref="FakeWindowsPrinting"/>, so a test sees them in order with the Windows calls. Rendering finishes on another
/// thread, as the real renderer does.
/// </summary>
internal sealed class FakePdfRenderer(List<string> calls) : IPdfRenderer
{
   /// <summary>The call list shared with the fake Windows, so the document and page classes below can write to it.</summary>
   private List<string> Calls { get; } = calls;



   /// <summary>The pages of the document, as width and height in units of 1/96 inch; one 4 by 6 inch page unless a test says otherwise.</summary>
   internal List<(double Width, double Height)> Pages { get; } = [(384, 576)];



   /// <summary>When set, loading the document fails with this exception, as a file that is not a PDF does.</summary>
   internal Exception? LoadFailure { get; set; }



   /// <summary>Runs when a page is taken out, with the number of pages taken so far including this one; a test uses it to cancel part way.</summary>
   internal Action<int>? OnGetPage { get; set; }



   /// <summary>When set, the size the renderer reports for its bitmap instead of the size that was asked for.</summary>
   internal (uint Width, uint Height)? ReportedSize { get; set; }



   /// <summary>How many pages were taken out.</summary>
   internal int PagesTaken { get; private set; }



   /// <summary>How many pages were released.</summary>
   internal int PagesReleased { get; private set; }



   /// <summary>The bytes of the stream the renderer was last given, from the position it had when it was handed over to its end; null until a load was asked for.</summary>
   internal byte[]? Handed { get; private set; }



   /// <summary>
   /// Reads the stream from where it stands to its end and keeps the bytes, then hands out the described document or fails
   /// the way a test asked. The stream is left open; it belongs to the caller.
   /// </summary>
   public async Task<IPdfDocument> LoadAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
   {
      Calls.Add("LoadPdf");
      using MemoryStream handed = new();
      await stream.AsStreamForRead().CopyToAsync(handed, cancellationToken);
      Handed = handed.ToArray();
      if(LoadFailure is not null)
      {
         throw LoadFailure;
      }
      return new Document(this);
   }



   /// <summary>The described document.</summary>
   private sealed class Document(FakePdfRenderer renderer) : IPdfDocument
   {
      /// <summary>The number of pages a test described.</summary>
      public uint PageCount => (uint)renderer.Pages.Count;



      /// <summary>Takes out one described page and tells the test's hook.</summary>
      public IPdfPage GetPage(uint index)
      {
         renderer.Calls.Add($"GetPage({index})");
         renderer.PagesTaken++;
         renderer.OnGetPage?.Invoke(renderer.PagesTaken);
         return new Page(renderer, index);
      }
   }



   /// <summary>One described page.</summary>
   private sealed class Page(FakePdfRenderer renderer, uint index) : IPdfPage
   {
      /// <summary>The width a test described.</summary>
      public double Width => renderer.Pages[(int)index].Width;



      /// <summary>The height a test described.</summary>
      public double Height => renderer.Pages[(int)index].Height;



      /// <summary>Returns four marker bytes (the first is the page index) as a bitmap of the asked size, after a thread switch.</summary>
      public async Task<PageBitmap> RenderAsync(uint width, uint height)
      {
         renderer.Calls.Add($"RenderPage({index}, {width}, {height})");
         await Task.Yield();
         (uint reportedWidth, uint reportedHeight) = renderer.ReportedSize ?? (width, height);
         return new PageBitmap([(byte)index, 0xb, 0xc, 0xd], reportedWidth, reportedHeight);
      }



      /// <summary>Records that the page was released.</summary>
      public void Dispose()
      {
         renderer.Calls.Add($"DisposePage({index})");
         renderer.PagesReleased++;
      }
   }
}
