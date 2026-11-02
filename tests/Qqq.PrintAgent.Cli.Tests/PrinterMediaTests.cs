/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Tests native-media decisions with synthetic DEVMODE bytes and injected driver/DC results; no test opens a real spool document.
/// </summary>
public sealed class PrinterMediaTests
{
   /// <summary>
   /// Checks 4-by-1, 4-by-2 and 4-by-6 form conversion using queue bytes rather than PDF dimensions or assumptions about loaded labels.
   /// </summary>
   [Theory]
   [InlineData(254, 25400)]
   [InlineData(508, 50800)]
   [InlineData(1524, 152400)]
   public void ConfiguredLabelDimensionsComeFromMachineDevMode(int length, int expectedHeight)
   {
      byte[] mode = Mode((short)length);
      (int width, int height) = WindowsPrinters.ReadExplicitDimensions(mode);
      PrinterMediaProfile profile = Assert.IsType<PrinterMediaProfile>(WindowsPrinters.BuildMediaProfile("fixture", "driver", mode, width, height, 300, 300));
      Assert.Equal(101600, profile.WidthMicrons);
      Assert.Equal(expectedHeight, profile.HeightMicrons);
      Assert.Equal(300, profile.DpiX);
      Assert.Equal(300, profile.DpiY);
      Assert.Equal("WINDOWS_QUEUE_DEFAULT", profile.Source);
      Assert.Equal("DRIVER_DEFAULT", profile.ApplyMode);
   }



