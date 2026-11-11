/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>A loaded PDF: how many pages it has and a way to take one of them out.</summary>
internal interface IPdfDocument
{
   /// <summary>The number of pages in the document.</summary>
   uint PageCount { get; }



   /// <summary>Takes out the page at the given zero-based index. The caller disposes it when the page has been printed.</summary>
   IPdfPage GetPage(uint index);
}
