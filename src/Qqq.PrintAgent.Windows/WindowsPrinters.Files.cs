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

public sealed partial class WindowsPrinters
{
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
      RequireJobPaperSupport(job);
      if(job.Options.Copies is < 1 or > 32767 || !printer.Formats.Contains(job.Format, StringComparer.Ordinal))
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