   /// <summary>
   /// Ensures incomplete geometry, unknown DPI and out-of-range dimensions cannot produce a selectable profile.
   /// </summary>
   [Fact]
   public void PartialDimensionsAndUnknownDpiCannotAdvertiseSupportedStock()
   {
      byte[] mode = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(72), 5);
      Assert.Equal((0, 0), WindowsPrinters.ReadExplicitDimensions(mode));
      Assert.Null(WindowsPrinters.BuildMediaProfile("fixture", "driver", mode, 101600, 152400, 0, 300));
      Assert.Null(WindowsPrinters.BuildMediaProfile("fixture", "driver", mode, 101600, 152400, 300, -4));
      Assert.Null(WindowsPrinters.BuildMediaProfile("fixture", "driver", mode, 2_000_001, 152400, 300, 300));
   }



   /// <summary>
   /// Checks repeatable fingerprints and invalidation by private defaults or driver identity, including landscape axis resolution.
   /// </summary>
   [Fact]
   public void RevisionIsStableButPrivateConfigurationAndDriverChangesInvalidateIt()
   {
      byte[] mode = Mode(1524);
      PrinterMediaProfile first = Profile(mode);
      Assert.Equal(first, Profile(mode.ToArray()));
      mode[^1] = 1;
      PrinterMediaProfile changed = Profile(mode);
      Assert.Equal(first.Id, changed.Id);
      Assert.NotEqual(first.Revision, changed.Revision, StringComparer.Ordinal);
      Assert.NotEqual(first.Revision, WindowsPrinters.BuildMediaProfile("fixture", "different driver", Mode(1524), 101600, 152400, 300, 300)!.Revision, StringComparer.Ordinal);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(76), 2);
      Assert.Equal(152400, Profile(mode).WidthMicrons);
      Assert.Equal(101600, Profile(mode).HeightMicrons);
   }



   /// <summary>
   /// Reads the actual pinned callback buffer to prove selected bytes are passed intact and the legacy null path remains distinct.
   /// </summary>
   [Fact]
   public void PdfFactoryReceivesTheCapturedDefaultBytesRatherThanCurrentUserDefaults()
   {
      byte[] captured = Mode(508);
      IntPtr result = WindowsPrinters.WithPinnedMode(captured, pointer =>
      {
         Assert.NotEqual(IntPtr.Zero, pointer);
         byte[] actual = new byte[captured.Length];
         Marshal.Copy(pointer, actual, 0, actual.Length);
         Assert.Equal(captured, actual);
         return new IntPtr(42);
      });
      Assert.Equal(new IntPtr(42), result);
      Assert.Equal(IntPtr.Zero, WindowsPrinters.WithPinnedMode(null, pointer => pointer));
   }



   /// <summary>
   /// Rejects invalid orientation and incomplete private settings instead of advertising unverifiable media.
   /// </summary>
   [Fact]
   public void UnknownOrientationAndTruncatedDriverDataDoNotProduceProfiles()
   {
      byte[] mode = Mode(1524);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(76), 0);
      Assert.Null(WindowsPrinters.BuildMediaProfile("fixture", "driver", mode, 101600, 152400, 300, 300));
      Assert.Null(WindowsPrinters.BuildMediaProfile("fixture", "driver", Mode(1524)[..^1], 101600, 152400, 300, 300));
   }



   /// <summary>
   /// Builds a synthetic 300-DPI profile from supplied driver bytes, allowing tests to isolate normalization and fingerprint changes.
   /// </summary>
   private static PrinterMediaProfile Profile(byte[] mode)
      => Assert.IsType<PrinterMediaProfile>(WindowsPrinters.BuildMediaProfile("fixture", "driver", mode, 101600, 152400, 300, 300));



   /// <summary>
   /// Verifies conflicting active driver settings fail before the injected StartDoc callback and leave captured bytes untouched.
   /// </summary>
   [Theory]
   [InlineData(2, 100)]
   [InlineData(1, 50)]
   [InlineData(0, 100)]
   public void DriverCopiesOrScalingCannotMultiplyOrResizeSelectedPdf(int copies, int scale)
   {
      byte[] mode = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(72), 0x11d);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(86), (short)copies);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(84), (short)scale);
      byte[] original = mode.ToArray();
      int submissions = 0;
      Assert.Throws<PrinterMediaMismatchException>(() => WindowsPrinters.StartValidatedPdfDocument(mode, Profile(mode),
         capability => Caps(capability, 1200, 1800, 300, 300), () => ++submissions));
      Assert.Equal(0, submissions);
      Assert.Equal(original, mode);
   }



   /// <summary>
   /// Simulates wrong physical size, orientation, resolution and unknown caps to prove effective-DC mismatches make zero submissions.
   /// </summary>
   [Theory]
   [InlineData(1200, 600, 300, 300)]
   [InlineData(1800, 1200, 300, 300)]
   [InlineData(1200, 1800, 203, 300)]
   [InlineData(1200, 1800, 300, 600)]
   [InlineData(0, 1800, 300, 300)]
   public void RejectedOrSubstitutedDeviceContextNeverStartsDocument(int width, int height, int dpiX, int dpiY)
   {
      byte[] mode = Mode(1524);
      int submissions = 0;
      Assert.Throws<PrinterMediaMismatchException>(() => WindowsPrinters.StartValidatedPdfDocument(mode, Profile(mode),
         capability => Caps(capability, width, height, dpiX, dpiY), () => ++submissions));
      Assert.Equal(0, submissions);
   }



   /// <summary>
   /// Accepts matching portrait/landscape device contexts within one-pixel rounding and invokes the document callback exactly once.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void MatchingEffectiveContextAllowsOneDocumentWithDevicePixelRounding(bool landscape)
   {
      byte[] mode = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(72), 0x11d);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(86), 1);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(84), 100);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(76), (short)(landscape ? 2 : 1));
      int submissions = 0;
      int result = WindowsPrinters.StartValidatedPdfDocument(mode, Profile(mode),
         capability => Caps(capability, landscape ? 1801 : 1201, landscape ? 1200 : 1800, 300, 300), () => ++submissions);
      Assert.Equal(1, result);
      Assert.Equal(1, submissions);
   }



   /// <summary>
   /// Maps only the physical-size and DPI capability indices used by validation, making accidental reliance on other driver values fail the test.
   /// </summary>
   private static int Caps(int capability, int width, int height, int dpiX, int dpiY) => capability switch
   {
      110 => width,
      111 => height,
      88 => dpiX,
      90 => dpiY,
      _ => throw new ArgumentOutOfRangeException(nameof(capability)),
   };



   /// <summary>
   /// Simulates a driver's private copy-count mirror update and proves job-only normalization preserves original queue bytes and revision.
   /// </summary>
   [Fact]
   public void JobOverridesAreMergedByDriverWithoutChangingQueueFingerprintOrPrivateInput()
   {
      byte[] captured = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(72), 0x11d);
      BinaryPrimitives.WriteInt16LittleEndian(captured.AsSpan(86), 3);
      BinaryPrimitives.WriteInt16LittleEndian(captured.AsSpan(84), 50);
      captured[^1] = 3;
      byte[] original = captured.ToArray();
      PrinterMediaProfile selected = Profile(captured);
      int merges = 0;
      byte[] result = WindowsPrinters.NormalizePdfMode(captured, selected, input =>
      {
         merges++;
         Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(86)));
         Assert.Equal(100, BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(84)));
         Assert.Equal(3, input[^1]);
         byte[] merged = input.ToArray();
         merged[^1] = 1; // A driver updates its private copy-count mirror during normalization.
         return merged;
      }, Profile);
      Assert.Equal(1, merges);
      Assert.Equal(1, result[^1]);
      Assert.Equal(original, captured);
      Assert.Equal(selected.Revision, Profile(captured).Revision);
      int submissions = 0;
      Assert.Equal(1, WindowsPrinters.StartValidatedPdfDocument(result, selected,
         capability => Caps(capability, 1200, 1800, 300, 300), () => ++submissions));
   }



   /// <summary>
   /// Simulates ignored overrides, stripped flags and changed geometry/DPI, asserting refusal before the document callback.
   /// </summary>
   [Theory]
   [InlineData("copies")]
   [InlineData("scale")]
   [InlineData("flags")]
   [InlineData("geometry")]
   [InlineData("dpi")]
   public void UnverifiableDriverNormalizationCannotReachSpoolBoundary(string change)
   {
      byte[] captured = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(72), 0x11d);
      int submissions = 0;
      Assert.Throws<PrinterMediaMismatchException>(() =>
      {
         byte[] result = WindowsPrinters.NormalizePdfMode(captured, Profile(captured), input =>
         {
            if(string.Equals(change, "copies", StringComparison.Ordinal))
            { BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(86), 2); }
            if(string.Equals(change, "scale", StringComparison.Ordinal))
            { BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(84), 50); }
            if(string.Equals(change, "flags", StringComparison.Ordinal))
            { BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(72), 13); }
            return input;
         }, output => Profile(output) with
         {
            HeightMicrons = string.Equals(change, "geometry", StringComparison.Ordinal) ? 50800 : 152400,
            DpiX = string.Equals(change, "dpi", StringComparison.Ordinal) ? 203 : 300,
         });
         WindowsPrinters.StartValidatedPdfDocument(result, Profile(captured),
            capability => Caps(capability, 1200, 1800, 300, 300), () => ++submissions);
      });
      Assert.Equal(0, submissions);
   }



   /// <summary>
   /// Covers the observed label-driver field mask so absent native scaling does not exclude a valid selected PDF configuration.
   /// </summary>
   [Fact]
   public void QueueWithoutNativeScaleSupportKeepsThatFieldAbsentAndUsesSoftwareScaling()
   {
      byte[] captured = Mode(1524);
      BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(72), 290573);
      BinaryPrimitives.WriteInt16LittleEndian(captured.AsSpan(86), 3);
      byte[] original = captured.ToArray();
      byte[] normalized = WindowsPrinters.NormalizePdfMode(captured, Profile(captured), input =>
      {
         Assert.Equal(290573u, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(72)));
         Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(84)));
         Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(86)));
         return input;
      }, Profile);
      int submissions = 0;
      Assert.Equal(1, WindowsPrinters.StartValidatedPdfDocument(normalized, Profile(captured),
         capability => Caps(capability, 1200, 1800, 300, 300), () => ++submissions));
      Assert.Equal(original, captured);
   }



   /// <summary>
   /// Creates a bounded synthetic DEVMODE with explicit form fields and private bytes for deterministic tests without native driver calls.
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
