/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows PDF renderer behind <see cref="IPdfRenderer"/>: it loads a document with Windows.Data.Pdf, hands out
/// its pages, and renders a page to pixels through the Windows image decoder. It decides nothing about printing.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Calls the Windows Runtime PDF renderer (Windows.Data.Pdf) and image decoder (Windows.Graphics.Imaging). These are operating system components: what they return depends on the Windows edition, its installed features and the display scale of the session (a request for 600 by 900 pixels came back as 750 by 1125 on a desktop at 125 percent), so a test that ran them would not be hermetic. No rule lives here: the check of each page's size, the bound on the rendered bitmap, the size and position of the page on the paper, the scaling choice and the copy and page loops are decided in WindowsPrinters, which is tested with a fake of IPdfRenderer. The pixel format asked of the decoder (32-bit BGRA, alpha ignored, no orientation or colour management) is fixed here because it describes the bytes this class returns.")]
internal sealed class WindowsPdfRenderer : IPdfRenderer
{
   /// <summary>Loads the document from the stream with the Windows PDF renderer; a file that is not a readable PDF fails here.</summary>
   public async Task<IPdfDocument> LoadAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
      => new Document(await PdfDocument.LoadFromStreamAsync(stream).AsTask(cancellationToken).ConfigureAwait(false));



   /// <summary>A document loaded by the Windows PDF renderer.</summary>
   private sealed class Document(PdfDocument document) : IPdfDocument
   {
      /// <summary>The page count the renderer reports.</summary>
      public uint PageCount => document.PageCount;



      /// <summary>Takes the page out of the renderer's document.</summary>
      public IPdfPage GetPage(uint index) => new Page(document.GetPage(index));
   }



   /// <summary>A page of a document loaded by the Windows PDF renderer.</summary>
   private sealed class Page(PdfPage page) : IPdfPage
   {
      /// <summary>The width the renderer reports for the page.</summary>
      public double Width => page.Size.Width;



      /// <summary>The height the renderer reports for the page.</summary>
      public double Height => page.Size.Height;



      /// <summary>Renders the page into an in-memory image of the given size and decodes it to 32-bit BGRA pixels.</summary>
      public async Task<PageBitmap> RenderAsync(uint width, uint height)
      {
         using InMemoryRandomAccessStream rendered = new();
         await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
         {
            DestinationWidth = width,
            DestinationHeight = height,
         });
         rendered.Seek(0);
         BitmapDecoder decoder = await BitmapDecoder.CreateAsync(rendered);
         PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
         return new PageBitmap(pixels.DetachPixelData(), decoder.PixelWidth, decoder.PixelHeight);
      }



      /// <summary>Releases the renderer's page.</summary>
      public void Dispose() => page.Dispose();
   }
}
