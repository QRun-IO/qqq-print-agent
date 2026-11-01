/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers.Binary;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Covers explicit actual-size job paper for short labels with synthetic DEVMODE bytes and injected device-context sizes; no spool document is opened.
/// </summary>
public sealed class JobPaperTests
{
   /// <summary>
   /// Converts the exact millimeter values the server sends for 4 × 1 and 4 × 2 inch stock, and leaves ordinary options on the queue default.
   /// </summary>
   [Theory]
   [InlineData(101.6, 25.4, 101600, 25400)]
   [InlineData(101.6, 50.8, 101600, 50800)]
   public void ExactShortLabelMillimetersBecomeMicrons(double width, double height, int widthMicrons, int heightMicrons)
   {
      Assert.Equal<(int, int)?>((widthMicrons, heightMicrons), new PrintOptions(PaperWidthMm: width, PaperHeightMm: height).CustomPaperMicrons());
      Assert.Null(new PrintOptions().CustomPaperMicrons());
   }



   /// <summary>
   /// Refuses partial, non-positive, non-finite, oversized and media-profile-conflicting paper before any native printer call.
   /// </summary>
   [Fact]
   public void MalformedOrConflictingPaperIsRejected()
   {
      PrinterMediaSelection selection = new("default", new string('a', 64));
      PrintOptions[] invalid =
      [
         new(PaperWidthMm: 101.6),
         new(PaperHeightMm: 25.4),
         new(PaperWidthMm: 0, PaperHeightMm: 25.4),
         new(PaperWidthMm: 101.6, PaperHeightMm: -25.4),
         new(PaperWidthMm: double.NaN, PaperHeightMm: 25.4),
         new(PaperWidthMm: 101.6, PaperHeightMm: double.PositiveInfinity),
         new(PaperWidthMm: 101.6, PaperHeightMm: 2000.1),
         new(PaperWidthMm: 101.6, PaperHeightMm: 25.4, MediaProfile: selection),
      ];
      foreach(PrintOptions options in invalid)
      {
         Assert.Throws<InvalidDataException>(() => { _ = options.CustomPaperMicrons(); });
      }
   }



   /// <summary>
   /// Allows job paper only for PDF; ZPL carries its own dimensions and is unchanged without paper options.
   /// </summary>
   [Fact]
   public void JobPaperAppliesToPdfOnly()
   {
      PrintOptions paper = new(PaperWidthMm: 101.6, PaperHeightMm: 25.4);
      WindowsPrinters.RequireJobPaperSupport(Job("PDF", paper));
      WindowsPrinters.RequireJobPaperSupport(Job("ZPL", new PrintOptions()));
      Assert.Throws<InvalidDataException>(() => WindowsPrinters.RequireJobPaperSupport(Job("ZPL", paper)));
   }



   /// <summary>
   /// Applies explicit paper and the copy/scale guards to a clone, drops the stored form name, and leaves the captured queue bytes untouched.
   /// </summary>
   [Fact]
   public void ExplicitPaperIsAppliedToACloneOfTheQueueDefaults()
   {
      byte[] captured = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(72), 0x1011d);
      BinaryPrimitives.WriteInt16LittleEndian(captured.AsSpan(86), 3);
      BinaryPrimitives.WriteInt16LittleEndian(captured.AsSpan(84), 50);
      byte[] original = captured.ToArray();
      int merges = 0;
      byte[] result = WindowsPrinters.BuildCustomPaperMode(captured, (101600, 25400), input =>
      {
         merges++;
         return input;
      });
      Assert.Equal(1, merges);
      Assert.Equal(original, captured);
      Assert.Equal(0x1u | 0x2 | 0x4 | 0x8 | 0x10 | 0x100, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(72)));
      Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(76)));
      Assert.Equal(256, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(78)));
      Assert.Equal(254, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(80)));
      Assert.Equal(1016, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(82)));
      Assert.Equal(100, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(84)));
      Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(86)));
      Assert.Equal(captured.Length, result.Length);
   }



   /// <summary>
   /// A driver that ignores the one-copy override cannot reach the spool boundary.
   /// </summary>
   [Fact]
   public void DriverThatRestoresCopiesIsRefused()
   {
      byte[] captured = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(72), 0x11d);
      Assert.Throws<PrinterMediaMismatchException>(() => WindowsPrinters.BuildCustomPaperMode(captured, (101600, 50800), input =>
      {
         byte[] changed = input.ToArray();
         BinaryPrimitives.WriteInt16LittleEndian(changed.AsSpan(86), 2);
         return changed;
      }));
   }



   /// <summary>
   /// The job's device context must report the requested physical label; a driver that keeps its 4 × 6 default fails before StartDoc.
   /// </summary>
   [Theory]
   [InlineData(25400, 1200, 300, 300, true)]
   [InlineData(50800, 1200, 600, 300, true)]
   [InlineData(25400, 1200, 1800, 300, false)]
   [InlineData(50800, 1200, 1800, 300, false)]
   [InlineData(25400, 1200, 300, 203, false)]
   public void DeviceContextMustMatchRequestedLabelBeforeSpooling(int heightMicrons, int width, int height, int dpi, bool matches)
   {
      byte[] mode = WindowsPrinters.BuildCustomPaperMode(Mode(1524), (101600, heightMicrons), input => input);
      PrinterMediaProfile expected = new("job-paper", "job-paper", "Job paper", 101600, heightMicrons, 300, 300);
      int submissions = 0;
      Func<int> start = () => ++submissions;
      Func<int, int> caps = capability => capability switch
      {
         110 => width,
         111 => height,
         88 or 90 => dpi,
         _ => throw new ArgumentOutOfRangeException(nameof(capability)),
      };
      if(matches)
      {
         Assert.Equal(1, WindowsPrinters.StartValidatedPdfDocument(mode, expected, caps, start));
         Assert.Equal(1, submissions);
         return;
      }
      Assert.Throws<PrinterMediaMismatchException>(() => WindowsPrinters.StartValidatedPdfDocument(mode, expected, caps, start));
      Assert.Equal(0, submissions);
   }



   /// <summary>
   /// Builds a minimal job record whose only relevant inputs are format and options.
   /// </summary>
   private static PrintJob Job(string format, PrintOptions options)
      => new("job", "printer", "title", format, new string('0', 64), 1, options, "2026-11-01T00:00:00Z", "QUEUED");



   /// <summary>
   /// Creates a bounded synthetic DEVMODE with explicit 4-inch-wide form fields for deterministic tests without native driver calls.
   /// </summary>
   private static byte[] Mode(short height)
   {
      byte[] mode = new byte[224];
      BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(68), 220);
      BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(70), 4);
      BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(72), 13);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(76), 1);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(80), height);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(82), 1016);
      return mode;
   }
}
