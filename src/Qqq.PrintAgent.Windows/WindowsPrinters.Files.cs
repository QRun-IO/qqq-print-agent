/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Qqq.PrintAgent.Core.Printing;
using Windows.Data.Pdf;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Staged-file part of the Windows adapter: printing a document that was already downloaded and verified into the protected staging
/// folder, so large documents never have to be held in memory and are re-verified immediately before they are spooled.
/// </summary>
public sealed partial class WindowsPrinters
{
   /// <summary>
   /// Prints a staged file. The path must be an absolute, regular file (no link), and its length and SHA-256 are checked against the job
   /// again here, because it is the last step before the spooler. The queue and options are then validated (copies, no per-job paper
   /// dimensions, a format the queue offers), and the document goes to the RAW ZPL or PDF path.
   /// </summary>
   public async Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken)
   {
      if(!Path.IsPathFullyQualified(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
      {
         throw new InvalidDataException("The worker requires a regular, absolute staging file.");
      }
      using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      if(input.Length != job.ContentLength || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)), job.ContentSha256, StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidDataException("Staged document integrity check failed.");
      }
      input.Position = 0;
      PrinterInfo printer = Discover().Single(item => string.Equals(item.PrinterId, job.PrinterId, StringComparison.Ordinal));
      if(job.Options.Copies is < 1 or > 32767 || job.Options.PaperWidthMm is not null || job.Options.PaperHeightMm is not null
         || !printer.Formats.Contains(job.Format, StringComparer.Ordinal))
      {
         throw new InvalidDataException("The queue or print options are unsupported.");
      }
      if(string.Equals(job.Format, "ZPL", StringComparison.Ordinal))
      {
         return PrintRawStream(printer, job, input, cancellationToken);
      }
      using global::Windows.Storage.Streams.IRandomAccessStream random = input.AsRandomAccessStream();
      PdfDocument document = await PdfDocument.LoadFromStreamAsync(random).AsTask(cancellationToken).ConfigureAwait(false);
      return await PrintPdfDocumentAsync(printer, job, document, cancellationToken).ConfigureAwait(false);
   }



   /// <summary>Spools a staged file as one RAW document, rechecking the selected media just before it starts, and aborts the document if anything fails after it began so no partial label stays queued.</summary>
   private static string PrintRawStream(PrinterInfo selectedPrinter, PrintJob job, Stream input, CancellationToken token)
   {
      Check(Native.OpenPrinter(selectedPrinter.Name, out IntPtr printer, IntPtr.Zero));
      try
      {
         _ = RequireCurrentMedia(selectedPrinter, job);
         Native.DocInfo1 info = new() { DocumentName = $"QQQ {job.JobId}", DataType = "RAW" };
         uint id = Native.StartDocPrinter(printer, 1, ref info);
         if(id == 0)
         { throw new Win32Exception(Marshal.GetLastWin32Error()); }
         try
         {
            WriteRawCopies(printer, job.Options.Copies, input, token);
            Check(Native.EndDocPrinter(printer));
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
         }
         catch { Native.AbortPrinter(printer); throw; }
      }
      finally { Native.ClosePrinter(printer); }
   }



   /// <summary>Writes the staged bytes once per copy in 64 KiB pieces, continuing after partial spooler writes and refusing an impossible byte count, with cancellation checked between writes.</summary>
   private static void WriteRawCopies(IntPtr printer, int copies, Stream input, CancellationToken token)
   {
      byte[] buffer = new byte[65536];
      for(int copy = 0; copy < copies; copy++)
      {
         input.Position = 0;
         int count;
         while((count = input.Read(buffer, 0, buffer.Length)) != 0)
         {
            int offset = 0;
            while(offset < count)
            {
               token.ThrowIfCancellationRequested();
               byte[] remaining = buffer.AsSpan(offset, count - offset).ToArray();
               Check(Native.WritePrinter(printer, remaining, remaining.Length, out int written));
               if(written <= 0 || written > remaining.Length)
               { throw new IOException("Printer returned an invalid byte count."); }
               offset += written;
            }
         }
      }
   }
}
