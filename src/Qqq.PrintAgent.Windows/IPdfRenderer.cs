/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Windows.Storage.Streams;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows PDF renderer as the printing rules use it: open a document, then hand out its pages one at a time. An
/// implementation only loads and renders; which pages are printed, how often, at what size and where on the paper is
/// decided by <see cref="WindowsPrinters"/>.
/// </summary>
internal interface IPdfRenderer
{
   /// <summary>
   /// Loads a PDF from the given stream. The document keeps reading from that stream while its pages are rendered, so
   /// the caller keeps the stream open until it is done with the document.
   /// </summary>
   Task<IPdfDocument> LoadAsync(IRandomAccessStream stream, CancellationToken cancellationToken);
}
