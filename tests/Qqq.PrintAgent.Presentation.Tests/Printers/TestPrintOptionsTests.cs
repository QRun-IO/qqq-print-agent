/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Printers;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Printers;

/// <summary>The choices, preselections and guidance of the test-print dialog.</summary>
public sealed class TestPrintOptionsTests
{
   /// <summary>The opening sentence names the server and the printer and says the test uses real stock and needs sharing and approval.</summary>
   [Fact]
   public void Introduction_NamesServerAndPrinter()
      => Assert.Equal("Print one high-contrast Forge diagnostic page or label on Dock 4 labels. This uses real stock and requires local sharing and server approval.", TestPrintOptions.Introduction("Forge", "Dock 4 labels"));



   /// <summary>A label printer offers both formats by their friendly names and preselects ZPL with the 4 × 6 label at the resolution its driver name suggests.</summary>
   [Fact]
   public void LabelPrinter_OffersBothFormatsAndPrefersZplAtTheDriverResolution()
   {
      PrinterSnapshot printer = TestData.Printer(formats: ["PDF", "ZPL"]);

      Assert.Equal([new TestPrintChoice("Document (PDF)", "PDF"), new TestPrintChoice("Printer-ready label (ZPL)", "ZPL")], TestPrintOptions.Formats(printer));
      Assert.Equal("ZPL", TestPrintOptions.PreferredFormat(printer));
      Assert.Equal("LABEL_4X6_300", TestPrintOptions.PreferredProfile("ZPL", "ZDesigner ZT411-300dpi ZPL"));
      Assert.Equal("Label stock and resolution", TestPrintOptions.ProfileHeader("ZPL"));
      Assert.Equal(9, TestPrintOptions.Profiles("ZPL").Count);
      Assert.Equal(new TestPrintChoice("4 × 6 in label — 203 dpi", "LABEL_4X6_203"), TestPrintOptions.Profiles("ZPL")[0]);
   }



   /// <summary>A document printer offers PDF only, preselects Letter, and offers Letter, A4 and the three label sizes.</summary>
   [Fact]
   public void DocumentPrinter_OffersPdfWithPaperAndLabelSizes()
   {
      PrinterSnapshot printer = TestData.Printer(formats: ["PDF"]);

      Assert.Equal("PDF", TestPrintOptions.PreferredFormat(printer));
      Assert.Equal("LETTER", TestPrintOptions.PreferredProfile("PDF", driverName: null));
      Assert.Equal("Paper or label stock", TestPrintOptions.ProfileHeader("PDF"));
      Assert.Equal("LETTER,A4,LABEL_4X6_300,LABEL_4X2_300,LABEL_4X1_300", string.Join(',', TestPrintOptions.Profiles("PDF").Select(choice => choice.Value)));
      Assert.Equal("US Letter — 8.5 × 11 in", TestPrintOptions.Profiles("PDF")[0].Label);
   }



   /// <summary>With nothing selected in the format box the dialog works with PDF; a selection is used as it is.</summary>
   [Fact]
   public void MissingFormatSelection_MeansPdf()
   {
      Assert.Equal("PDF", TestPrintOptions.FormatOrDefault(null));
      Assert.Equal("ZPL", TestPrintOptions.FormatOrDefault("ZPL"));
   }



   /// <summary>ZPL guidance names the label size to load (or "matching" when the profile is unknown) and insists on checking the real resolution.</summary>
   [Theory]
   [InlineData("LABEL_4X2_203", "Load 4 × 2 in labels and verify")]
   [InlineData(null, "Load matching labels and verify")]
   public void ZplGuidance_NamesTheStockAndTheResolutionCheck(string? profile, string opening)
   {
      string guidance = TestPrintOptions.Guidance("ZPL", profile);

      Assert.StartsWith(opening, guidance, StringComparison.Ordinal);
      Assert.EndsWith("a wrong DPI changes the printed size. This diagnostic does not calibrate the printer or save device configuration.", guidance, StringComparison.Ordinal);
   }



   /// <summary>A short PDF label says it carries its own actual-size paper and leaves the queue default alone; Letter, A4, a 4 × 6 PDF label and no selection get the general "match the queue" guidance.</summary>
   [Theory]
   [InlineData("LABEL_4X1_300", "Load 4 × 1 in labels first. This job carries its own actual-size 4 × 1 in paper, so the Windows queue default (for example 4 × 6 in) is not changed and nothing is printed until you continue. The diagnostic prints one copy.")]
   [InlineData("LABEL_4X6_300", "Load stock matching the selected size and set the Windows queue to the same paper or label size. The diagnostic prints one copy at actual size; the stock selection does not change Windows printer defaults.")]
   [InlineData("A4", "Load stock matching the selected size and set the Windows queue to the same paper or label size. The diagnostic prints one copy at actual size; the stock selection does not change Windows printer defaults.")]
   [InlineData(null, "Load stock matching the selected size and set the Windows queue to the same paper or label size. The diagnostic prints one copy at actual size; the stock selection does not change Windows printer defaults.")]
   public void PdfGuidance_DependsOnWhetherTheJobCarriesItsOwnPaper(string? profile, string expected)
      => Assert.Equal(expected, TestPrintOptions.Guidance("PDF", profile));
}
