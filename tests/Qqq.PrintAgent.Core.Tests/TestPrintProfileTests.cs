/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// Guards the test-print request contract with the server: the operator's media choice must reach the wire exactly once and
/// only for test prints, older callers must keep the original request shape, and default selection must stay conservative.
/// </summary>
public sealed class TestPrintProfileTests
{
   /// <summary>
   /// Every offered media profile is serialized into the real test-print request, together with a fresh 32-digit request id the
   /// server uses to deduplicate retries. A profile dropped here would print on whatever stock the printer already holds.
   /// </summary>
   [Theory]
   [InlineData("PDF", "LETTER")]
   [InlineData("PDF", "A4")]
   [InlineData("PDF", "LABEL_4X6_300")]
   [InlineData("ZPL", "LABEL_4X6_203")]
   [InlineData("ZPL", "LABEL_4X6_300")]
   [InlineData("ZPL", "LABEL_4X6_600")]
   [InlineData("PDF", "LABEL_4X2_300")]
   [InlineData("PDF", "LABEL_4X1_300")]
   [InlineData("ZPL", "LABEL_4X2_203")]
   [InlineData("ZPL", "LABEL_4X1_300")]
   [InlineData("ZPL", "LABEL_4X1_600")]
   public async Task SelectedMediaProfile_IsIncludedInActualTestPrintRequest(string format, string profile)
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      string job = await api.OperatorActionAsync("test-print", null, "queue-1", format, false, profile, CancellationToken.None);
      Assert.Equal("new-test-job", job);
      Assert.Equal("/printing/v1/device/test-print", handler.Path);
      JsonElement body = handler.Body;
      Assert.Equal("queue-1", body.GetProperty("printerId").GetString());
      Assert.Equal(format, body.GetProperty("format").GetString());
      Assert.Equal(profile, body.GetProperty("testProfile").GetString());
      Assert.True(Guid.TryParseExact(body.GetProperty("requestId").GetString(), "N", out _));
   }



   /// <summary>
   /// The overload without a profile must send the original four-field body and no null testProfile, because servers that
   /// predate media profiles reject unknown or null members.
   /// </summary>
   [Fact]
   public async Task ExistingCaller_KeepsLegacyShapeWithoutNullTestProfile()
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      await api.OperatorActionAsync("test-print", null, "queue-1", "PDF", false, CancellationToken.None);
      Assert.False(handler.Body.TryGetProperty("testProfile", out _));
      Assert.Equal(4, handler.Body.EnumerateObject().Count());
   }



   /// <summary>
   /// A profile passed alongside a retry or cancel is ignored: those actions act on an existing job and must never carry media
   /// intent, while the duplicate-risk confirmation still has to reach the server.
   /// </summary>
   [Theory]
   [InlineData("retry-job")]
   [InlineData("cancel-job")]
   public async Task Profile_IsNeverSentForOtherOperatorActions(string operation)
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      await api.OperatorActionAsync(operation, "existing-job", null, null, true, "LABEL_4X6_600", CancellationToken.None);
      Assert.False(handler.Body.TryGetProperty("testProfile", out _));
      Assert.True(handler.Body.GetProperty("confirmDuplicateRisk").GetBoolean());
   }



   /// <summary>
   /// A printer that can print labels gets a ZPL test regardless of the order its formats are listed, so a label printer is
   /// never sent a PDF page just because PDF was enumerated first.
   /// </summary>
   [Fact]
   public void LabelCapability_TakesPrecedenceOverPdfEnumerationOrder()
   {
      Assert.Equal("ZPL", TestPrintDefaults.Format(["PDF", "ZPL"]));
      Assert.Equal("ZPL", TestPrintDefaults.Format(["ZPL"]));
      Assert.Equal("PDF", TestPrintDefaults.Format(["PDF"]));
   }



   /// <summary>
   /// The suggested label resolution comes only from an explicit dpi figure in the driver name. A bare model number such as 600
   /// is not a resolution, and guessing wrong would scale a label, so the 203 dpi default applies instead.
   /// </summary>
   [Theory]
   [InlineData("ZPL", "ZDesigner ZT411-300dpi", "LABEL_4X6_300")]
   [InlineData("ZPL", "Thermal 600 DPI", "LABEL_4X6_600")]
   [InlineData("ZPL", "Generic thermal", "LABEL_4X6_203")]
   [InlineData("ZPL", "Model 600 with unknown resolution", "LABEL_4X6_203")]
   [InlineData("PDF", "Laser 600dpi", "LETTER")]
   public void ResolutionSuggestion_UsesExplicitDpiHintsOnly(string format, string driver, string expected)
      => Assert.Equal(expected, TestPrintDefaults.Profile(format, driver));



   /// <summary>
   /// The short label sizes are offered after 4x6 with plain wording, so an operator picks stock by inches and dpi and never sees
   /// an internal profile identifier.
   /// </summary>
   [Fact]
   public void ShortLabelChoices_AreFriendlyAndKeepFourBySixFirst()
   {
      IReadOnlyList<(string Label, string Profile)> zpl = TestPrintDefaults.Choices("ZPL");
      Assert.Equal(
         new[] { "LABEL_4X6_203", "LABEL_4X6_300", "LABEL_4X6_600", "LABEL_4X2_203", "LABEL_4X2_300", "LABEL_4X2_600", "LABEL_4X1_203", "LABEL_4X1_300", "LABEL_4X1_600" },
         zpl.Select(choice => choice.Profile));
      Assert.Equal("4 × 6 in label — 203 dpi", zpl[0].Label);
      Assert.Equal("4 × 1 in label — 600 dpi", zpl[^1].Label);
      IReadOnlyList<(string Label, string Profile)> pdf = TestPrintDefaults.Choices("PDF");
      Assert.Equal(new[] { "LETTER", "A4", "LABEL_4X6_300", "LABEL_4X2_300", "LABEL_4X1_300" }, pdf.Select(choice => choice.Profile));
      Assert.Equal("4 × 6 in label", pdf[2].Label);
      Assert.All(zpl.Concat(pdf), choice => Assert.DoesNotContain("LABEL_", choice.Label, StringComparison.Ordinal));
   }



   /// <summary>The guidance names the stock to load for a label profile and says nothing for paper or no selection.</summary>
   [Theory]
   [InlineData("LABEL_4X6_300", "4 × 6 in")]
   [InlineData("LABEL_4X2_203", "4 × 2 in")]
   [InlineData("LABEL_4X1_600", "4 × 1 in")]
   [InlineData("LETTER", "")]
   [InlineData(null, "")]
   public void LabelStock_NamesTheStockToLoad(string? profile, string expected)
      => Assert.Equal(expected, TestPrintDefaults.LabelStock(profile));



   /// <summary>Records the request path and JSON body of the one call under test and answers with a canned new job id.</summary>
   private sealed class CaptureHandler : HttpMessageHandler
   {
      internal JsonElement Body { get; private set; }
      internal string? Path { get; private set; }



      /// <summary>Clones the parsed body before the request content is disposed, so assertions can read it afterwards.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Path = request.RequestUri!.AbsolutePath;
         using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
         Body = document.RootElement.Clone();
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"jobId\":\"new-test-job\"}") };
      }
   }
}
