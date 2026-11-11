/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One page of a loaded PDF: its size as the document states it, in units of 1/96 inch, and a way to render it to a
/// bitmap of a size the caller chooses. The size comes from the document and is not trusted; the caller checks it.
/// </summary>
internal interface IPdfPage : IDisposable
{
   /// <summary>The page width in units of 1/96 inch, exactly as the document states it.</summary>
   double Width { get; }



   /// <summary>The page height in units of 1/96 inch, exactly as the document states it.</summary>
   double Height { get; }



   /// <summary>Renders the page to a bitmap of the given size in pixels and returns its 32-bit BGRA pixels, first row at the top, with the size the renderer actually produced.</summary>
   Task<PageBitmap> RenderAsync(uint width, uint height);
}
